using System.Diagnostics;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Utils;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Job
{
    /// <summary>
    /// 定时任务调度契约(task_108):管理服务(SysJobService)与界面的稳定入口——
    /// RunNow 立即执行/查询代码注册名/查询当前生效调度。实现类为 Singleton 常驻件(见 JobScheduler remarks)。
    /// </summary>
    public interface IJobScheduler
    {
        /// <summary>
        /// 立即触发一次任务(不等到点;触发即返回,不等待执行完成——长任务不阻塞调用方)。
        /// 防重入锁与调度执行同口径——上一轮未完成即拒绝。
        /// 返回 false:任务未注册代码,或上一轮未完成。
        /// </summary>
        Task<bool> RunNowAsync(string jobName);

        /// <summary>代码注册的任务名集合([JobName] 标注的 IJob 实现类)。</summary>
        IReadOnlyCollection<string> GetRegisteredJobNames();

        /// <summary>本实例标识(批 AJ:NodeTag:进程Id:GUID;集群视图用于标注"本实例")。</summary>
        string InstanceId { get; }

        /// <summary>当前生效的调度任务名(数据库已加载且 Enabled)。</summary>
        IReadOnlyCollection<string> GetScheduledJobNames();
    }

    /// <summary>
    /// 定时任务调度器(task_108,Furion.Schedule 的原生替代):
    /// BackgroundService 常驻循环(5s 粒度)+ [JobName] 反射扫描(代码注册)+ SysJobDetail 数据库持久化(重启恢复)。
    /// </summary>
    /// <remarks>
    /// 调度循环:启动预载数据库任务定义(重启恢复)→ 每 5s 检查到期任务(Interval 到点)→ 触发执行
    /// (后台运行,循环不等待执行完成)→ 每 60s(12 轮)刷新数据库快照(管理侧 启用/禁用/间隔 变更 1 分钟内免重启生效)。
    /// 防重入(手册 §5.16 简化,单机口径):执行前经 ICacheService 取锁 sys_job:{name}
    /// (原子自增语义:首个自增者持锁,后到者见值 &gt;1 跳过「上一轮未完成」;执行完 RemoveAsync 释放;
    /// TTL 30 分钟仅防进程异常后的锁残留泄漏,不是执行超时控制)。
    /// 重复触发防护(批 AK):防重入锁只保证「不同时跑」,不保证「同一轮不重复跑」——LastRunTime 的内存快照
    /// 每 60s 才整表重载,任务执行完、锁释放后,刷新前的每个 tick 仍会被判到期并重复执行(实测 5.1s 窗口 11 次)。
    /// 故引入**共享缓存中的权威「上次触发时刻」**(CacheConst.KeyJobLast,跨实例统一判据):取到防重入锁后、
    /// 执行前在**锁内**用它复检(ConfirmDueBySharedLastAsync);未到点即就地释放锁并把内存快照对齐权威值,
    /// 到点则先写共享键再回写内存快照(RecordFiredAsync),然后进入执行体。内存快照(IsDue)保留为快速筛选:
    /// 它只可能比共享基准更旧 ⇒ 判「未到点」必然真未到点,不会漏触发。执行 finally 只释放防重入锁、绝不删共享键。
    /// 手动触发(RunNow)不受此复检约束(「立即执行、不看 Interval」的原契约不变),但同样登记本次触发时刻。
    /// 失败隔离:任务执行异常仅记 ILogger(Error)+ 不退出调度器(契约);LastRunTime 在触发点(开始时刻)更新——
    /// 下次到点以开始时刻计,长任务(执行时长 &gt; 间隔)与防重入锁配合,至多一轮在途,不堆积重复触发。
    /// 降级自愈:数据库不可达(生产占位配置等)不阻塞宿主启动——每轮重试预载直至成功(对齐 task_010 启动降级口径)。
    /// 注册(§99③ 开发与注册分离,零改 Program.cs):[Service] 显式锁定 ServiceType=IJobScheduler + Singleton,
    /// AddAutoServices 的 HostedService 分支补注册 IHostedService(工厂经主服务类型解析,共享同一单例——EventBus 同模式)。
    /// 作用域隔离:单例不持有 Scoped 依赖——每次预载/执行临时建作用域解析仓储与 IJob 实现(手册 §5.9 同口径)。
    /// DB 串行(并发探针实证,task_108):共享单例 SqlSugar scope 同一实例的并发操作会撞在同一连接上
    /// ("A command is already in progress");调度器自身三类 DB 操作——后台预载(循环线程)、任务执行
    /// (fire-and-forget 线程池线程)、RunNow(HTTP 线程)——经 _dbSerial 互斥串行。与 HTTP 直连仓储的
    /// 并发限制属框架级已知差距(docs/框架差距清单.md),不在本件范围。
    /// </remarks>
    [Service(ServiceType = typeof(IJobScheduler), Lifetime = ServiceLifetime.Singleton)]
    public class JobScheduler : BackgroundService, IJobScheduler
    {
        /// <summary>到期检查粒度(契约:5s 轮询;基座不追求秒级精度——手册 §5.14 开发注意)。</summary>
        private const int DefaultTickIntervalMs = 5000;

        /// <summary>数据库快照刷新周期(12 轮 = 60s):管理侧变更 1 分钟内免重启生效。</summary>
        private const int RefreshEveryTicks = 12;

        /// <summary>防重入锁 TTL 兜底:任务挂死且进程异常时,锁残留 30 分钟后自动过期(正常路径 finally 释放)。</summary>
        private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 共享「上次触发时刻」基准键 TTL 的下界(秒,批 AK)= 1 小时。取值依据(三条都要成立):
        /// ① **明显长于快照刷新周期**——内存快照每 <see cref="RefreshEveryTicks"/> 轮(缺省 60s)才重载,
        ///    基准键若在此之前过期,复检读空即退回旧缺陷(重复触发回归);1 小时 = 刷新周期的 60 倍。
        /// ② **不短于 2 × Interval**(见 <see cref="LastTtlFor"/>)——基准至少要活过下一轮到点判定,
        ///    否则"每次到点都恰好丢基准"会造成周期性重复。
        /// ③ **不永久占键**——它是缓存不是账本,必须有自然失效边界(同 KeyJobLock 的"泄漏兜底"纪律);
        ///    且管理侧把 Interval 改得极大时,旧基准最多 1 小时后消失,由 DB 预载值兜底接续。
        /// </summary>
        private const long LastTtlFloorSeconds = 3600;

        /// <summary>集群节点心跳超时(批 AJ:超过该秒数未心跳的节点视为死亡,读侧过滤)。</summary>
        private const int NodeHeartbeatTimeoutSeconds = 90;

        private readonly IServiceProvider _rootProvider;
        private readonly ICacheService _cache;
        private readonly ILogger<JobScheduler> _logger;
        private readonly int _tickIntervalMs;

        // ── 集群模式(批 AJ 5.16)──────────────────────────────────────────────
        // 开关与身份来自配置(JobCluster:*);Enabled=false 时跳过心跳与续租(单实例部署省 Redis 往返),
        // 防重入锁仍走既有 IncrementAsync 口径(Redis 档下本就跨实例)。
        private readonly bool _clusterEnabled;
        private readonly string _nodeTag;
        private readonly string _instanceId;
        private readonly TimeSpan _lockRenewInterval;

        // 代码注册表:JobName → IJob 实现类型(构造期反射扫描填充;填充先于循环启动,读取无竞争)
        private readonly Dictionary<string, Type> _jobTypes = new();

        // 数据库调度快照:JobName → 调度项(预载/刷新期整体换新引用——volatile 原子换,读侧免锁)
        private volatile Dictionary<string, JobScheduleEntry> _schedules = new();
        // 本调度实例的启动时刻(节点行 startedAt 的语义＝"这个调度器"何时起,批 AT 起与身份单点**解耦**:
        // 身份单点只保证 InstanceId/NodeTag/Pid 跨视图一致〔两表对号靠它〕,而 startedAt 是调度器自己的事实——
        // 若取身份单点的进程级时刻,全量套件里晚跑的用例会看到"启动于 10 分钟前"的自身节点行
        // 〔与 §42.4 视图"该实例启动多久"的直觉和既有断言都不符〕)
        private readonly DateTime _startedAt = DateTime.Now;
        private volatile bool _dbLoaded;
        private int _ticks;

        // 调度器 DB 操作串行锁(task_108):共享单例 SqlSugar scope 同一实例并发不安全
        // (task_108 并发探针实证:同实例 20 并发查询大量 "A command is already in progress";
        // 独立 scope 实例则 20/20 通过)——本调度器的后台预载(循环线程)/ 任务执行(fire-and-forget 线程池线程)/
        // RunNow(HTTP 线程)会在不同线程上并发触碰同一 scope 实例,此处将调度器自身的 DB 操作串行化。
        // 注:与 HTTP 路径直连仓储的并发限制属框架级已知差距(见 docs/框架差距清单.md),不在本件范围。
        private readonly SemaphoreSlim _dbSerial = new(1, 1);

        /// <summary>
        /// DI 构造入口(同 EventBus 口径):缺省仅扫描本类所在程序集(Core)——框架内置任务一律落 Core;
        /// 跨程序集(如测试程序集)走显式 Assembly[] 构造,不在 DI 入口硬编码。
        /// </summary>
        [ActivatorUtilitiesConstructor]
        public JobScheduler(IServiceProvider rootProvider, ICacheService cache, ILogger<JobScheduler> logger)
            : this(rootProvider, cache, logger, new[] { typeof(JobScheduler).Assembly }, DefaultTickIntervalMs)
        {
        }

        /// <summary>
        /// 构造:扫描 assemblies 中全部 [JobName] 任务(重复任务名启动即抛)。
        /// </summary>
        /// <param name="assemblies">待扫描程序集(测试传测试程序集——EventBus 同模式)。</param>
        /// <param name="tickIntervalMs">到期检查粒度毫秒数(缺省 5000;测试注入短周期加速用例)。</param>
        public JobScheduler(IServiceProvider rootProvider, ICacheService cache, ILogger<JobScheduler> logger, Assembly[] assemblies, int tickIntervalMs = DefaultTickIntervalMs)
        {
            _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tickIntervalMs = Math.Max(50, tickIntervalMs);

            // 集群身份与开关(批 AJ):IConfiguration 可选解析(最小宿主/单测容器可能未注册 → 走缺省)
            var config = rootProvider.GetService<IConfiguration>();
            _clusterEnabled = config?.GetValue("JobCluster:Enabled", true) ?? true;
            // 批 AT F1:身份取单点(与监控快照表 sys_server_metrics 同一 InstanceId,两视图可对号;
            // NodeTag 仍读 JobCluster:NodeTag 缺省机器名,格式与批 AJ 原样一致)。
            // 注意**不取** identity.StartedAt:节点行的 startedAt 语义是"本调度器启动时刻",见 _startedAt 字段注释。
            var identity = InstanceIdentity.Resolve(config);
            _nodeTag = identity.NodeTag;
            _instanceId = identity.InstanceId;
            var renewSeconds = config?.GetValue("JobCluster:LockRenewSeconds", 300) ?? 300;
            _lockRenewInterval = TimeSpan.FromSeconds(Math.Max(30, renewSeconds));

            ScanJobs(assemblies);
        }

        /// <inheritdoc />
        public IReadOnlyCollection<string> GetRegisteredJobNames() => _jobTypes.Keys.ToArray();

        /// <inheritdoc />
        public string InstanceId => _instanceId;

        /// <inheritdoc />
        public IReadOnlyCollection<string> GetScheduledJobNames()
            => _schedules.Where(kv => kv.Value.Enabled).Select(kv => kv.Key).ToArray();

        // 反射扫描:IJob 实现 + [JobName] → 代码注册表。重复任务名快速失败(两任务同名 = 配置错误,启动即抛)。
        private void ScanJobs(Assembly[] assemblies)
        {
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 个别类型加载失败不阻断启动:取已加载部分继续扫描(AddAutoServices 同口径)
                    types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
                }

                foreach (var type in types)
                {
                    if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !type.IsPublic)
                    {
                        continue;
                    }

                    if (!typeof(IJob).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    var attr = type.GetCustomAttribute<JobNameAttribute>(inherit: false);
                    if (attr is null)
                    {
                        continue;
                    }

                    if (!_jobTypes.TryAdd(attr.Name, type))
                    {
                        throw new InvalidOperationException(
                            $"[JobName(\"{attr.Name}\")] 重复:{type.FullName}——任务名必须全局唯一");
                    }
                }
            }

            _logger.LogInformation("定时任务代码注册完成:{Count} 个({Names})", _jobTypes.Count, string.Join(", ", _jobTypes.Keys));
        }

        /// <summary>
        /// 启动即报一次心跳(批 AT F1 配套):<see cref="BackgroundService.StartAsync"/> 返回时本实例**必然已在节点表可见**。
        /// </summary>
        /// <remarks>
        /// WHY 放进 StartAsync 而不是只放调度循环:循环里的心跳是异步的(start 不等待 ExecuteAsync 跑完),
        /// 于是"同一 NodeTag 的新实例"在其首轮心跳落地前,集群视图仍显示**上一实例**的最后心跳——
        /// 滚动发布/重建期间会出现"视图指向旧实例"的短暂假象(批 AJ 用例 01 的真机回归实测抓到该窗口)。
        /// 心跳内部已全捕获(缓存不可用只告警),故不改变"启动不依赖外部依赖"的既有降级口径。
        /// </remarks>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_clusterEnabled)
            {
                await HeartbeatAsync();
            }

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 启动预载(重启恢复):失败(库不可达/占位配置)不阻塞宿主启动——每轮重试自愈直至成功
            if (!await LoadSchedulesAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested && !await LoadSchedulesAsync(stoppingToken))
                {
                    await SafeDelayAsync(stoppingToken);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                _ticks++;

                // 周期快照刷新:管理侧变更(启用/禁用/间隔/备注)1 分钟内免重启生效
                if (_ticks % RefreshEveryTicks == 0)
                {
                    await LoadSchedulesAsync(stoppingToken);
                }

                // 集群心跳(批 AJ):每轮刷新本实例节点行(失败不阻断调度——记日志)
                if (_clusterEnabled)
                {
                    await HeartbeatAsync();
                }

                var now = DateTime.Now;
                foreach (var (name, entry) in _schedules)
                {
                    if (!entry.Enabled || !_jobTypes.ContainsKey(name))
                    {
                        continue;
                    }

                    // 归属实例校验(批 AJ):TargetNode 非空且不匹配本机 NodeTag ⇒ 直接跳过(不加锁不占位)
                    if (!string.IsNullOrWhiteSpace(entry.TargetNode)
                        && !string.Equals(entry.TargetNode, _nodeTag, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsDue(entry, now))
                    {
                        continue;
                    }

                    // 触发(后台运行,不阻塞调度循环):防重入锁 + 锁内权威复检 + 本次触发时刻登记 + 执行,全在后台 Task 内
                    // enforceInterval=true:调度触发受 Interval 约束(与手动触发相对,见 FireAsync)
                    _ = FireAsync(name, now, "scheduled", stoppingToken, enforceInterval: true);
                }

                await SafeDelayAsync(stoppingToken);
            }
        }

        /// <summary>
        /// 到期检查(内存快照口径):从未执行(LastRunTime 空)或间隔已到(自上一轮开始时刻计)。
        /// 批 AK 后它只是**前置快速筛选**(判定式一字未改):内存快照只可能比共享基准更旧 ⇒ 判「未到点」
        /// 必然真未到点(不会漏触发);判「到点」的都要进锁内权威复检 <see cref="ConfirmDueBySharedLastAsync"/>
        /// 再裁一次(复检只会收紧,不会放宽)——两者叠加才等于「同一 Interval 全集群至多一次」。
        /// </summary>
        private static bool IsDue(JobScheduleEntry entry, DateTime now)
        {
            if (entry.LastRunTime is null)
            {
                return true;
            }

            return (now - entry.LastRunTime.Value).TotalSeconds >= entry.IntervalSeconds;
        }

        /// <summary>
        /// 共享基准键 TTL = max(1 小时下界, 2 × Interval)。依据见 <see cref="LastTtlFloorSeconds"/>;
        /// Interval 非正(快照项缺失/行非法)时按 0 处理 ⇒ 落到 1 小时下界。
        /// </summary>
        private static TimeSpan LastTtlFor(int intervalSeconds)
            => TimeSpan.FromSeconds(Math.Max(LastTtlFloorSeconds, (long)Math.Max(0, intervalSeconds) * 2));

        // 触发流程:取防重入锁(上一轮未完成 → 记日志跳过,返回 false)→ **锁内权威复检(仅调度触发,批 AK)**
        // → 登记本次触发时刻(共享基准键 + 内存快照)→ 后台执行。
        // 返回是否实际触发(调度循环忽略该值;RunNow 以它裁决「上一轮未完成」)。体内容错全捕获,异常不逃逸到未观察 Task 池。
        // 触发裁决两段(批 AK):① 防重入原子自增锁(首个自增者持锁;>1 = 上一轮在途 → 跳过)——只保证「不同时跑」;
        //   ② 共享「上次触发时刻」复检——补上「同一轮不重复跑」:仅凭内存快照,任务执行完、锁释放后
        //   在 60s 刷新窗口内的每个 tick 都会再次判到期并重复执行(批 AK 实测 5.1s 窗口 11 次)。
        // IncrementAsync 无配套 Decrement——跳过方不动键(持锁方统一在 ExecuteTriggeredAsync 的 finally 释放);
        // 复检判「未到点」时尚无执行体接手,由复检路径就地释放刚取到的锁。
        // 取锁成功即返回 true(触发即返回,不等待执行完成)——长任务不阻塞调度循环/HTTP 请求。
        // enforceInterval:true = 调度触发(受 Interval 约束,须复检);false = 手动 RunNow(「立即执行」原契约,
        //   不复检,但同样登记本次触发时刻 ⇒ 手动执行也计入「上次触发」,后续调度不重复补跑一轮)。
        private async Task<bool> FireAsync(string name, DateTime now, string trigger, CancellationToken stoppingToken, bool enforceInterval)
        {
            var lockKey = CacheConst.KeyJobLock + name;
            long lockValue;
            try
            {
                lockValue = await _cache.IncrementAsync(lockKey, LockTtl);
            }
            catch (Exception ex)
            {
                // 缓存故障:防重入状态未知,保守跳过(本方法已在后台 Task/调用方已裁决,异常不外溢)
                _logger.LogError(ex, "定时任务 {Name} 防重入锁获取失败,跳过本次触发", name);
                return false;
            }

            if (lockValue > 1)
            {
                _logger.LogInformation("定时任务 {Name} 上一轮未完成,跳过本次触发(防重入)", name);
                return false;
            }

            // 快照项:调度触发必有(循环就来自它);手动触发可能为 null(行禁用/间隔非法/未入库) ⇒ Interval 按 0、基准兜底按 null
            _schedules.TryGetValue(name, out var entry);
            var intervalSeconds = entry?.IntervalSeconds ?? 0;

            if (enforceInterval)
            {
                if (!await ConfirmDueBySharedLastAsync(name, now, intervalSeconds, entry?.LastRunTime, lockKey))
                {
                    return false;
                }
            }
            else if (!await RecordFiredAsync(name, now, intervalSeconds))
            {
                // 手动触发:基准登记失败不阻断执行(「立即执行」是对外契约),最多退回「本轮未被计入上次触发时刻」
                _logger.LogWarning("定时任务 {Name} 上次触发时刻登记失败(手动触发,本轮执行照常)", name);
            }

            _ = ExecuteTriggeredAsync(name, now, trigger, stoppingToken, lockKey);
            return true;
        }

        /// <summary>
        /// 锁内权威复检(批 AK,重复触发窗口的根治点):以共享缓存的「上次触发时刻」为跨实例统一判据,
        /// 裁决本轮是否真该执行。返回 true = 确认到点(且本次触发时刻已登记);
        /// false = 本轮已被触发过/基准不可用(不执行,防重入锁已就地释放)。
        /// </summary>
        /// <remarks>
        /// WHY 复检必须在**锁内**:持锁方是本轮唯一写者,「读基准 → 判 → 写基准」之间没有并发对手 ⇒
        /// 跨实例正确性来自这把 Redis 原子自增锁,不需要 CAS/脚本(单写者窗口内 SetAsync 即足够)。
        /// WHY 挡下也要回写内存快照(WriteSnapshotLastRun):否则本实例每个 tick 都要重复「取锁→复检→释放」空转;
        /// 回写后本实例的 IsDue 直接判未到点,直到下一个 Interval ——且它以**其他实例的触发时刻**起算,
        /// 于是集群整体的语义变成「每 Interval 全局至多一次」。
        /// WHY 兜底用快照值(= DB 预载值):键不存在 = 首次调度或 TTL 自然到期;此时数据库行的 LastRunTime
        /// 就是最新的权威时刻(执行方在 ExecuteTriggeredAsync 里同值落库),据此判定与修复前语义一致、不重复触发。
        /// WHY 读/写失败一律保守跳过本轮:基准不可用 = 无法确认「本轮是否已被跑过」,宁可少跑一轮
        /// (下一 tick 快照仍未回写 ⇒ 照常重来,不漏)也不冒重复执行之险——与既有「锁获取失败即跳过」同口径。
        /// </remarks>
        private async Task<bool> ConfirmDueBySharedLastAsync(
            string name, DateTime now, int intervalSeconds, DateTime? snapshotLast, string lockKey)
        {
            DateTime? sharedLast;
            try
            {
                sharedLast = await _cache.GetAsync<DateTime?>(CacheConst.KeyJobLast + name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时任务 {Name} 共享上次触发时刻读取失败,跳过本次触发(不冒重复执行之险)", name);
                await ReleaseLockQuietlyAsync(lockKey, name);
                return false;
            }

            // 键不存在 → 内存快照(=DB 预载值)即当前权威;两者都空 = 从未执行 → 视为到点(首轮语义不变)
            var authoritative = sharedLast ?? snapshotLast;
            if (authoritative is { } last && (now - last).TotalSeconds < intervalSeconds)
            {
                _logger.LogInformation("定时任务 {Name} 距上次触发 {Elapsed:F1}s 未达间隔 {Interval}s,跳过本轮(共享基准,可能由其他实例触发)",
                    name, (now - last).TotalSeconds, intervalSeconds);
                WriteSnapshotLastRun(name, last);
                await ReleaseLockQuietlyAsync(lockKey, name);
                return false;
            }

            if (!await RecordFiredAsync(name, now, intervalSeconds))
            {
                await ReleaseLockQuietlyAsync(lockKey, name);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 登记本次触发时刻:**先写共享基准键,再回写内存快照**——顺序不可颠倒(先写快照会留下
        /// 「本实例快照已新、共享基准仍旧」的窗口,其他实例读到旧基准就会重复执行)。
        /// 返回 false = 共享键写入失败(调用方据此保守跳过本轮;内存快照不回写,下轮照常重判)。
        /// </summary>
        private async Task<bool> RecordFiredAsync(string name, DateTime now, int intervalSeconds)
        {
            try
            {
                await _cache.SetAsync(CacheConst.KeyJobLast + name, now, LastTtlFor(intervalSeconds));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时任务 {Name} 共享上次触发时刻写入失败,跳过本次触发(不冒重复执行之险)", name);
                return false;
            }

            WriteSnapshotLastRun(name, now);
            return true;
        }

        /// <summary>
        /// 内存快照回写(批 AK):<see cref="JobScheduleEntry"/> 是不可变 record ⇒ 沿用 LoadSchedulesAsync 的
        /// 「复制整表 + 换新引用后原子赋值」口径(写侧整体替换、读侧免锁),不原地改字典。
        /// WHY 同名 last-write-wins 可接受:① 同名任务由防重入锁保证同一时刻只有一轮在跑,回写方与复检方
        /// 写的都是「本轮触发时刻」这一个事实;② 与 60s 周期刷新并发时,最坏只是回写被同刻刷新覆盖,
        /// 而刷新读回的 DB 行 LastRunTime 正是执行方写的同一个时刻(ExecuteTriggeredAsync),两者数值一致;
        /// ③ 权威判据始终是共享基准键,内存快照只作快速筛选——回写丢失顶多多一次空取锁,不会造成重复执行。
        /// </summary>
        private void WriteSnapshotLastRun(string name, DateTime lastRun)
        {
            var current = _schedules;
            if (!current.TryGetValue(name, out var entry) || entry.LastRunTime == lastRun)
            {
                return;
            }

            var next = new Dictionary<string, JobScheduleEntry>(current);
            next[name] = entry with { LastRunTime = lastRun };
            _schedules = next;
        }

        /// <summary>
        /// 就地释放防重入锁(复检挡下/基准不可用时——此时还没有执行体接手,故不会走到 ExecuteTriggeredAsync 的 finally)。
        /// 释放失败只告警:残留由锁 TTL(30 分钟)兜底,与既有口径一致;异常不外溢(fire-and-forget 安全)。
        /// </summary>
        private async Task ReleaseLockQuietlyAsync(string lockKey, string name)
        {
            try
            {
                await _cache.RemoveAsync(lockKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时任务 {Name} 防重入锁释放失败(残留至锁 TTL 兜底)", name);
            }
        }

        // 取锁成功(且调度触发已通过锁内权威复检)的触发执行(后台 Task 体):DB 串行 → LastRunTime 落库 → 任务体;
        // finally 只释放防重入锁(共享基准键已由 FireAsync 写过,此处不删)。
        // 体内容错全捕获不逃逸(fire-and-forget 安全口径);宿主停止仅记日志。
        private async Task ExecuteTriggeredAsync(string name, DateTime now, string trigger, CancellationToken token, string lockKey)
        {
            var serialHeld = false;
            var sw = Stopwatch.StartNew();
            var success = false;
            string? failure = null;

            // 批 AJ:执行期续租(消除"执行超过锁 TTL 被其它实例重复执行且无提示"的静默重复)
            using var renewCts = new CancellationTokenSource();
            var renewTask = _clusterEnabled ? RenewLockLoopAsync(lockKey, renewCts.Token) : Task.CompletedTask;
            var runningKey = CacheConst.KeyJobRunning;

            try
            {
                if (_clusterEnabled)
                {
                    // 运行中登记(集群视图):JSON 里带实例/标签/触发方式;失败只告警(可观测性不阻断执行)
                    try
                    {
                        await _cache.HashSetAsync(runningKey, name, new
                        {
                            jobName = name,
                            instanceId = _instanceId,
                            nodeTag = _nodeTag,
                            startedAt = now,
                            trigger
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "定时任务 {Name} 运行中登记失败(集群视图缺失该行,执行继续)", name);
                    }
                }

                // DB 串行锁:与预载/其他任务执行互斥(共享 scope 实例并发不安全,见 _dbSerial 注释)
                await _dbSerial.WaitAsync(token);
                serialHeld = true;

                // LastRunTime = 开始时刻:长任务(执行时长 > 间隔)的下次到点以开始时刻计,配合防重入锁至多一轮在途
                using (var scope = _rootProvider.CreateScope())
                {
                    var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
                    var row = await repo.GetFirstAsync(u => u.JobName == name);
                    if (row is not null)
                    {
                        row.LastRunTime = now;
                        await repo.UpdateAsync(row);
                    }
                }

                // 每执行临时建作用域(单例不持有 Scoped);任务内 DB 操作同在此串行锁内
                using var execScope = _rootProvider.CreateScope();
                var job = (IJob)execScope.ServiceProvider.GetRequiredService(_jobTypes[name]);
                await job.ExecuteAsync(new JobContext(name, trigger, now), token);
                success = true;
                _logger.LogInformation("定时任务 {Name} 执行完成({Trigger}, {Ms}ms)", name, trigger, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger.LogInformation("定时任务 {Name} 宿主停止中,本轮执行中止", name);
            }
            catch (Exception ex)
            {
                // 执行异常:记录 + 不退出调度器(契约);LastRunTime 已在触发点更新,下次到点照常
                failure = ex.Message;
                _logger.LogError(ex, "定时任务 {Name} 执行异常({Trigger}),调度器继续", name, trigger);
            }
            finally
            {
                // 执行日志:每轮执行完成(成功/异常)落 1 行——触发时刻/耗时/成败/错误信息/执行实例;
                // 宿主停止取消(执行被中断)不落行;写入在串行锁内 + 独立 scope(G7 安全形态);
                // 落行失败仅告警——可观测性永不阻断执行与锁释放。
                if (serialHeld && (success || failure is not null))
                {
                    try
                    {
                        await WriteJobLogAsync(name, trigger, now, sw.ElapsedMilliseconds, success, failure);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "定时任务 {Name} 执行日志落库失败(执行继续)", name);
                    }
                }

                if (serialHeld)
                {
                    _dbSerial.Release();
                }

                // 先停续租再释放锁:顺序反了会让续租把已释放的键重新设过期(无害但多余)
                renewCts.Cancel();
                try
                {
                    await renewTask;
                }
                catch (OperationCanceledException)
                {
                    // 预期:续租循环被取消
                }

                if (_clusterEnabled)
                {
                    try
                    {
                        await _cache.HashDeleteAsync(runningKey, name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "定时任务 {Name} 运行中登记清理失败(视图残留该行,下次执行会覆盖)", name);
                    }
                }

                // 只释放防重入锁;**绝不删共享「上次触发时刻」基准键(KeyJobLast+name)**——
                // 删掉 = 复检读空 = 重复触发窗口回归(批 AK 的根治点就在这一行的"不做什么")
                await _cache.RemoveAsync(lockKey);
            }
        }

        /// <summary>
        /// 执行日志落库(sys_job_log):每轮执行完成(成功/异常)1 行——耗时/错误/执行实例随行。
        /// 独立 scope(G7「每操作独立 scope」安全形态);ErrorMsg 截断 512 防大字段膨胀。
        /// </summary>
        private async Task WriteJobLogAsync(string name, string trigger, DateTime triggeredAt, long durationMs, bool success, string? failure)
        {
            using var scope = _rootProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobLog>>();
            await repo.InsertAsync(new SysJobLog
            {
                JobName = name,
                Trigger = trigger,
                TriggeredAt = triggeredAt,
                DurationMs = durationMs,
                Success = success,
                ErrorMsg = failure is null ? null : failure.Length > 512 ? failure[..512] : failure,
                InstanceId = _instanceId,
                NodeTag = _nodeTag
            });
        }

        /// <summary>
        /// 锁续租循环(批 AJ):按 <c>JobCluster:LockRenewSeconds</c>(缺省 300s)周期延长防重入锁 TTL,
        /// 使**长任务执行期间锁不过期**;取消即退出(由 ExecuteTriggeredAsync.finally 触发)。
        /// 续租失败只告警:任务继续执行,但锁可能在 TTL 后过期(此时另一实例可能重复触发)——日志可检索。
        /// </summary>
        private async Task RenewLockLoopAsync(string lockKey, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(_lockRenewInterval, token);
                    await _cache.SetExpireAsync(lockKey, LockTtl);
                }
            }
            catch (OperationCanceledException)
            {
                // 预期:任务结束/宿主停止
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时任务防重入锁续租失败(任务 {LockKey} 仍在执行;锁可能在 TTL 后过期)", lockKey);
            }
        }

        /// <summary>
        /// 集群心跳(批 AJ):把本实例写入节点表(覆盖写,含 lastBeat);失败只告警(不阻断调度循环)。
        /// 另有 30 天 TTL 兜底防键泄漏——**死节点不靠 TTL 摘除**,由读侧按 lastBeat 超期(90s)过滤。
        /// </summary>
        private async Task HeartbeatAsync()
        {
            try
            {
                // WHY 传对象而非预序列化串:HashSetAsync(key, field, object?) 由各实现**自己**做一次序列化;
                // 预序列化会变成"字符串里嵌 JSON"(Memory 档读侧反序列化直接失败——本批 e2e 实证),
                // 统一传对象 ⇒ Redis/Memory 两档写入形态一致(单层)。
                // WHY 载荷内重复 instanceId:ICacheService.HashGetAllAsync<T> 只回 List<T>(不携带 field),
                // 端点要按实例 Id 判定 isSelf ⇒ 标识必须在值里(批 AJ 实测签名后修正)
                await _cache.HashSetAsync(CacheConst.KeyJobNode, _instanceId, new
                {
                    instanceId = _instanceId,
                    nodeTag = _nodeTag,
                    startedAt = _startedAt,
                    lastBeat = DateTime.Now
                });
                await _cache.SetExpireAsync(CacheConst.KeyJobNode, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时任务集群心跳写入失败(本实例 {InstanceId} 在集群视图中可能显示为过期)", _instanceId);
            }
        }

        /// <inheritdoc />
        public async Task<bool> RunNowAsync(string jobName)
        {
            if (!_jobTypes.ContainsKey(jobName))
            {
                _logger.LogWarning("RunNow 拒绝:任务 {Name} 未注册代码(无 [JobName] 实现类)", jobName);
                return false;
            }

            // 手动触发 = 立即执行、不看 Interval(契约不变)⇒ enforceInterval=false 跳过锁内复检;
            // 但本次触发时刻仍写入共享基准键与内存快照(FireAsync.RecordFiredAsync),保持「手动执行也计入上次执行时刻」
            return await FireAsync(jobName, DateTime.Now, "manual", CancellationToken.None, enforceInterval: false);
        }

        // 数据库快照加载/刷新:整体换新引用(volatile 原子换)。失败保留旧快照(首次失败 _dbLoaded 仍 false,每轮重试)。
        private async Task<bool> LoadSchedulesAsync(CancellationToken ct)
        {
            var serialHeld = false;
            try
            {
                // DB 串行锁:与任务执行/RunNow 互斥(共享 scope 实例并发不安全,见 _dbSerial 注释)
                await _dbSerial.WaitAsync(ct);
                serialHeld = true;

                using var scope = _rootProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
                // WHY 不向 SQL 传 ct(测试诊断实证):共享单例 SqlSugar 客户端(SqlSugarScope)跨执行缓存命令对象,
                // 传入的取消令牌会滞留其上——StopAsync 取消 stoppingToken 后,后续任何 SQL(含其他请求)直接 OCE,
                // 生产停止链路同险。循环级取消已由 Task.Delay(ct)+ 循环条件保证;预载查询毫秒级,传 token 无实际收益。
                var rows = await repo.AsQueryable().ToListAsync();
                ct.ThrowIfCancellationRequested();

                var firstLoad = !_dbLoaded;
                var map = new Dictionary<string, JobScheduleEntry>();
                var problems = new List<string>();
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.JobName))
                    {
                        continue;
                    }

                    if (!_jobTypes.ContainsKey(row.JobName))
                    {
                        problems.Add($"{row.JobName}(无代码实现)");
                        continue;
                    }

                    if (row.Interval <= 0)
                    {
                        problems.Add($"{row.JobName}(间隔非正)");
                        continue;
                    }

                    map[row.JobName] = new JobScheduleEntry(row.Interval, row.Enabled, row.LastRunTime, row.TargetNode);
                }

                _schedules = map;
                _dbLoaded = true;

                if (problems.Count > 0)
                {
                    _logger.LogWarning("定时任务数据库行被跳过:{Problems}", string.Join(" ", problems));
                }

                if (firstLoad)
                {
                    _logger.LogInformation("定时任务预载完成:数据库 {DbCount} 行 / 代码注册 {CodeCount} 个 / 生效 {EnabledCount} 个",
                        rows.Count, _jobTypes.Count, map.Values.Count(e => e.Enabled));
                }

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时任务数据库预载失败(服务继续运行,下一轮重试自愈)");
                return false;
            }
            finally
            {
                if (serialHeld)
                {
                    _dbSerial.Release();
                }
            }
        }

        private async Task SafeDelayAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_tickIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                // 宿主停止:由循环条件自然退出
            }
        }

        /// <summary>单任务调度快照(SysJobDetail 行 → 内存投影)。</summary>
        private sealed record JobScheduleEntry(int IntervalSeconds, bool Enabled, DateTime? LastRunTime, string? TargetNode);
    }
}
