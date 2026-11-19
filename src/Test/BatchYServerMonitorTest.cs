using AdminCore.Core.Services.Server;
using AdminCore.Core.Utils.ServerMonitor;
using AdminCore.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 Y 4.14 服务器监控测试:/proc 解析纯函数(固定文本)+ 全路径采集(Linux 容器)+
    /// DI 解析(SysServerService 无状态无 DB 依赖,轻量容器)。
    /// </summary>
    public class BatchYServerMonitorTest : IDisposable
    {
        private IServiceCollection? _services;
        private ServiceProvider? _provider;

        public void Dispose()
        {
            _provider?.Dispose();
        }

        // ───────────────────────── /proc 解析纯函数(平台无关,固定文本直测) ─────────────────────────

        [Fact(DisplayName = "FormatUptime: 天/时分秒格式;负值钳零")]
        public void FormatUptime_格式()
        {
            Assert.Equal("3d 07:12:45", ServerInfoCollector.FormatUptime(TimeSpan.FromDays(3) + TimeSpan.FromHours(7) + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(45)));
            Assert.Equal("0d 00:00:01", ServerInfoCollector.FormatUptime(TimeSpan.FromSeconds(1)));
            Assert.Equal("0d 00:00:00", ServerInfoCollector.FormatUptime(TimeSpan.FromSeconds(-5)));
        }

        [Fact(DisplayName = "ParseCpuModelName: 取 model name 冒号后内容")]
        public void ParseCpuModelName_取型号()
        {
            const string cpuinfo = "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz\n\nprocessor\t: 1\nmodel name\t: Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz\n";
            Assert.Equal("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", ServerInfoCollector.ParseCpuModelName(cpuinfo));
        }

        [Fact(DisplayName = "ParseCpuModelName: 无 model name 行 → null")]
        public void ParseCpuModelName_无型号行()
        {
            Assert.Null(ServerInfoCollector.ParseCpuModelName("processor\t: 0\nvendor_id\t: ARM\n"));
        }

        [Fact(DisplayName = "ParseStatUsagePercent: 双快照 delta 算使用率")]
        public void ParseStatUsagePercent_双快照()
        {
            // 首拍: user=100 nice=0 system=50 idle=400 iowait=50 irq=0 softirq=0 steal=0 → total=600 idle=450
            // 次拍: user=200 nice=0 system=100 idle=550 iowait=50 irq=0 softirq=0 steal=0 → total=900 idle=600
            // dTotal=300 dIdle=150 → used=(300-150)*100/300=50%
            var first = "cpu  100 0 50 400 50 0 0 0 0 0\n";
            var second = "cpu  200 0 100 550 50 0 0 0 0 0\n";
            Assert.Equal(50.0, ServerInfoCollector.ParseStatUsagePercent(first, second));
        }

        [Fact(DisplayName = "ParseStatUsagePercent: 全忙(idle 不涨) → 100;无 delta → null;坏行 → null")]
        public void ParseStatUsagePercent_边界()
        {
            var first = "cpu  100 0 50 400 50 0 0 0 0 0\n";
            // idle 不动(450→450),total 涨 → 100%
            var second = "cpu  200 0 100 400 50 0 0 0 0 0\n";
            Assert.Equal(100.0, ServerInfoCollector.ParseStatUsagePercent(first, second));

            // total 不涨 → null
            Assert.Null(ServerInfoCollector.ParseStatUsagePercent(first, first));

            // 非 cpu 汇总行 → null
            Assert.Null(ServerInfoCollector.ParseStatUsagePercent("cpu0 1 2 3 4 5 6 7 8 9\n", second));
        }

        [Fact(DisplayName = "ParseMemInfo: MemTotal/MemAvailable kB → bytes")]
        public void ParseMemInfo_固定文本()
        {
            const string meminfo = "MemTotal:       16384000 kB\nMemFree:         8192000 kB\nMemAvailable:   12288000 kB\nBuffers:          512000 kB\n";
            var (total, available) = ServerInfoCollector.ParseMemInfo(meminfo);
            Assert.Equal(16384000L * 1024, total);
            Assert.Equal(12288000L * 1024, available);

            // 缺 MemAvailable(老内核) → null 不炸
            var (t2, a2) = ServerInfoCollector.ParseMemInfo("MemTotal:       16384000 kB\nMemFree:         100 kB\n");
            Assert.Equal(16384000L * 1024, t2);
            Assert.Null(a2);
        }

        // ───────────────────────── 全路径采集 ─────────────────────────

        [Fact(DisplayName = "GetRuntimeInfo: 进程/内存/OS 快照非空且自洽")]
        public void GetRuntimeInfo_全路径()
        {
            var info = ServerInfoCollector.GetRuntimeInfo();
            Assert.True(info.Pid > 0);
            Assert.False(string.IsNullOrWhiteSpace(info.ProcessName));
            Assert.False(string.IsNullOrWhiteSpace(info.Uptime));
            Assert.False(string.IsNullOrWhiteSpace(info.OsDescription));
            Assert.False(string.IsNullOrWhiteSpace(info.HostName));
            Assert.True(info.WorkingSetBytes > 0);
            Assert.True(info.ManagedMemoryBytes > 0);
            Assert.True(info.ProcessorCount > 0);
            Assert.False(info.StartTime == default);
        }

        [Fact(DisplayName = "GetHardwareInfo: 跨平台恒有 ProcessorCount/Disks;Linux 另有 CPU/内存字段")]
        public void GetHardwareInfo_全路径()
        {
            var info = ServerInfoCollector.GetHardwareInfo();
            Assert.True(info.ProcessorCount > 0);
            Assert.NotEmpty(info.Disks);
            Assert.All(info.Disks, d => Assert.False(string.IsNullOrWhiteSpace(d.Root)));

            if (OperatingSystem.IsLinux())
            {
                Assert.False(string.IsNullOrWhiteSpace(info.CpuModelName));
                Assert.NotNull(info.CpuUsagePercent);
                Assert.InRange(info.CpuUsagePercent!.Value, 0, 100);
                Assert.NotNull(info.MemoryTotalBytes);
                Assert.True(info.MemoryTotalBytes > 0);
                Assert.NotNull(info.MemoryUsagePercent);
                Assert.InRange(info.MemoryUsagePercent!.Value, 0, 100);
            }
            else
            {
                Assert.Null(info.CpuModelName);
                Assert.Null(info.MemoryTotalBytes);
            }
        }

        [Fact(DisplayName = "GetFrameworkAssemblies: 含 AdminCore.Core,仅 AdminCore 家族")]
        public void GetFrameworkAssemblies_家族口径()
        {
            var list = ServerInfoCollector.GetFrameworkAssemblies();
            Assert.NotEmpty(list);
            Assert.Contains(list, a => a.Name == "AdminCore.Core");
            Assert.All(list, a => Assert.Matches("^AdminCore", a.Name));
        }

        [Fact(DisplayName = "DI: SysServerService 经 AddAutoServices 解析,三方法可调用")]
        public void Di_解析服务()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoServices(typeof(SysServerService).Assembly);
            var provider = services.BuildServiceProvider();

            var svc = provider.GetRequiredService<SysServerService>();
            Assert.NotNull(svc.GetRuntimeInfo());
            Assert.NotNull(svc.GetHardwareInfo());
            Assert.NotEmpty(svc.GetFrameworkAssemblies());
        }
    }
}
