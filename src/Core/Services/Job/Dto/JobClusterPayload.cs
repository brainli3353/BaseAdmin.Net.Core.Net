namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 集群节点/运行中任务的**缓存载荷**(批 AJ:Hash value 的 JSON 形状,仅供服务端读取解析)。
    /// 与对外输出 DTO 分开:载荷含读侧判定所需字段(lastBeat),输出为面向 UI 的投影。
    /// </summary>
    public class JobNodePayload
    {
        /// <summary>实例 Id(field 的同名副本,便于解析后直接使用)。</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>运维标签(JobCluster:NodeTag;缺省机器名)。</summary>
        public string NodeTag { get; set; } = string.Empty;

        /// <summary>实例启动时刻。</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>最后心跳时刻(读侧据此判存活)。</summary>
        public DateTime? LastBeat { get; set; }
    }

    /// <summary>运行中任务的缓存载荷(批 AJ)。</summary>
    public class JobRunningPayload
    {
        /// <summary>任务名(Hash field 的同名副本)。</summary>
        public string JobName { get; set; } = string.Empty;

        /// <summary>执行实例 Id。</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>执行实例标签。</summary>
        public string? NodeTag { get; set; }

        /// <summary>开始时刻。</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>触发方式(scheduled/manual)。</summary>
        public string? Trigger { get; set; }
    }
}
