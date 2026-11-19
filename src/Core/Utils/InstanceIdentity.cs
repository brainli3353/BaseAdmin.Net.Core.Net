using Microsoft.Extensions.Configuration;

namespace AdminCore.Core.Utils
{
    /// <summary>
    /// 实例身份单点(批 AT F1):集群可观测面的"我是谁"——进程级不变量(进程号/GUID/启动时刻)共享,
    /// 标签(<c>NodeTag</c>)按传入配置解析并按标签缓存(<see cref="Resolve"/> 幂等)。
    /// </summary>
    /// <remarks>
    /// WHY 需要单点:批 AJ 的调度节点表(`sys_job_node`)与批 AT 的监控快照表(`sys_server_metrics`)都要一个实例标识,
    /// 此前各自就地拼 GUID ⇒ 两表 field 对不上号,运维看到"调度视图里 3 台、监控视图里 3 台"却无法确认是不是同三台
    /// (尤其滚动发布时新旧实例交叠)。身份必须是**单点事实**,不是每个可观测件各造一个。
    ///
    /// 格式口径(与批 AJ 原样一致,零行为变更):`{NodeTag}:{进程号}:{GUID:N}`——
    /// 每次进程启动都不同(重启即新实例);`NodeTag` 取 `JobCluster:NodeTag`(缺省机器名),
    /// 沿用该配置键是为让"任务归属(§42.3)"与"监控节点标签"共用一个运维标签体系,不另立一套键(#11 复用优先)。
    ///
    /// WHY 按 **NodeTag** 缓存而不是"进程内只解析一次"(批 AT 本批回归实测修正):
    /// 同一个进程里可能承载多个逻辑实例标识——生产不会(一进程一配置),但**测试装置会**:
    /// 批 AJ/AK 的两个 <c>JobScheduler</c> 在同一测试进程内用不同 `JobCluster:NodeTag` 模拟"两实例"。
    /// 若整身份按进程单例缓存,第二个调度器会拿到与第一个**逐字相同**的 InstanceId ⇒
    /// 心跳互相覆盖(节点表只剩一行)、`isSelf` 标注错乱、跨实例重复触发判据失真
    /// (全量回归实测:批 AJ 4 例 + 批 AK 1 例转红)。按标签缓存后:标签不同 ⇒ 身份不同(测试语义恢复),
    /// 标签相同 ⇒ 返回**同一对象**(生产两处消费点逐字一致,两表可对号)。
    ///
    /// WHY 进程级不变量(pid/GUID/StartedAt)是静态共享的:它们是"这个进程"的事实,与标签无关——
    /// 保证任何一次解析出来的 Pid/StartedAt 都一致,不会因调用点不同而漂移。
    /// **StartedAt 的边界(批 AT 实现期修正)**:它是"进程启动时刻",**不是**"某个调度器实例的启动时刻"——
    /// 节点行(`sys_job_node`)的 `startedAt` 语义属调度器自身(见 `JobScheduler._startedAt` 注释),故那里不取本值;
    /// 否则全量套件里晚跑的用例会看到"启动于 10 分钟前"的自身节点行(批 AJ 用例 01 实机回归抓到)。
    ///
    /// 档位/边界:与缓存档位无关(纯进程内字符串);Memory 档下各表不跨进程,同差距清单 G18 口径。
    ///
    /// 用法:<c>var id = InstanceIdentity.Resolve(config);</c>(装配期/构造期调用,幂等)
    /// 或直接读 <see cref="Current"/>(= 按缺省标签解析,即机器名)。
    /// </remarks>
    public sealed record InstanceIdentity(string NodeTag, string InstanceId, int Pid, DateTime StartedAt)
    {
        private static readonly object Gate = new();

        // 进程级不变量:一次生成、全体共享(标签无关)
        private static readonly string ProcessGuid = Guid.NewGuid().ToString("N");
        private static readonly DateTime ProcessStartedAt = DateTime.Now;
        private static readonly int ProcessId = Environment.ProcessId;

        // 按标签缓存:同标签幂等返回同一对象(生产:单条);不同标签各自成身份(测试装置:双实例模拟)
        private static readonly Dictionary<string, InstanceIdentity> Cache = new(StringComparer.Ordinal);

        /// <summary>
        /// 本进程按**缺省标签**(机器名)解析的身份;等价于 <c>Resolve(null)</c>(幂等,同一对象)。
        /// </summary>
        public static InstanceIdentity Current => Resolve(null);

        /// <summary>
        /// 解析本实例身份(按标签幂等:同 <c>JobCluster:NodeTag</c> 重复调用返回**同一实例**)。
        /// </summary>
        /// <param name="config">配置根(可为 null——最小宿主/单测容器未注册时按缺省机器名解析)。</param>
        public static InstanceIdentity Resolve(IConfiguration? config)
        {
            var nodeTag = config?["JobCluster:NodeTag"] is { Length: > 0 } tag
                ? tag
                : Environment.MachineName;

            lock (Gate)
            {
                if (Cache.TryGetValue(nodeTag, out var cached))
                {
                    return cached;
                }

                var created = new InstanceIdentity(
                    nodeTag,
                    $"{nodeTag}:{ProcessId}:{ProcessGuid}",
                    ProcessId,
                    ProcessStartedAt);

                Cache[nodeTag] = created;
                return created;
            }
        }
    }
}