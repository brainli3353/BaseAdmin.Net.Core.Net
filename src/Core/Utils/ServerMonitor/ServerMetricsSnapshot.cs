namespace AdminCore.Core.Utils.ServerMonitor
{
    /// <summary>
    /// 自报监控快照**载荷**(批 AT F3):<c>sys_server_metrics</c> Hash 的一条 field 的 JSON 形状。
    /// 与对外 Dto(<c>ServerNodeOutput</c>)分开——载荷＝写入形态(采集原文),Dto＝面向 UI 的投影(含存活/本实例判定)。
    /// </summary>
    /// <remarks>
    /// WHY 载荷内重复 <see cref="InstanceId"/>:<c>ICacheService.HashGetAllAsync&lt;T&gt;</c> 只回 <c>List&lt;T&gt;</c>、
    /// **不携带 field**(批 AJ 心跳同因实测)⇒ 读侧要按实例对号与判「本实例」,标识必须在值里。
    /// WHY 直接挂三个采集原文对象(不再摊平):采集器是快照的**唯一形状来源**,再摊一次只会制造两份真相;
    /// 载荷体量很小(运行时 17 字段＋硬件若干＋裁剪后磁盘数条＋框架程序集 4-6 条),按节点各存一份不构成负担。
    /// WHY 传对象而非预序列化串:<c>HashSetAsync(key, field, object)</c> 由各实现自己序列化一次——
    /// 预序列化会变成"字符串里嵌 JSON"(Memory 档读侧反序列化失败,批 AJ e2e 实证),两档形态必须一致。
    /// </remarks>
    public sealed class ServerMetricsSnapshot
    {
        /// <summary>实例标识(<c>{NodeTag}:{Pid}:{GUID}</c>,同 <c>InstanceIdentity</c>);也是 Hash 的 field 名。</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>运维标签(<c>JobCluster:NodeTag</c>,缺省机器名)——与调度节点表同源,两视图可对号。</summary>
        public string NodeTag { get; set; } = string.Empty;

        /// <summary>本次上报时刻(读侧按它判"死节点":超 <c>ServerNodeService.StaleSeconds</c> 即从视图隐藏)。</summary>
        public DateTime ReportAt { get; set; }

        /// <summary>运行时快照(进程/内存/GC/线程/OS;采集失败可为 null,不炸整体)。</summary>
        public ServerRuntimeOutput? Runtime { get; set; }

        /// <summary>硬件快照(CPU/内存/磁盘——磁盘已按文件系统类型裁剪,批 AT F2)。</summary>
        public ServerHardwareOutput? Hardware { get; set; }

        /// <summary>框架程序集清单(<c>AdminCore.*</c> 家族;集群下顺带回答"各实例是不是同一个版本")。</summary>
        public List<ServerAssemblyOutput> Assemblies { get; set; } = new();
    }
}
