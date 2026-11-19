using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Utils.ServerMonitor
{
    /// <summary>
    /// 服务器硬件/系统信息(批 Y 4.14):CPU/内存/磁盘。
    /// /proc 相关字段在 Linux 外平台为 null(DriveInfo 跨平台恒有)。
    /// </summary>
    public sealed class ServerHardwareOutput
    {
        /// <summary>CPU 型号(/proc/cpuinfo model name;非 Linux null)。</summary>
        public string? CpuModelName { get; set; }
        /// <summary>CPU 使用率(%;/proc/stat 双采样 delta;非 Linux 或采样失败 null)。</summary>
        public double? CpuUsagePercent { get; set; }
        public int ProcessorCount { get; set; }
        /// <summary>物理内存总量(字节;/proc/meminfo MemTotal;非 Linux null)。</summary>
        [JsonConverter(typeof(NullableLongNumberJsonConverter))]
        public long? MemoryTotalBytes { get; set; }
        /// <summary>可用内存(字节;/proc/meminfo MemAvailable)。</summary>
        [JsonConverter(typeof(NullableLongNumberJsonConverter))]
        public long? MemoryAvailableBytes { get; set; }
        /// <summary>内存使用率(%)。</summary>
        public double? MemoryUsagePercent { get; set; }
        public List<DiskInfoOutput> Disks { get; set; } = new();
    }

    /// <summary>磁盘(挂载点)信息。</summary>
    public sealed class DiskInfoOutput
    {
        /// <summary>挂载点/盘符根路径。</summary>
        public string Root { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsReady { get; set; }
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long TotalSize { get; set; }
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long AvailableFreeSpace { get; set; }
        public double? UsagePercent { get; set; }
    }
}
