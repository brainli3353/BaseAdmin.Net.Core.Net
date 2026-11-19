namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务集群视图(批 AJ 5.16):{nodes, running}(JSON camelCase)。
    /// nodes=实例节点清单(含本实例标注;僵尸节点已按心跳超期过滤);
    /// running=运行中任务清单(哪个实例正在跑哪个任务)。
    /// </summary>
    public sealed record JobClusterOutput(List<JobNodeOutput> Nodes, List<JobRunningOutput> Running);

    /// <summary>
    /// 集群节点行 {instanceId, nodeTag, startedAt, lastBeat, isAlive, isSelf}。
    /// isAlive=最后一次心跳在超时窗口内(读侧判据);isSelf=是否本进程(便于运维对照)。
    /// </summary>
    public sealed record JobNodeOutput(string InstanceId, string NodeTag, DateTime? StartedAt, DateTime? LastBeat, bool IsAlive, bool IsSelf);

    /// <summary>运行中任务行 {jobName, instanceId, nodeTag, startedAt, trigger}(trigger=scheduled/manual)。</summary>
    public sealed record JobRunningOutput(string JobName, string InstanceId, string? NodeTag, DateTime? StartedAt, string? Trigger);
}
