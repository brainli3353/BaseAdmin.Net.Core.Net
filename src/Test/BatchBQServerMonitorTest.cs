using AdminCore.Core.Utils.ServerMonitor;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BQ sysServer 服务器监控复审验证件:
    /// D1 <see cref="ServerInfoCollector.HasCapacitySemantics"/> 纯函数(docker 宿主伪挂载/不可读条目过滤判据,
    /// 真机 e2e 实锤:nsfs/tracefs/overlayfs 层 DriveFormat 取不到 ⇒ 绕过伪 FS 排除表,21 条里 17 条全 0 垃圾行);
    /// D2 前端 i18n `sysServer.title` 键补齐属文案面(源文件断言见用例 2,防未来再丢——全站 23 页仅此页曾缺)。
    /// </summary>
    public class BatchBQServerMonitorTest
    {
        [Fact(DisplayName = "HasCapacitySemantics: 三条件全中才滤(无容量语义伪挂载/不可读条目),任一给数即保留")]
        public void D1_无容量语义条目过滤判据()
        {
            // 实锤泄漏形态(docker 宿主 netns/tracing/overlayfs 层):不可读且全 0 ⇒ 滤
            var ghost = new DiskInfoOutput { Root = "/run/docker/netns/x", Name = "/run/docker/netns/x", IsReady = false, TotalSize = 0, AvailableFreeSpace = 0 };
            Assert.False(ServerInfoCollector.HasCapacitySemantics(ghost));

            // 真盘:可读 ⇒ 留(即使容量极小的边界卷)
            var real = new DiskInfoOutput { Root = "/", IsReady = true, TotalSize = 316464824320, AvailableFreeSpace = 206076747776 };
            Assert.True(ServerInfoCollector.HasCapacitySemantics(real));

            // 不可读但容量非 0(缓存/瞬时抖动):有数据可显示 ⇒ 留(「保守保留」边界不变)
            var jitter = new DiskInfoOutput { Root = "/data", IsReady = false, TotalSize = 1, AvailableFreeSpace = 0 };
            Assert.True(ServerInfoCollector.HasCapacitySemantics(jitter));

            // 可读但容量 0(极小卷边界):IsReady 给数 ⇒ 留
            var tiny = new DiskInfoOutput { Root = "/x", IsReady = true, TotalSize = 0, AvailableFreeSpace = 0 };
            Assert.True(ServerInfoCollector.HasCapacitySemantics(tiny));
        }

        [Fact(DisplayName = "i18n 硬门: sysServer 段含 title 键(zh/en 对称)——批 BQ D2 防复发")]
        public void D2_sysServer_i18n_title键在()
        {
            // 测试宿主 cwd 与 BaseDirectory 落点均随构建态漂移(实测 src/Test、src/Test/bin/、src/ 三种)——
            // 向上探测:第一个同时含 web/ 与 src/ 子目录的祖先目录即仓库根,稳定不随构建态变
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "web")) && Directory.Exists(Path.Combine(dir.FullName, "src"))))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            var zh = File.ReadAllText(Path.Combine(dir!.FullName, "web", "src", "i18n", "zh-CN.ts"));
            var en = File.ReadAllText(Path.Combine(dir!.FullName, "web", "src", "i18n", "en-US.ts"));
            Assert.Contains("sysServer: {", zh);
            Assert.Matches(@"sysServer:\s*\{[^}]*title:\s*'[^']+',", zh);
            Assert.Matches(@"sysServer:\s*\{[^}]*title:\s*'[^']+',", en);
        }
    }
}
