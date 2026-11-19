using AdminCore.Core;
using AdminCore.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Globalization;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 J(5.5 SqlSugar AOP 慢 SQL 检测)测试。
    /// 覆盖:超阈值 pg_sleep 触发 Warning(原生 SQL+堆栈首帧)/阈值内不触发/
    /// SlowSqlSeconds&lt;=0 关闭/正常快 SQL 不记录。
    /// 独立 AddSqlSugar 容器(每用例各自阈值)+FakeLoggerProvider 捕获日志(不依赖宿主)。
    /// </summary>
    public class BatchJSlowSqlTest
    {
        private const string DbConnectionString =
            TestPg.ConnectionString;

        [Fact]
        public async Task 超过阈值_记慢SQL警告含原生SQL与调用点()
        {
            if (!IsPostgresReachable()) { Assert.Fail("PG 不可达,跳过"); }

            using var scope = NewScope(0.2, out var logs);
            var db = scope.GetRequiredService<ISqlSugarClient>();

            // DB 侧实睡 0.5s > 阈值 0.2s
            await db.Ado.GetStringAsync("SELECT pg_sleep(0.5)");

            var slow = logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("慢 SQL")).ToList();
            Assert.Single(slow);
            Assert.Contains("pg_sleep", slow[0].Message);  // 原生 SQL(参数内联)
            Assert.Contains("调用点", slow[0].Message);    // 堆栈首帧字段在案
            // 注:异步链下 SqlSugar 5.1.4.217 同步堆栈退化为 ExecutionContext 帧(实测,参照同特性),
            // 故不断言文件名;同步场景首帧定位正常(g4 §5 ⑤ 实证补充)
            Assert.Contains("0.2", slow[0].Message);       // 阈值回显
        }

        [Fact]
        public async Task 阈值内_不记慢SQL()
        {
            if (!IsPostgresReachable()) { Assert.Fail("PG 不可达,跳过"); }

            using var scope = NewScope(10, out var logs);
            var db = scope.GetRequiredService<ISqlSugarClient>();

            await db.Ado.GetStringAsync("SELECT pg_sleep(0.2)");

            Assert.DoesNotContain(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("慢 SQL"));
        }

        [Fact]
        public async Task 阈值小于等于零_关闭检测()
        {
            if (!IsPostgresReachable()) { Assert.Fail("PG 不可达,跳过"); }

            using var scope = NewScope(0, out var logs);
            var db = scope.GetRequiredService<ISqlSugarClient>();

            await db.Ado.GetStringAsync("SELECT pg_sleep(0.3)");

            Assert.DoesNotContain(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("慢 SQL"));
        }

        [Fact]
        public async Task 正常快SQL_不记慢SQL()
        {
            if (!IsPostgresReachable()) { Assert.Fail("PG 不可达,跳过"); }

            using var scope = NewScope(0.1, out var logs);
            var db = scope.GetRequiredService<ISqlSugarClient>();

            await db.Ado.GetStringAsync("SELECT 1");

            Assert.DoesNotContain(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("慢 SQL"));
        }

        // ── 独立容器:每用例各自阈值 + FakeLoggerProvider 捕获 ──────────

        private static ServiceProvider NewScope(double slowSqlSeconds, out List<(LogLevel Level, string Message)> logs)
        {
            logs = new List<(LogLevel, string)>();
            var fake = new CapturingLoggerProvider(logs);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    ["DbSettings:SlowSqlSeconds"] = slowSqlSeconds.ToString(CultureInfo.InvariantCulture)
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(fake));
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            return services.BuildServiceProvider();
        }

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var result = client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2));
                return result;
            }
            catch
            {
                return false;
            }
        }

        // ── 日志捕获测试替身 ────────────────────────────────────────────

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            private readonly List<(LogLevel Level, string Message)> _logs;

            public CapturingLoggerProvider(List<(LogLevel Level, string Message)> logs) => _logs = logs;

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);

            public void Dispose() { }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<(LogLevel Level, string Message)> _logs;

            public CapturingLogger(string category, List<(LogLevel Level, string Message)> logs)
            {
                _category = category;
                _logs = logs;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (_logs)
                {
                    _logs.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
