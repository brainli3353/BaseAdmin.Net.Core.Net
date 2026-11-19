using AdminCore.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 上游回灌①（2026-10-28，来源 fork 侧实证）：文件日志**写失败必须降级为丢弃态**，
/// 不得把异常抛回 <see cref="ILogger"/> 调用方（否则启动期日志目录不可写会直接打死宿主进程，
/// 且无 HTTP 错误面可看）。零库：不连 PG/Redis，用"不可写路径"制造真实 IO 异常。
/// </summary>
public class BatchBackflowFileLoggerTest
{
    [Fact]
    public void 日志路径不可写_记录不抛且进入丢弃态()
    {
        // /dev/null 是字符设备，其下无法创建目录/文件 ⇒ 首次写入必抛（本仓测试跑 Linux 容器）
        var options = new FileLoggerOptions
        {
            Enabled = true,
            Path = "/dev/null/t845_backflow_log",
            MinimumLevel = LogLevel.Trace
        };

        using var provider = new FileLoggerProvider(options);
        var logger = provider.CreateLogger("Backflow.Probe");

        // 修复前：这一行会抛 IOException/UnauthorizedAccessException（异常冒到日志调用方）
        logger.LogInformation("first-write-should-not-throw");

        // 丢弃态：后续写入不再尝试、也不再抛
        logger.LogInformation("after-disabled-should-not-throw");
    }

    [Fact]
    public void 反向哨兵_路径可写时正常落文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t845_backflow_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var options = new FileLoggerOptions { Enabled = true, Path = dir, MinimumLevel = LogLevel.Trace };
            using (var provider = new FileLoggerProvider(options))
            {
                provider.CreateLogger("Backflow.Probe").LogInformation("hello-backflow");
            }

            var files = Directory.GetFiles(dir, "app-*.log");
            Assert.Single(files);
            Assert.Contains("hello-backflow", File.ReadAllText(files[0]));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
