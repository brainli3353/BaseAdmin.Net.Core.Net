using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace AdminCore.Core.Utils.ServerMonitor
{
    /// <summary>
    /// 服务器信息采集器(批 Y 4.14,参照 Admin.NET HardwareInfoManager/RuntimeInfoManger 移植,
    /// 按本仓平台裁剪:Linux 容器为主——无 GPU/主板/温度,单点快照不连续采样)。
    /// 只读诊断信息,零外部依赖:System.Diagnostics + /proc(Linux) + DriveInfo(跨平台)。
    /// /proc 解析逻辑独立为纯函数(收字符串入参,单测可固定文本直测,不依赖宿主平台)。
    /// 非 Linux 平台(开发机 Windows 等)/proc 相关字段回落 null,不抛异常。
    /// </summary>
    public static class ServerInfoCollector
    {
        /// <summary>
        /// 运行时信息(进程/内存/GC/线程/OS)。
        /// </summary>
        public static ServerRuntimeOutput GetRuntimeInfo()
        {
            var proc = Process.GetCurrentProcess();
            int? threadCount = null;
            try
            {
                // Linux 下 Process.Threads 语义受限(仅托管线程可见且部分环境抛异常)——try 回落 null
                threadCount = proc.Threads.Count;
            }
            catch
            {
                threadCount = null;
            }

            return new ServerRuntimeOutput
            {
                Pid = Environment.ProcessId,
                ProcessName = Path.GetFileName(Environment.ProcessPath ?? string.Empty),
                StartTime = proc.StartTime,
                Uptime = FormatUptime(DateTime.Now - proc.StartTime),
                DotNetVersion = Environment.Version.ToString(),
                OsDescription = RuntimeInformation.OSDescription,
                HostName = Environment.MachineName,
                WorkingSetBytes = proc.WorkingSet64,
                PrivateMemoryBytes = proc.PrivateMemorySize64,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                GcGen0 = GC.CollectionCount(0),
                GcGen1 = GC.CollectionCount(1),
                GcGen2 = GC.CollectionCount(2),
                IsServerGC = GCSettings.IsServerGC,
                ThreadCount = threadCount,
                ProcessorCount = Environment.ProcessorCount
            };
        }

        /// <summary>
        /// 硬件/系统信息(CPU/内存/磁盘)。/proc 不可用时对应字段 null(DriveInfo 跨平台恒可用)。
        /// CPU 使用率 = /proc/stat 首行两次采样 delta(约 200ms 窗口)——单点无 delta 概念。
        /// </summary>
        public static ServerHardwareOutput GetHardwareInfo()
        {
            var output = new ServerHardwareOutput
            {
                ProcessorCount = Environment.ProcessorCount
            };

            if (OperatingSystem.IsLinux())
            {
                string? cpuinfo = TryRead("/proc/cpuinfo");
                if (cpuinfo != null)
                {
                    output.CpuModelName = ParseCpuModelName(cpuinfo);
                    output.CpuUsagePercent = SampleCpuUsagePercent(200);
                }

                string? meminfo = TryRead("/proc/meminfo");
                if (meminfo != null)
                {
                    var (totalBytes, availableBytes) = ParseMemInfo(meminfo);
                    output.MemoryTotalBytes = totalBytes;
                    output.MemoryAvailableBytes = availableBytes;
                    if (totalBytes > 0)
                    {
                        var total = totalBytes.Value;
                        var used = total - (availableBytes ?? 0);
                        output.MemoryUsagePercent = Math.Round(used * 100.0 / total, 1);
                    }
                }
            }

            // 磁盘:DriveInfo 跨平台(Linux=全部挂载点,Windows=盘符);逐盘 try——个别挂载点权限不足不炸整体
            // 批 AT F2:**只报可报告文件系统**——容器内挂载点可达 70+ 条(proc/sysfs/tmpfs/cgroup…),单机诊断还能翻,
            //   聚合到"每节点一行"的集群视图就完全不可用(指南 §27.3 该口径同批改判)
            foreach (var drive in SafeGetDrives())
            {
                string driveFormat;
                try
                {
                    driveFormat = drive.DriveFormat;
                }
                catch
                {
                    driveFormat = string.Empty; // 取不到类型 ⇒ 保守保留(真盘偶发权限抖动不该把盘抹掉)
                }

                if (!IsReportableFileSystem(driveFormat))
                {
                    continue;
                }

                try
                {
                    var total = drive.TotalSize;
                    var free = drive.AvailableFreeSpace;
                    output.Disks.Add(new DiskInfoOutput
                    {
                        Root = drive.RootDirectory.ToString(),
                        Name = drive.Name,
                        IsReady = drive.IsReady,
                        TotalSize = total,
                        AvailableFreeSpace = free,
                        UsagePercent = total > 0 ? Math.Round((total - free) * 100.0 / total, 1) : null
                    });
                }
                catch
                {
                    output.Disks.Add(new DiskInfoOutput
                    {
                        Root = drive.RootDirectory.ToString(),
                        Name = drive.Name,
                        IsReady = false
                    });
                }
            }

            // 批 BQ D1:**无容量语义条目过滤**——IsReportableFileSystem 的「类型取不到⇒保守保留」边界
            // 在 docker 宿主实锤失守:nsfs/tracefs/overlayfs 层挂载的 DriveFormat 取不到(异常/空)⇒ 绕过
            // 排除表进清单,且这些条目 TotalSize/Free 恒 0(无容量可读)⇒ 单机视图 21 条里 17 条是垃圾行
            // (真机 e2e 实锤)。这类条目在视图里本不可用(全 0/空),过滤不是"把盘抹掉"——可显示容量的真盘不受影响。
            output.Disks = output.Disks.Where(HasCapacitySemantics).ToList();

            return output;
        }

        /// <summary>
        /// 该磁盘条目是否具备容量语义(批 BQ D1 纯函数,单测直测):
        /// <c>!IsReady &amp;&amp; TotalSize==0 &amp;&amp; AvailableFreeSpace==0</c> ⇒ 无(伪挂载/不可读条目,不上报)。
        /// 三条件全中才滤——任一条件能给出数据(哪怕 IsReady=true 但容量 0 的极小卷)都保留。
        /// </summary>
        public static bool HasCapacitySemantics(DiskInfoOutput disk)
        {
            return disk.IsReady || disk.TotalSize > 0 || disk.AvailableFreeSpace > 0;
        }

        /// <summary>
        /// 框架已加载程序集清单(参照 GetNuGetPackages 口径:只列本框架家族,非全量 BCL)。
        /// </summary>
        public static List<ServerAssemblyOutput> GetFrameworkAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && (a.GetName().Name ?? string.Empty).StartsWith("AdminCore", StringComparison.OrdinalIgnoreCase))
                .Select(a =>
                {
                    var name = a.GetName();
                    return new ServerAssemblyOutput
                    {
                        Name = name.Name ?? string.Empty,
                        Version = name.Version?.ToString() ?? string.Empty,
                        FullName = a.FullName ?? string.Empty
                    };
                })
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 运行时长格式化 → "Nd HH:mm:ss"(如 "3d 07:12:45";不足 1 天 "0d ...")。
        /// 纯函数,单测直测(TimeSpan 标准格式串对独立 d 投机非法——历史坑,插值口径规避)。
        /// </summary>
        public static string FormatUptime(TimeSpan uptime)
        {
            var up = uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
            return $"{(long)up.TotalDays}d {up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
        }

        /// <summary>
        /// 该文件系统类型是否值得上报(批 AT F2 纯函数,单测直测固定文本/字符串)。
        /// 排除表＝Linux 常见伪文件系统(无容量语义或非持久盘);保留 ext2/3/4、xfs、btrfs、zfs、
        /// ntfs、fat/exfat,以及 **overlay**(容器根盘——把它滤掉等于没看这台机器)。
        /// 类型取不到(空串/异常)时返回 true:保守保留——真盘偶发的权限抖动不该把盘抹掉。
        /// </summary>
        public static bool IsReportableFileSystem(string? driveFormat)
        {
            if (string.IsNullOrWhiteSpace(driveFormat))
            {
                return true;
            }

            return !PseudoFileSystems.Contains(driveFormat.Trim().ToLowerInvariant());
        }

        // 伪文件系统排除表(小写比对;来自 /proc/mounts 常见项,Windows 侧 DriveFormat 恒为 NTFS/FAT 系不命中)
        private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal)
        {
            "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "cgroup", "cgroup2", "pstore",
            "securityfs", "debugfs", "tracefs", "configfs", "fusectl", "mqueue", "shm",
            "binfmt_misc", "autofs", "squashfs", "efivarfs", "nsfs", "rpc_pipefs", "selinuxfs",
            "hugetlbfs", "bpf", "ramfs", "9p", "fuse.lxcfs", "fuse.gvfsd-fuse", "fuse.portalfs"
        };

        // ───────────────────────── /proc 解析纯函数(单测直测) ─────────────────────────

        /// <summary>
        /// 解析 /proc/cpuinfo 的型号(取第一个 "model name" 行的冒号后内容;无则 null)。
        /// </summary>
        public static string? ParseCpuModelName(string cpuinfo)
        {
            foreach (var raw in cpuinfo.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    var idx = line.IndexOf(':', StringComparison.Ordinal);
                    return idx >= 0 ? line[(idx + 1)..].Trim() : null;
                }
            }
            return null;
        }

        /// <summary>
        /// 解析 /proc/stat 首行 CPU 时间片的"已用 jiffies 占比"(0–100)。
        /// 行格式:cpu user nice system idle iowait irq softirq steal ...
        /// used = total - idle - iowait;入参为两次采样快照,返回区间使用率。
        /// </summary>
        public static double? ParseStatUsagePercent(string statFirst, string statSecond)
        {
            var f = ParseStatLine(statFirst);
            var s = ParseStatLine(statSecond);
            if (f == null || s == null)
            {
                return null;
            }

            var (fTotal, fIdle) = f.Value;
            var (sTotal, sIdle) = s.Value;
            var dTotal = sTotal - fTotal;
            if (dTotal <= 0)
            {
                return null;
            }

            var dIdle = sIdle - fIdle;
            var used = (dTotal - dIdle) * 100.0 / dTotal;
            return Math.Clamp(Math.Round(used, 1), 0, 100);
        }

        /// <summary>
        /// 解析 /proc/meminfo 的 MemTotal/MemAvailable(kB → bytes;缺失项 null)。
        /// </summary>
        public static (long? TotalBytes, long? AvailableBytes) ParseMemInfo(string meminfo)
        {
            long? total = null;
            long? available = null;
            foreach (var raw in meminfo.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemKb(line);
                }

                if (total != null && available != null)
                {
                    break;
                }
            }

            return (total, available);
        }

        // ───────────────────────── 内部实现 ─────────────────────────

        /// <summary>采样 /proc/stat 两次(间隔 ms)求 CPU 使用率;任一读取失败返回 null。</summary>
        private static double? SampleCpuUsagePercent(int intervalMs)
        {
            var first = TryRead("/proc/stat");
            if (first == null)
            {
                return null;
            }

            Thread.Sleep(intervalMs);
            var second = TryRead("/proc/stat");
            return second == null ? null : ParseStatUsagePercent(first, second);
        }

        private static string? TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析 "MemTotal:  16384 kB" → 16384*1024;格式异常返回 null。</summary>
        private static long? ParseMemKb(string line)
        {
            // 格式固定:字段名: 数值 [单位]
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return null;
            }

            return long.TryParse(parts[1], out var kb) ? kb * 1024 : null;
        }

        /// <summary>解析 /proc/stat 首行 → (totalJiffies, idleJiffies);非 cpu 汇总行返回 null。</summary>
        private static (long Total, long Idle)? ParseStatLine(string statContent)
        {
            var firstLine = statContent.Split('\n', 2)[0];
            if (!firstLine.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var fields = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                return null;
            }

            var values = fields[1..]
                .Select(v => long.TryParse(v, out var n) ? n : 0L)
                .ToList();
            var total = values.Sum();
            // idle = 第 4 列(idle)+ 第 5 列(iowait,存在时)
            var idle = values.Count > 3 ? values[3] : 0L;
            if (values.Count > 4)
            {
                idle += values[4];
            }

            return (total, idle);
        }

        private static DriveInfo[] SafeGetDrives()
        {
            try
            {
                return DriveInfo.GetDrives();
            }
            catch
            {
                return Array.Empty<DriveInfo>();
            }
        }
    }
}
