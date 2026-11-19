using System.Text.Json.Serialization;
using AdminCore.Core.Json;
using AdminCore.Core.Utils.ServerMonitor;

namespace AdminCore.Core.Services.Server.Dto
{
    /// <summary>
    /// 集群节点监控行(批 AT F4):<c>GET api/sysServer/nodes</c> 的一条记录＝一个实例自报的快照投影。
    /// 平铺字段供**节点表**渲染,末尾三块原样载荷供**详情区**渲染同一节点(读一次、不错配)。
    /// </summary>
    /// <remarks>
    /// WHY 平铺＋嵌套并存:节点表要按列排序/比对(CPU/内存/最满盘),详情要逐字段展示——
    /// 只给嵌套就得在前端做一遍派生(两份真相),只给平铺就丢掉了磁盘明细与程序集清单。
    /// 平铺字段全部**由载荷派生**(单点计算在 <c>SysServerNodeService.Map</c>),不存在漂移可能。
    /// </remarks>
    public sealed class ServerNodeOutput
    {
        /// <summary>实例标识 <c>{NodeTag}:{Pid}:{GUID}</c>(与调度节点表 <c>sys_job_node</c> 同源可对号)。</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>运维标签(<c>JobCluster:NodeTag</c>,缺省机器名)。</summary>
        public string NodeTag { get; set; } = string.Empty;

        /// <summary>主机名(采集自 <c>Environment.MachineName</c>,与 NodeTag 可不同——标签是人为配的)。</summary>
        public string HostName { get; set; } = string.Empty;

        /// <summary>进程号。</summary>
        public int Pid { get; set; }

        /// <summary>进程启动时刻(取运行时快照 StartTime)。</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>运行时长文本("3d 07:12:45")。</summary>
        public string Uptime { get; set; } = string.Empty;

        /// <summary>.NET 版本。</summary>
        public string DotNetVersion { get; set; } = string.Empty;

        /// <summary>OS 描述。</summary>
        public string OsDescription { get; set; } = string.Empty;

        /// <summary>工作集(字节)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long WorkingSetBytes { get; set; }

        /// <summary>托管堆(字节)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long ManagedMemoryBytes { get; set; }

        /// <summary>GC 模式(Server/Workstation)。</summary>
        public bool IsServerGC { get; set; }

        /// <summary>GC 0/1/2 代回收次数。</summary>
        public int GcGen0 { get; set; }
        public int GcGen1 { get; set; }
        public int GcGen2 { get; set; }

        /// <summary>CPU 使用率(%;非 Linux 或采集失败 null)。</summary>
        public double? CpuUsagePercent { get; set; }

        /// <summary>逻辑核数。</summary>
        public int ProcessorCount { get; set; }

        /// <summary>物理内存总量(字节;非 Linux null)。</summary>
        [JsonConverter(typeof(NullableLongNumberJsonConverter))]
        public long? MemoryTotalBytes { get; set; }

        /// <summary>内存使用率(%)。</summary>
        public double? MemoryUsagePercent { get; set; }

        /// <summary>可报告磁盘数(已按文件系统类型裁剪,批 AT F2)。</summary>
        public int DiskCount { get; set; }

        /// <summary>最满那块盘的占用率(%;无盘或无有效值 null)——集群表格里"该关心哪台"的第一眼指标。</summary>
        public double? DiskMaxUsagePercent { get; set; }

        /// <summary>框架版本(<c>AdminCore.Core</c> 程序集版本;多实例滚动发布时用来确认"是否同版本")。</summary>
        public string CoreVersion { get; set; } = string.Empty;

        /// <summary>已加载框架程序集条数。</summary>
        public int AssemblyCount { get; set; }

        /// <summary>本次快照上报时刻(粒度＝上报周期,缺省 30s)。</summary>
        public DateTime ReportAt { get; set; }

        /// <summary>是否本进程(端点在哪个实例上执行,那个实例即 true)。</summary>
        public bool IsSelf { get; set; }

        /// <summary>运行时明细(详情区渲染;采集失败可为 null)。</summary>
        public ServerRuntimeOutput? Runtime { get; set; }

        /// <summary>硬件明细(含裁剪后的磁盘列表;详情区渲染)。</summary>
        public ServerHardwareOutput? Hardware { get; set; }

        /// <summary>框架程序集清单(详情区渲染)。</summary>
        public List<ServerAssemblyOutput> Assemblies { get; set; } = new();
    }
}
