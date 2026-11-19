using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using AdminCore.Core.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AdminCore.Core.EventBus
{
    /// <summary>
    /// 事件发布器(契约):发布即入队,由后台消费者异步执行订阅,主流程不等待订阅执行(异步解耦)。
    /// </summary>
    public interface IEventPublisher
    {
        /// <summary>
        /// 发布事件:payload 入队即返回。本方法返回的 Task 在「入队完成」时完成(满则短暂等待背压),
        /// 「不」在订阅执行/落库完成时完成——落库由后台消费者异步完成,主流程(请求链路)不等待。
        /// </summary>
        Task PublishAsync<T>(string eventName, T payload);
    }

    /// <summary>
    /// 事件订阅者标记接口:宿主类实现本接口并以其方法贴 <see cref="EventSubscribeAttribute"/>,
    /// 标识其为「可 DI 解析 + 声明式订阅」的订阅者(供总线按具体类型从作用域解析并做防御校验)。
    /// </summary>
    public interface IEventSubscriber
    {
    }

    /// <summary>
    /// 声明式订阅特性(契约 §5.9):标注在订阅方法上,启动时反射扫描自动登记,新增订阅无需手写注册代码。
    /// Inherited=false:订阅语义逐方法显式标注,不随继承传播。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public class EventSubscribeAttribute : Attribute
    {
        /// <summary>订阅的事件名(与 <see cref="IEventPublisher.PublishAsync{T}"/> 的 eventName 精确匹配)。</summary>
        public string Name { get; }

        /// <summary>
        /// 最大重试次数(批 S 5.8 精细重试,参照 Admin.NET 同名口径;-1=回落全局缺省 ≤2 次)。
        /// 0=不重试(首次失败即终态 Error)。
        /// </summary>
        public int NumRetries { get; set; } = -1;

        /// <summary>重试间隔毫秒(批 S 5.8;-1=回落全局缺省 1000ms)。</summary>
        public int RetryTimeout { get; set; } = -1;

        /// <summary>
        /// 触发重试的异常类型白名单(批 S 5.8):空(缺省)=任意异常都触发重试;
        /// 非空=仅列出的类型(含子类)触发重试,其余异常首次失败即终态(不可重试异常不空耗重试)。
        /// </summary>
        public Type[] ExceptionTypes { get; set; } = Array.Empty<Type>();

        public EventSubscribeAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("事件名不能为空", nameof(name));
            }
            Name = name;
        }
    }

    /// <summary>
    /// 抑制事件执行审计(批 S 5.8,参照 Admin.NET SuppressMonitor 同口径):
    /// 标注在 [EventSubscribe] 订阅方法上,该订阅的执行不写 sys_log_event 审计行(订阅照常执行)。
    /// 用途:高频/低价值事件的审计降噪。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public class SuppressMonitorAttribute : Attribute
    {
    }

    /// <summary>
    /// 轻量内存事件总线(契约 §5.6 单机版,Redis 版留作集群扩展):
    /// 有界 Channel(容量 1000) + 单后台消费者 + 启动时反射扫描 [EventSubscribe] + 订阅失败重试≤2(间隔 1s)。
    /// </summary>
    /// <remarks>
    /// 生命周期:实现 <see cref="BackgroundService"/>,由宿主(IHostedService)启动/停止消费者循环。
    /// 有界满则「等待背压」而非丢弃:操作日志等审计数据不可静默丢失;背压仅短暂阻塞入队方,不丢数据。
    /// 作用域隔离:本类为单例(后台消费者常驻),不直接持有 Scoped 依赖;消费每事件临时建作用域解析订阅者,
    /// 规避「单例吃不到 Scoped」的经典陷阱(手册 §5.9)。
    /// 失败不阻断:订阅执行失败重试耗尽仅记 ILogger 告警,绝不上抛——事件消费失败不影响主流程/队列推进。
    /// 注册(§99③ 开发与注册分离,零改 Program.cs):贴 <see cref="ServiceAttribute"/> 显式锁定 ServiceType=IEventPublisher +
    /// Singleton——本类同时实现 IEventPublisher 与 IHostedService(经 BackgroundService)两个公共接口,省略 ServiceType
    /// 会触发自动匹配歧义(拒绝静默误注册);宿主侧 IHostedService→实现 的补注册由 AddAutoServices 的 HostedService 分支完成
    /// (见 ServiceCollectionExtensions)——该分支用「工厂经主服务类型 IEventPublisher 解析」而非直接 IHostedService→实现,
    /// 使两处命中同一单例缓存条目、共享同一实例(.NET DI 按 (ServiceType,ImplementationType) 缓存,直接双注册会分裂成两个实例)。
    /// </remarks>
    [Service(ServiceType = typeof(IEventPublisher), Lifetime = ServiceLifetime.Singleton)]
    public class EventBus : BackgroundService, IEventPublisher
    {
        // 重试语义(契约 §5.8 简化):首次执行之外最多重试 2 次,每次间隔 1s,共最多 3 次尝试;仍失败记告警。
        private const int MaxRetries = 2;
        private const int RetryDelayMs = 1000;
        // 有界容量(契约):单机内存够用的保守上限;满则等待背压。
        private const int ChannelCapacity = 1000;

        private readonly Channel<EventMessage> _channel;
        private readonly IServiceProvider _rootProvider;
        private readonly ILogger<EventBus> _logger;
        // 事件执行器(批 S 5.8):重试策略/执行审计的扩展点;DI 解析默认 RetryEventHandlerExecutor
        private readonly IEventHandlerExecutor _executor;

        // ── 跨进程双通道(批 AC 5.6:Redis Pub/Sub,集群扩展)────────────────────────────
        // 开关 EventBus:EnableRedis(缺省 false=纯本地内存通道,既有行为零变化);通道名 EventBus:Channel。
        // 语义:本地订阅照旧(本进程内即时执行),跨实例投递经 Redis 广播——
        //   ① 发布侧:PublishAsync 本地入队后额外 PUBLISH 一份(失败只告警,本地执行不受影响);
        //   ② 接收侧:本实例经 Redis 订阅收其它实例的事件 → 投本地队列按本地订阅者执行;
        //   ③ 自回环:消息带 Origin 实例标识,收到自己的消息直接丢弃(本进程已本地执行过,否则重复触发)。
        // WHY 用 Redis 而非消息队列:本仓已引 StackExchange.Redis(缓存),零新依赖(#11 复用优先);
        // Pub/Sub 无持久化(离线实例丢消息)属集群扩展期可接受口径,持久化投递(Stream/RabbitMQ)按需另立。
        private const string DefaultRedisChannel = "admincore:eventbus";
        private const string EnableRedisConfigKey = "EventBus:EnableRedis";
        private const string RedisChannelConfigKey = "EventBus:Channel";
        private const string CrossInstanceEventsConfigKey = "EventBus:CrossInstanceEvents";

        /// <summary>跨实例消息 JSON 口径(与全仓 camelCase + 中文不转义一致;JsonSerializerOptions 线程安全可共享)。</summary>
        private static readonly JsonSerializerOptions EventJsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IConnectionMultiplexer? _multiplexer;
        // 非 readonly:订阅失败时降级关闭(见 SubscribeRedis)
        private bool _redisEnabled;
        private readonly string _redisChannel;

        /// <summary>
        /// 跨实例事件白名单(批 AC:配置 <c>EventBus:CrossInstanceEvents</c>,逗号分隔事件名;缺省空=无事件跨实例)。
        /// </summary>
        /// <remarks>
        /// WHY 必须白名单而非"开启双通道即全部广播":本仓内建订阅者(OpLog/ExLog/MsgLog)语义是
        /// **本实例写本实例的库**(审计行归属"谁处理的请求"),若把 op_log/ex_log/msg_log 广播到其它实例,
        /// 每个实例都会执行一遍订阅 → 审计行重复落库(开启即出脏数据)。故跨实例投递只对**显式登记**的事件生效,
        /// 典型用途=缓存失效/配置变更/在线用户跨实例通知等"需要其它实例也动作"的事件;
        /// 白名单为空时双通道等价纯本地(零行为风险),业务按需登记(约定 #8 最小必要)。
        /// </remarks>
        private readonly HashSet<string> _crossInstanceEvents;
        /// <summary>本实例标识(自回环判据:收到带本实例 Origin 的消息=自己发布的,丢弃)。</summary>
        private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

        /// <summary>当前 Redis 订阅处理器引用(退订时带 handler 传回,只摘本实例订阅;见 SubscribeRedis WHY)。</summary>
        private Action<RedisChannel, RedisValue>? _redisHandler;

        // 事件名 → 订阅处理器列表(构造期反射扫描填充;读取期加锁,防御运行期变更)
        private readonly Dictionary<string, List<SubscriberHandler>> _handlers = new();
        private readonly object _scanLock = new();

        /// <summary>
        /// DI 构造入口(<see cref="ActivatorUtilitiesConstructorAttribute"/> 标注,供 [Service] 自动注册时由容器调用):
        /// 缺省仅扫描本类所在程序集(Core)。订阅者按任务限域一律落 Core/Services/Log/,单程序集即覆盖全部订阅;
        /// 跨程序集扫描(如测试程序集/未来集群扩展)走显式 <see cref="Assembly[]"/> 构造,不在 DI 入口硬编码。
        /// </summary>
        /// <remarks>
        /// config/multiplexer 为**可选参数**(批 AC):未注册(纯内存宿主/既有测试容器)→ 默认 null,
        /// 双通道视为关闭,行为与批 AC 前完全一致(批 X 仓储 IConfiguration 可选参数同先例)。
        /// </remarks>
        [ActivatorUtilitiesConstructor]
        public EventBus(IServiceProvider rootProvider, ILogger<EventBus> logger, IEventHandlerExecutor executor,
            IConfiguration? config = null, IConnectionMultiplexer? multiplexer = null)
            : this(rootProvider, logger, executor, new[] { typeof(EventBus).Assembly }, config, multiplexer)
        {
        }

        /// <summary>
        /// 构造:建 Channel(有界)并扫描 assemblies 中全部 [EventSubscribe] 方法登记订阅。
        /// </summary>
        /// <param name="rootProvider">根服务提供器,消费时据此建作用域解析订阅者。</param>
        /// <param name="logger">日志(告警/兜底)。</param>
        /// <param name="assemblies">待反射扫描的程序集(如 Core + Web.Core)。</param>
        /// <param name="config">配置(读 EventBus:EnableRedis / EventBus:Channel;null=双通道关闭)。</param>
        /// <param name="multiplexer">Redis 多路复用器(缓存同源单例;null=双通道不可用)。</param>
        public EventBus(IServiceProvider rootProvider, ILogger<EventBus> logger, IEventHandlerExecutor executor, Assembly[] assemblies,
            IConfiguration? config = null, IConnectionMultiplexer? multiplexer = null)
        {
            _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));

            // 有界 Channel:容量 1000,FullMode=Wait(满则等待背压)。
            // SingleReader=true(单消费者循环)、SingleWriter=false(多发布者并发入队);
            // AllowSynchronousContinuations=false:入队唤醒读者不回到当前线程,规避重入。
            _channel = Channel.CreateBounded<EventMessage>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            // 双通道装配(开关 + 通道名;开关开但无 Redis 连接 → 降级纯本地并告警,不阻断启动)
            _multiplexer = multiplexer;
            _redisEnabled = config?.GetValue(EnableRedisConfigKey, false) ?? false;
            _redisChannel = config?[RedisChannelConfigKey] is { Length: > 0 } channel ? channel : DefaultRedisChannel;
            _crossInstanceEvents = new HashSet<string>(
                (config?[CrossInstanceEventsConfigKey] ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
            if (_redisEnabled && _multiplexer is null)
            {
                _redisEnabled = false;
                _logger.LogWarning("事件总线双通道已配置开启({Key}=true),但容器无 Redis 连接(未注册 IConnectionMultiplexer)→ 降级纯本地通道",
                    EnableRedisConfigKey);
            }

            // 批 AU/AU 泛化(配置面 fail-loud):**内建跨实例事件**靠白名单准入——开了双通道却没登记某事件时,
            // 现象是"接口 200 但用户在别的实例上收不到"(静默失效,最难查)⇒ 启动时**逐项**响亮提示 + 动作指引。
            // 清单＝EventBusEvents.CrossInstanceBuiltIns(守卫与文档同源:新增内建事件只改清单 + 加订阅者)。
            // 未开双通道(单实例部署)不打扰。
            if (_redisEnabled)
            {
                foreach (var (eventName, label) in EventBusEvents.CrossInstanceBuiltIns)
                {
                    if (_crossInstanceEvents.Contains(eventName))
                    {
                        continue;
                    }

                    _logger.LogWarning(
                        "跨实例{Label}未启用:{WhitelistKey} 未包含 {Event}——集群下在 A 实例发起的动作将无法送达连到 B 实例的用户;" +
                        "登记该事件名后重启即生效(单实例部署可忽略本条)",
                        label, CrossInstanceEventsConfigKey, eventName);
                }
            }

            // 构造期完成订阅扫描(消费者尚未启动,单线程填充);扫描异常不阻断启动(记录后以已扫描部分运行)。
            ScanSubscribers(assemblies);
        }

        /// <summary>发布事件:入队即返回(满则短暂等待背压),不等待订阅执行。</summary>
        /// <remarks>
        /// 双通道开启时(批 AC 5.6)本地入队后额外向 Redis 广播一份供其它实例消费;
        /// 广播失败只告警——跨实例投递不可用不得影响本进程内的事件语义(本地订阅照常执行)。
        /// </remarks>
        public async Task PublishAsync<T>(string eventName, T payload)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("事件名不能为空", nameof(eventName));
            }

            var eventId = Guid.NewGuid().ToString("N");
            // Channel.Writer.WriteAsync 返回 ValueTask,本方法契约返回 Task——显式 await 完成 ValueTask→Task 适配
            await _channel.Writer.WriteAsync(new EventMessage(eventName, eventId, payload));

            await PublishToRedisAsync(eventName, eventId, payload).ConfigureAwait(false);
        }

        /// <summary>跨实例广播(批 AC 5.6):包 JSON 信封(事件名/事件 Id/来源实例/载荷)后 PUBLISH;失败只告警。</summary>
        /// <remarks>仅白名单内事件跨实例(见 _crossInstanceEvents WHY:防审计类事件重复落库)。</remarks>
        private async Task PublishToRedisAsync<T>(string eventName, string eventId, T payload)
        {
            if (!_redisEnabled || _multiplexer is null || !_crossInstanceEvents.Contains(eventName))
            {
                return;
            }

            try
            {
                var envelope = JsonSerializer.Serialize(
                    new RedisEnvelope(eventName, eventId, _instanceId, JsonSerializer.Serialize(payload, EventJsonOpts)),
                    EventJsonOpts);
                await _multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(_redisChannel), envelope);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "事件 {Event} 跨实例广播失败(本进程内执行不受影响)", eventName);
            }
        }

        /// <summary>后台消费者主循环:逐条读取 Channel 并分发;单条消费异常兜底隔离,绝不上抛(BackgroundService 上抛=宿主崩溃)。</summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 跨实例订阅(批 AC 5.6):先挂 Redis 订阅再进消费循环(消费者就绪前收到的跨实例事件在 Channel 内排队)
            SubscribeRedis();

            _logger.LogInformation("事件总线后台消费者启动(有界容量 {Capacity},满则等待背压,重试≤{Retries} 间隔 {Delay}ms,双通道 {Dual})",
                ChannelCapacity, MaxRetries, RetryDelayMs, _redisEnabled ? $"开(通道 {_redisChannel})" : "关");

            try
            {
                await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await DispatchAsync(message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 兜底:分发层未捕获异常在此隔离,保证队列持续推进(消费失败不影响后续事件)
                        _logger.LogError(ex, "事件 {Event} 分发兜底异常(已隔离,不影响队列)", message.EventName);
                    }
                }
            }
            finally
            {
                UnsubscribeRedis();
                _logger.LogInformation("事件总线后台消费者停止");
            }
        }

        /// <summary>
        /// 挂 Redis 订阅(批 AC 5.6):收其它实例广播的事件 → 投本地队列 → 按本地订阅者执行。
        /// 订阅失败 → 降级纯本地并记错误(集群扩展失效不阻断宿主启动,同"事件消费失败不阻断主流程"口径)。
        /// </summary>
        private void SubscribeRedis()
        {
            if (!_redisEnabled || _multiplexer is null)
            {
                return;
            }

            try
            {
                // WHY 保存 handler 引用(批 AC 实测修正):Unsubscribe(channel) 不带 handler 时,StackExchange.Redis
                // 会摘掉**整通道订阅**——多实例共用一个 IConnectionMultiplexer(同进程多 EventBus/测试容器)时,
                // 一个实例 StopAsync 会连带摘掉其它实例对本通道的订阅。带 handler 退订只摘自己的订阅。
                _redisHandler = (_, value) => OnRedisMessage(value);
                _multiplexer.GetSubscriber().Subscribe(RedisChannel.Literal(_redisChannel), _redisHandler);
                _logger.LogInformation("事件总线双通道已启用:Redis 通道 {Channel} 本实例 {Instance}", _redisChannel, _instanceId);
            }
            catch (Exception ex)
            {
                _redisEnabled = false;
                _logger.LogError(ex, "事件总线双通道订阅失败 → 降级纯本地通道(跨实例事件不再接收)");
            }
        }

        /// <summary>注销 Redis 订阅(停机;失败只告警)。带 handler 退订=只摘本实例订阅(见 SubscribeRedis WHY)。</summary>
        private void UnsubscribeRedis()
        {
            if (!_redisEnabled || _multiplexer is null || _redisHandler is null)
            {
                return;
            }

            try
            {
                _multiplexer.GetSubscriber().Unsubscribe(RedisChannel.Literal(_redisChannel), _redisHandler);
                _redisHandler = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "事件总线双通道注销订阅失败(停机继续)");
            }
        }

        /// <summary>
        /// 跨实例消息处理(Redis 回调线程,须非阻塞):解析信封 → 自回环丢弃 → TryWrite 入本地队列。
        /// WHY TryWrite 而非 WriteAsync:回调是同步签名,等待背压会挂住 Redis 回调线程;
        /// 队列满时丢弃并告警(跨实例事件可容忍丢弃,本进程内事件仍走 WriteAsync 背压不丢)。
        /// </summary>
        private void OnRedisMessage(RedisValue value)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<RedisEnvelope>(value.ToString(), EventJsonOpts);
                if (envelope is null || string.IsNullOrWhiteSpace(envelope.Event))
                {
                    return;
                }

                // 自回环:本实例发布的消息本地已执行,收到自己的广播直接丢弃(否则事件重复触发)
                if (string.Equals(envelope.Origin, _instanceId, StringComparison.Ordinal))
                {
                    return;
                }

                // 白名单校验(与发布侧同判据):非白名单事件即使被投递到本通道也不执行(防手工 PUBLISH 注入旁路)
                if (!_crossInstanceEvents.Contains(envelope.Event))
                {
                    _logger.LogDebug("跨实例事件 {Event} 不在白名单({Key}),已忽略", envelope.Event, CrossInstanceEventsConfigKey);
                    return;
                }

                if (!_channel.Writer.TryWrite(new EventMessage(envelope.Event, envelope.EventId, null, envelope.Payload)))
                {
                    _logger.LogWarning("跨实例事件 {Event} 入队失败(本地队列已满),该跨实例副本已丢弃", envelope.Event);
                }
            }
            catch (Exception ex)
            {
                // 消息体非法/类型不匹配:丢弃并告警,不影响订阅通道与宿主
                _logger.LogWarning(ex, "事件总线跨实例消息解析失败,已丢弃");
            }
        }

        // 分发:按事件名取订阅者列表,逐个「建作用域解析 + 类型校验 + 重试执行」;单订阅者失败不阻断其余订阅者。
        private async Task DispatchAsync(EventMessage message)
        {
            // TryGetValue 的 out 参数为 [NotNullWhen(true)] 可空;声明可空承接,命中 false 分支即返回,
            // 走到 foreach 时经流分析必非空(NotNullWhen(true) 保证 true 时非空)
            List<SubscriberHandler>? handlers;
            lock (_scanLock)
            {
                if (!_handlers.TryGetValue(message.EventName, out handlers))
                {
                    // 无订阅者:静默丢弃(仅调试级,属正常态——非所有事件都必有订阅)
                    _logger.LogDebug("事件 {Event} 无订阅者,忽略", message.EventName);
                    return;
                }
            }

            foreach (var handler in handlers)
            {
                // 每订阅者独立作用域:单例总线不持有 Scoped 依赖,按事件建作用域从 DI 解析订阅者(规避单例吃不到 Scoped)
                using var scope = _rootProvider.CreateScope();
                var subscriber = scope.ServiceProvider.GetService(handler.SubscriberType);
                if (subscriber is null)
                {
                    // 订阅者类型未注册(DI 解析不到):告警并跳过(不阻断队列;属集成错误,启动后配置问题)
                    _logger.LogWarning("事件 {Event} 的订阅者 {Type} 未能从 DI 解析,已跳过——请确认其已注册(如 [Service] 标注)",
                        message.EventName, handler.SubscriberType.FullName);
                    continue;
                }

                if (subscriber is not IEventSubscriber)
                {
                    // 防御:命中 [EventSubscribe] 却未实现标记接口(违反订阅者契约),告警跳过
                    _logger.LogWarning("订阅者 {Type} 未实现 IEventSubscriber(违反订阅者契约),事件 {Event} 已跳过",
                        handler.SubscriberType.Name, message.EventName);
                    continue;
                }

                // 载荷类型不匹配:订阅方法签名与发布载荷不符(集成错误),防御性跳过并告警,不阻断队列
                var param = handler.Method.GetParameters()[0];

                // 跨实例消息(批 AC 5.6):载荷以 JSON 形式持有(发布进程的类型无法跨进程序列化),
                // 此处按**订阅方法参数类型**反序列化还原;还原失败(结构不符/类型不可映射)即告警跳过
                object? payload = message.Payload;
                if (message.PayloadJson is not null)
                {
                    try
                    {
                        payload = JsonSerializer.Deserialize(message.PayloadJson, param.ParameterType, EventJsonOpts);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "跨实例事件 {Event} 载荷无法还原为 {Param}(订阅 {Type}.{Method}),已跳过",
                            message.EventName, param.ParameterType.Name, handler.SubscriberType.Name, handler.Method.Name);
                        continue;
                    }
                }

                if (!param.ParameterType.IsInstanceOfType(payload))
                {
                    _logger.LogWarning("事件 {Event} 载荷类型 {Payload} 与订阅方法 {Type}.{Method} 参数 {Param} 不符,已跳过",
                        message.EventName, payload?.GetType().Name, handler.SubscriberType.Name, handler.Method.Name, param.ParameterType.Name);
                    continue;
                }

                await InvokeWithRetryAsync(message, payload, subscriber, handler).ConfigureAwait(false);
            }
        }

        // 执行(批 S 5.8 委托执行器):重试策略/执行审计/SysLogEvent 落库全部由 IEventHandlerExecutor 承担
        // (默认 RetryEventHandlerExecutor:精细重试按订阅特性、缺省回落全局;审计默认开,SuppressMonitor opt-out)。
        // 说明:执行在单消费者线程内串行进行(重试间隔等待),高失败率会占用消费吞吐(既有口径,注释保留)。
        private async Task InvokeWithRetryAsync(EventMessage message, object? payload, object subscriber, SubscriberHandler handler)
        {
            var context = new EventHandlerExecutingContext
            {
                EventName = message.EventName,
                EventId = message.EventId,
                Payload = payload,
                SubscriberType = handler.SubscriberType,
                Method = handler.Method,
                Attribute = handler.Method.GetCustomAttribute<EventSubscribeAttribute>(inherit: false)
                    ?? throw new InvalidOperationException($"订阅方法 {handler.SubscriberType.Name}.{handler.Method.Name} 缺 [EventSubscribe] 特性(扫描期已登记,不应为空)")
            };

            await _executor.ExecuteAsync(context, ctx =>
            {
                var invoke = ctx.Method.Invoke(subscriber, new[] { ctx.Payload });
                // 订阅方法可返回 Task(异步)或 void(同步);返回 Task 则等待其完成(异常在此浮出,供执行器重试决策)
                if (invoke is Task task)
                {
                    return task;
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        // 启动扫描:遍历 assemblies 全部公共非抽象类的公共实例方法,命中 [EventSubscribe] 即登记(签名须恰 1 参数=载荷)。
        private void ScanSubscribers(Assembly[] assemblies)
        {
            if (assemblies is null)
            {
                _logger.LogWarning("事件总线未提供任何待扫描程序集,启动时零订阅(仅能接收无订阅事件并静默丢弃)");
                return;
            }

            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 个别类型因依赖缺失加载失败不阻断扫描:取已加载部分继续(口径同 AddAutoServices)
                    _logger.LogWarning(ex, "事件总线扫描程序集 {Assembly} 部分类型加载失败,按已加载部分继续", assembly.FullName);
                    types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
                }

                foreach (var type in types)
                {
                    if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !type.IsPublic)
                    {
                        continue;
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var attr = method.GetCustomAttribute<EventSubscribeAttribute>(inherit: false);
                        if (attr is null)
                        {
                            continue;
                        }

                        // 签名契约:订阅方法须恰一个参数(载荷),否则告警并跳过(防止误标注多参方法)
                        var parameters = method.GetParameters();
                        if (parameters.Length != 1)
                        {
                            _logger.LogWarning("订阅方法 {Type}.{Method} 参数个数 {Count} 异常(须恰为 1 个载荷参数),已跳过",
                                type.FullName, method.Name, parameters.Length);
                            continue;
                        }

                        lock (_scanLock)
                        {
                            if (!_handlers.TryGetValue(attr.Name, out var list))
                            {
                                list = new List<SubscriberHandler>();
                                _handlers[attr.Name] = list;
                            }
                            list.Add(new SubscriberHandler(type, method));
                        }
                        _logger.LogInformation("事件订阅注册:事件 {Event} → {Type}.{Method}", attr.Name, type.Name, method.Name);
                    }
                }
            }
        }

        // 订阅处理器:订阅者具体类型(供 DI 按具体类解析)+ 订阅方法(供反射调用)
        private readonly record struct SubscriberHandler(Type SubscriberType, MethodInfo Method);

        // 队列消息:事件名 + 事件 ID(发布时生成,审计行定位键,批 S 5.8)+ 载荷(object? 持有,消费时按订阅方法参数类型校验;null 载荷会被类型校验分支跳过)
        // PayloadJson(批 AC 5.6):跨实例消息的载荷以 JSON 承载(发布进程的具体类型跨不进程序列化),
        // 本地消息为 null(直接持有对象,零序列化开销)。两个字段互斥:PayloadJson 非空时按订阅参数类型反序列化还原。
        private sealed record EventMessage(string EventName, string EventId, object? Payload, string? PayloadJson = null);

        /// <summary>
        /// 跨实例消息信封(批 AC 5.6):事件名/事件 Id/来源实例标识/载荷 JSON。
        /// Origin = 发布实例标识(自回环判据);载荷 JSON 由接收侧按订阅方法参数类型还原。
        /// </summary>
        private sealed record RedisEnvelope(string Event, string EventId, string Origin, string Payload);
    }
}
