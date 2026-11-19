using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Utils.ServerMonitor
{
    /// <summary>
    /// 服务器运行时信息(批 Y 4.14):进程/内存/GC/线程/OS 单点快照。
    /// </summary>
    public sealed class ServerRuntimeOutput
    {
        public int Pid { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        /// <summary>已运行时长(格式化串 d.dd hh:mm:ss)。</summary>
        public string Uptime { get; set; } = string.Empty;
        public string DotNetVersion { get; set; } = string.Empty;
        public string OsDescription { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        /// <summary>工作集(物理内存,字节)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long WorkingSetBytes { get; set; }
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long PrivateMemoryBytes { get; set; }
        /// <summary>托管堆内存(字节,GC.GetTotalMemory)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long ManagedMemoryBytes { get; set; }
        public int GcGen0 { get; set; }
        public int GcGen1 { get; set; }
        public int GcGen2 { get; set; }
        public bool IsServerGC { get; set; }
        /// <summary>线程数(Linux 下 Process.Threads 语义受限,不可用为 null)。</summary>
        public int? ThreadCount { get; set; }
        public int ProcessorCount { get; set; }
    }
}
