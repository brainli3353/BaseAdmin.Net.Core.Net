using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.EventBus
{
    /// <summary>
    /// 事件执行上下文(批 S 5.8,自定 POCO 替代参照 Furion EventHandlerExecutingContext):
    /// 单次订阅执行的定位信息——供执行器做审计与精细重试决策。
    /// </summary>
    public sealed class EventHandlerExecutingContext
    {
        /// <summary>事件名(如 op_log/ex_log)。</summary>
        public string EventName { get; init; } = string.Empty;

        /// <summary>事件 ID(发布时生成的全局唯一标识,审计行 EventId)。</summary>
        public string EventId { get; init; } = string.Empty;

        /// <summary>载荷(引用持有;序列化失败不阻断执行,审计载荷置空)。</summary>
        public object? Payload { get; init; }

        /// <summary>订阅者具体类型。</summary>
        public Type SubscriberType { get; init; } = typeof(object);

        /// <summary>订阅方法。</summary>
        public System.Reflection.MethodInfo Method { get; init; } = null!;

        /// <summary>订阅特性(重试策略/审计开关的执行时定格)。</summary>
        public EventSubscribeAttribute Attribute { get; init; } = null!;
    }

    /// <summary>
    /// 事件执行器(批 S 5.8,参照 Admin.NET IEventHandlerExecutor 移植):
    /// 包裹订阅方法的执行——重试策略/执行审计/终态处理的扩展点。
    /// 默认实现 <see cref="RetryEventHandlerExecutor"/>;自定义实现替换注册即整体接管(扩展点语义)。
    /// </summary>
    public interface IEventHandlerExecutor
    {
        /// <summary>
        /// 执行 handler(内部含重试策略与审计)。**契约:绝不上抛**——重试耗尽/审计失败均内部消化,
        /// 事件消费失败不影响主流程与队列推进(总线既有口径)。
        /// </summary>
        Task ExecuteAsync(EventHandlerExecutingContext context, Func<EventHandlerExecutingContext, Task> handler);
    }

    /// <summary>
    /// 重试事件执行器(批 S 5.8,参照 Admin.NET RetryEventHandlerExecutor 移植):
    /// SysLogEvent 执行审计(Insert Running → 每尝试 finally Update 终态/中间态)+
    /// 精细重试(按 [EventSubscribe] 的 NumRetries/RetryTimeout/ExceptionTypes,缺省回落全局 ≤2/1s)。
    /// 参照偏差(见 g13 §5):LogDb 静态取库→DI 仓储;Furion Retry 工具→自实现循环;Furion context→自定 POCO。
    /// </summary>
    [Service(ServiceType = typeof(IEventHandlerExecutor), Lifetime = ServiceLifetime.Singleton)]
    public class RetryEventHandlerExecutor : IEventHandlerExecutor
    {
        // 全局缺省重试(与 EventBus 既有简化重试同值:特性未显式定制时行为不变)
        private const int GlobalMaxRetries = 2;
        private const int GlobalRetryDelayMs = 1000;

        private readonly ILogger<RetryEventHandlerExecutor> _logger;
        // 批 U 修:本服务 Singleton,不得直接注入 Scoped 仓储(ISqlSugarRepository)—生产 DI 校验
        // (ValidateOnBuild)启动即拒「Cannot consume scoped service from singleton」。
        // 改持 IServiceProvider,审计写库时自建 scope 解析仓储(service-provider 标准模式);
        // 无仓储的宿主(纯总线机制测试容器)解析抛异常 → 被 SaveAuditAsync 捕获 → 降级纯重试(仅审计禁用),
        // 与批 R 缓存适配器 GetService 降级同口径;生产容器(AddSqlSugar)必得实例 → 完整审计
        private readonly IServiceProvider _serviceProvider;

        public RetryEventHandlerExecutor(ILogger<RetryEventHandlerExecutor> logger, IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public async Task ExecuteAsync(EventHandlerExecutingContext context, Func<EventHandlerExecutingContext, Task> handler)
        {
            var shouldLog = context.Method.GetCustomAttribute<SuppressMonitorAttribute>(inherit: false) is null;

            SysLogEvent? log = null;
            if (shouldLog)
            {
                log = new SysLogEvent
                {
                    EventId = context.EventId,
                    EventName = context.EventName,
                    HandlerMethod = $"{context.SubscriberType.FullName}.{context.Method.Name}",
                    ExecutingTime = DateTime.Now,
                    Status = EventStatusEnum.Running,
                    Attribute = SerializeSnapshot(context.Attribute),
                    Payload = SerializePayload(context.Payload)
                };
                await SaveAuditAsync(log, "审计插入失败");
            }

            var numRetries = context.Attribute.NumRetries < 0 ? GlobalMaxRetries : context.Attribute.NumRetries;
            var retryDelay = context.Attribute.RetryTimeout < 0 ? GlobalRetryDelayMs : context.Attribute.RetryTimeout;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var exceptionList = new List<string>();
            Exception? lastException = null;
            var finalStatus = EventStatusEnum.Running;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await handler(context).ConfigureAwait(false);
                    finalStatus = EventStatusEnum.Success;
                    break; // 成功
                }
                catch (Exception ex)
                {
                    // ReflectionMethod.Invoke 的同步异常包裹在 TargetInvocationException,剥到根因便于定位(总线既有口径)
                    var root = ex is System.Reflection.TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                    lastException = root;
                    exceptionList.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {root.Message}");

                    // 异常类型白名单:非空且根因不属白名单(含子类)→ 不可重试,首次失败即终态
                    if (context.Attribute.ExceptionTypes.Length > 0 && !IsRetryable(root, context.Attribute.ExceptionTypes))
                    {
                        finalStatus = EventStatusEnum.Error;
                        _logger.LogWarning(root,
                            "事件 {Event} 订阅 {Method} 抛不可重试异常类型 {Type},首次失败即终态(审计 Status=Error,不阻断队列)",
                            context.EventName, context.Method.Name, root.GetType().Name);
                        break;
                    }

                    if (attempt >= numRetries)
                    {
                        // 重试耗尽:终态 Error(不阻断队列)
                        finalStatus = EventStatusEnum.Error;
                        _logger.LogWarning(root,
                            "事件 {Event} 订阅 {Method} 重试 {Retries} 次后仍失败,放弃(审计 Status=Error,不阻断队列)",
                            context.EventName, context.Method.Name, numRetries);
                        break;
                    }

                    // 中间态:仍有重试预算 → Retry(中间态随每尝试落库,参照同口径)
                    finalStatus = EventStatusEnum.Retry;
                    if (log is not null)
                    {
                        log.Status = EventStatusEnum.Retry;
                        await SaveAuditAsync(log, "审计更新失败");
                    }
                    _logger.LogDebug("事件 {Event} 订阅 {Method} 第 {Attempt} 次失败,{Delay}ms 后重试: {Message}",
                        context.EventName, context.Method.Name, attempt + 1, retryDelay, root.Message);
                    await Task.Delay(retryDelay).ConfigureAwait(false);
                }
            }

            if (log is not null)
            {
                log.ExecutedTime = DateTime.Now;
                log.Elapsed = stopwatch.ElapsedMilliseconds;
                log.RetryRecord = exceptionList.Count > 0 ? string.Join(Environment.NewLine, exceptionList) : null;
                log.Exception = lastException is null ? null
                    : $"{lastException.GetType().Name}: {lastException.Message}{Environment.NewLine}{lastException.StackTrace}";
                log.Status = finalStatus;
                await SaveAuditAsync(log, "审计更新失败");
            }
        }

        // 异常类型白名单匹配:根因类型属白名单任一类型或其子类 → 可重试
        private static bool IsRetryable(Exception ex, Type[] exceptionTypes)
            => exceptionTypes.Any(t => t.IsAssignableFrom(ex.GetType()));

        // 审计写库:自建 scope 解析 Scoped 仓储(Singleton 消费 Scoped 的合规路径);
        // 容器无仓储(降级宿主)/写库失败 → 只记错误不阻断(日志库不可用不伤事件流;参照同口径 try/catch 包裹)
        private async Task SaveAuditAsync(SysLogEvent log, string scene)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysLogEvent>>();
                if (log.Id <= 0)
                {
                    await repo.InsertAsync(log);
                }
                else
                {
                    await repo.UpdateAsync(log);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "事件 {Event} 审计{Scene}(已隔离,不影响事件流)", log.EventId, scene);
            }
        }

        // 载荷序列化:camelCase(与全仓 ApiResult 同口径);失败返回 null(审计缺载荷不阻断)
        private static string? SerializePayload(object? payload)
        {
            if (payload is null)
            {
                return null;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return null;
            }
        }

        // 特性快照:重试策略定格(执行时口径,运行期改代码不追溯)
        private static string? SerializeSnapshot(EventSubscribeAttribute attr)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    attr.Name,
                    attr.NumRetries,
                    attr.RetryTimeout,
                    ExceptionTypes = attr.ExceptionTypes.Select(t => t.FullName).ToArray()
                });
            }
            catch
            {
                return null;
            }
        }
    }
}
