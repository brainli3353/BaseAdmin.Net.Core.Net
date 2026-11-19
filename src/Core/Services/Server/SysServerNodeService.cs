using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Services.Server.Dto;
using AdminCore.Core.Utils;
using AdminCore.Core.Utils.ServerMonitor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdminCore.Core.Services.Server
{
    /// <summary>
    /// 集群节点监控服务(批 AT F4):把各实例自报的 <c>sys_server_metrics</c> 快照读成一张节点表——
    /// 让"服务器监控"从「谁应答看谁」变成「看全部实例，并说清你看的是谁」。只读零写。
    /// </summary>
    /// <remarks>
    /// 存活判据取**读侧按 <c>reportAt</c> 超期隐藏**(与批 AJ 集群视图 §42.4 完全同构):
    /// TTL 只负责防键泄漏,不负责摘除死节点——两件事分开,崩溃实例才不会在视图里赖 90 秒以上、
    /// 也不会因为一次网络抖动就消失(<c>ServerMetricsReporter.SnapshotTtl</c> 留了 3 个周期余量)。
    ///
    /// 载荷缺标识(<c>InstanceId</c> 空)的行**直接跳过**:既无法判"是否本实例",也无法与调度节点表对号,
    /// 留在表里只会制造"不知道是谁"的行(fail-loud 于日志之外,这里选择响亮地不显示)。
    ///
    /// 档位边界:Memory 档下这张表只看得到"执行本次读请求的那个实例"(等价改造前行为,不假装有集群)——
    /// 多实例必须 Redis 档(差距清单 G18／指南 §46.2 第 1 条)。
    /// </remarks>
    [Service]
    public class SysServerNodeService
    {
        /// <summary>快照过期阈值(秒,批 AT 定案)= 90 = 3 × 上报周期(30s)：与 <c>ServerMetricsReporter</c> 同口径,单点常量防漂移。</summary>
        public const int StaleSeconds = 90;

        private readonly ICacheService _cache;

        // 本实例标识:批 AT F1 单点,但**按配置解析**(不是 `InstanceIdentity.Current` 的缺省机器名标签)——
        // WHY:节点行的标签来自 `JobCluster:NodeTag`,若用缺省标签比较,配了标签的实例会把真实本机判成"别人"
        // (真机 e2e 实测:isSelf 全 false)。按配置解析后与上报器/调度器**同源**,且不依赖谁先解析(无顺序耦合)。
        private readonly string _selfInstanceId;

        public SysServerNodeService(IServiceProvider rootProvider, ICacheService cache)
        {
            // IConfiguration 可选解析(最小宿主/单测容器可能未注册 → 回落机器名标签),口径同 JobScheduler
            _selfInstanceId = InstanceIdentity.Resolve(rootProvider.GetService<IConfiguration>()).InstanceId;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// 全部存活节点(按 NodeTag、InstanceId 升序;"本实例"不置顶——排序稳定比"谁在报"更重要,前端用 <c>isSelf</c> 标)。
        /// </summary>
        public async Task<List<ServerNodeOutput>> GetNodesAsync()
        {
            var snapshots = await _cache.HashGetAllAsync<ServerMetricsSnapshot>(CacheConst.KeyServerMetrics);
            var now = DateTime.Now;
            var self = _selfInstanceId;
            var stale = TimeSpan.FromSeconds(StaleSeconds);

            var nodes = new List<ServerNodeOutput>(snapshots.Count);
            foreach (var snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.InstanceId))
                {
                    continue; // 载荷无身份:无法对号与判本实例,跳过(见 remarks)
                }

                if (now - snapshot.ReportAt > stale)
                {
                    continue; // 死节点(或长时间没上报)读侧隐藏,同 §42.4 口径
                }

                nodes.Add(Map(snapshot, string.Equals(snapshot.InstanceId, self, StringComparison.Ordinal)));
            }

            return nodes
                .OrderBy(n => n.NodeTag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.InstanceId, StringComparer.Ordinal)
                .ToList();
        }

        // 载荷 → 节点行(平铺派生字段单点算这里,前端只渲染不再派生)
        internal static ServerNodeOutput Map(ServerMetricsSnapshot snapshot, bool isSelf)
        {
            var runtime = snapshot.Runtime;
            var hardware = snapshot.Hardware;
            var disks = hardware?.Disks;

            double? diskMax = null;
            if (disks is { Count: > 0 })
            {
                var usages = disks.Where(d => d.UsagePercent is not null).Select(d => d.UsagePercent!.Value).ToList();
                if (usages.Count > 0)
                {
                    diskMax = usages.Max();
                }
            }

            return new ServerNodeOutput
            {
                InstanceId = snapshot.InstanceId,
                NodeTag = snapshot.NodeTag,
                HostName = runtime?.HostName ?? string.Empty,
                Pid = runtime?.Pid ?? 0,
                StartedAt = runtime?.StartTime,
                Uptime = runtime?.Uptime ?? string.Empty,
                DotNetVersion = runtime?.DotNetVersion ?? string.Empty,
                OsDescription = runtime?.OsDescription ?? string.Empty,
                WorkingSetBytes = runtime?.WorkingSetBytes ?? 0,
                ManagedMemoryBytes = runtime?.ManagedMemoryBytes ?? 0,
                IsServerGC = runtime?.IsServerGC ?? false,
                GcGen0 = runtime?.GcGen0 ?? 0,
                GcGen1 = runtime?.GcGen1 ?? 0,
                GcGen2 = runtime?.GcGen2 ?? 0,
                CpuUsagePercent = hardware?.CpuUsagePercent,
                ProcessorCount = hardware?.ProcessorCount ?? runtime?.ProcessorCount ?? 0,
                MemoryTotalBytes = hardware?.MemoryTotalBytes,
                MemoryUsagePercent = hardware?.MemoryUsagePercent,
                DiskCount = disks?.Count ?? 0,
                DiskMaxUsagePercent = diskMax,
                CoreVersion = snapshot.Assemblies.FirstOrDefault(a =>
                    string.Equals(a.Name, "AdminCore.Core", StringComparison.OrdinalIgnoreCase))?.Version ?? string.Empty,
                AssemblyCount = snapshot.Assemblies.Count,
                ReportAt = snapshot.ReportAt,
                IsSelf = isSelf,
                Runtime = runtime,
                Hardware = hardware,
                Assemblies = snapshot.Assemblies
            };
        }
    }
}
