using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Utils.ServerMonitor
{
    /// <summary>
    /// 服务器监控**自报**器(批 AT F3):每实例周期采集自身快照写入共享缓存,
    /// 让"服务器监控"从"谁应答看谁"升级成集群多节点视图(聚合读口见 <c>SysServerNodeService</c>)。
    /// </summary>
    /// <remarks>
    /// WHY 自报而非扇出:另一种做法是聚合实例逐个 HTTP 调各节点的 <c>api/sysServer/*</c>——那要节点地址表＋内部鉴权
    /// ＋超时与部分失败处理,且 <c>hardware</c> 的 CPU 双采样有 200ms 阻塞窗口,串行扇出就是 <c>200ms × N</c>。
    /// 自报路线零跨实例调用、零新增攻击面,节点存活判据与批 AJ 心跳同构(运维心智一致),代价只是"粒度＝上报周期"
    /// ——本域定位本来就是**单点快照诊断**(§27.1「不连续采样,前端轮询即面板」),不是秒级监控。
    ///
    /// 周期与存活边界:<see cref="ReportInterval"/> 30s 上报一次、键 TTL <see cref="SnapshotTtl"/> 90s(3 个周期余量,
    /// 防一次抖动丢节点);**死节点不靠 TTL 摘除**,由读侧按 <c>reportAt</c> 超期隐藏(同 §42.5 坑 7 口径)。
    /// 宿主正常停止时主动删自己那条 field(滚动发布后视图立刻干净);异常崩溃则由 TTL 与读侧超期共同兜住。
    ///
    /// 失败语义:上报异常只 LogWarning **不阻断**——可观测性件不得改变业务语义(同批 AJ 心跳/续租口径);
    /// 缓存不可用时该实例在集群视图里缺席,但仍可从 <c>api/sysServer/runtime|hardware|assembly</c> 看它的即时单点。
    ///
    /// 档位边界:Memory 档键不跨进程 ⇒ 集群视图只看得到"应答的那个实例"(等价于改造前行为,不假装有集群),
    /// 多实例必须 Redis 档(同差距清单 G18／指南 §46.2 第 1 条)。
    ///
    /// 注册(§99③ 零改 Program.cs):<c>[Service]</c> 锁定 ServiceType＝具体类＋Singleton,
    /// AddAutoServices 的 HostedService 分支据此补注册 IHostedService(与 JobScheduler/WorkerIdGuard 同模式)。
    /// </remarks>
    [Service(ServiceType = typeof(ServerMetricsReporter), Lifetime = ServiceLifetime.Singleton)]
    public sealed class ServerMetricsReporter : BackgroundService
    {
        /// <summary>上报周期(批 AT 定案)= 30 秒。粒度换负载:面板"≤30s 前"的标注比多花 N 倍采集更诚实。</summary>
        public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

        /// <summary>快照键 TTL(批 AT 定案)= 90 秒 = 3 × 周期:防一次上报抖动就让整台实例在视图里消失。</summary>
        public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(90);

        private readonly ICacheService _cache;
        private readonly ILogger<ServerMetricsReporter> _logger;
        private readonly InstanceIdentity _identity;

        public ServerMetricsReporter(IServiceProvider rootProvider, ICacheService cache, ILogger<ServerMetricsReporter> logger)
        {
            // 身份取单点(批 AT F1;IConfiguration 可选解析,口径同 JobScheduler)
            _identity = InstanceIdentity.Resolve(rootProvider.GetService<IConfiguration>());
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReportOnceAsync();

                try
                {
                    await Task.Delay(ReportInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 宿主停止:退出循环走 finally 摘除
                }
            }

            // 正常停止:摘掉自己的 field(滚动发布后集群视图不该留旧实例那一行)
            try
            {
                await _cache.HashDeleteAsync(CacheConst.KeyServerMetrics, _identity.InstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务器监控快照摘除失败(本实例 {InstanceId} 那行将由读侧超期隐藏兜住)", _identity.InstanceId);
            }
        }

        /// <summary>
        /// 采集并上报一轮(公开供单测直调一次,不必等 30s 周期;<see cref="ExecuteAsync"/> 循环内亦调它)。
        /// 内部全捕获:返回 false 表示本轮未写入(缓存异常),不向上抛。
        /// </summary>
        public async Task<bool> ReportOnceAsync()
        {
            try
            {
                var snapshot = new ServerMetricsSnapshot
                {
                    InstanceId = _identity.InstanceId,
                    NodeTag = _identity.NodeTag,
                    ReportAt = DateTime.Now,
                    Runtime = ServerInfoCollector.GetRuntimeInfo(),
                    Hardware = ServerInfoCollector.GetHardwareInfo(),
                    Assemblies = ServerInfoCollector.GetFrameworkAssemblies()
                };

                await _cache.HashSetAsync(CacheConst.KeyServerMetrics, _identity.InstanceId, snapshot);
                await _cache.SetExpireAsync(CacheConst.KeyServerMetrics, SnapshotTtl);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务器监控快照上报失败(本实例 {InstanceId} 在集群视图可能显示为过期;采集与业务语义无关,不重试不外抛)", _identity.InstanceId);
                return false;
            }
        }
    }
}
