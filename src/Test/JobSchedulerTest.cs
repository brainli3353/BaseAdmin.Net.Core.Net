using System.Diagnostics;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Job;
using AdminCore.Core.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

// ============================================================================
// 探针任务(定义在本测试程序集,供 JobScheduler 显式 Assembly[] 构造反射扫描)。
// WHY 落测试程序集而非 Core:验证「新增任务贴 [JobName] 即被自动登记」这一契约——
// 不写任何手写注册代码,仅靠声明式特性 + DI 可解析即生效(同 EventBusTest 订阅者口径)。
// 计数器全部 static volatile:跨线程(调度后台执行)可见,用例间重置。
// ============================================================================

/// <summary>
/// 探针任务:记录执行时刻/次数/完成次数;DelayMs &gt; 0 时执行体等待指定时长(验证防重入:长任务跨多个到点)。
/// </summary>
[JobName("test_probe")]
public sealed class TestProbeJob : IJob
{
    public static int DelayMs;

    public static int Executions;

    public static int Completed;

    public static DateTime? LastStartedAt;

    /// <summary>非空时执行体抛异常(2026-09-30:验证执行日志失败分支 Success=false + ErrorMsg)。</summary>
    public static volatile string? FailWith;

    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        LastStartedAt = DateTime.Now;
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, ct);
        }

        var failWith = FailWith;
        if (failWith is not null)
        {
            throw new InvalidOperationException(failWith);
        }

        Interlocked.Increment(ref Completed);
    }
}

/// <summary>
/// 定时任务测试(task_108):调度触发/LastRunTime 落库/防重入/禁用不执行/RunNow/重启恢复/无代码行跳过/日志清理保留期。
/// 依赖 PostgreSQL(开发库,与 EventBusTest/RepositoryTest 同源),不可达时诚实降级(同口径 Assert.Fail 注明)。
/// 调度粒度经构造注入 tickIntervalMs=200ms 加速用例(生产缺省 5000ms 不变)。
/// </summary>
public class JobSchedulerTest
{
    private readonly ITestOutputHelper _output;

    public JobSchedulerTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 EventBusTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>测试调度粒度:200ms(生产缺省 5000ms;构造注入,不改生产行为)。</summary>
    private const int TickMs = 200;

    private const string ProbeJobName = "test_probe";

    /// <summary>PG 可达性探测(TCP 直连,不依赖 ORM)。</summary>
    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var ok = client.ConnectAsync(DbHost, DbPort).Wait(TimeSpan.FromSeconds(3));
            return ok && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ---- 内存日志记录器:捕获告警(无代码实现/预载失败等),供断言(同 EventBusTest 口径) ----
    private sealed class MemoryLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public IReadOnlyList<string> Errors
        {
            get { lock (_errors) return _errors.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new MemoryLogger(this);

        public void Dispose() { }

        internal void AddWarning(string message)
        {
            lock (_warnings) _warnings.Add(message);
        }

        internal void AddError(string message)
        {
            lock (_errors) _errors.Add(message);
        }

        private sealed class MemoryLogger : ILogger
        {
            private readonly MemoryLoggerProvider _owner;
            public MemoryLogger(MemoryLoggerProvider owner) => _owner = owner;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    _owner.AddWarning(formatter(state, exception));
                }

                if (logLevel == LogLevel.Error)
                {
                    var msg = formatter(state, exception);
                    if (exception is not null)
                    {
                        msg += " | EX: " + exception; // 消息式重载的 formatter 不含异常详情,手动补
                    }

                    _owner.AddError(msg);
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    /// <summary>
    /// 构建测试 DI 容器:日志(可捕获告警)+ SqlSugar(开发库)+ 内存缓存(Redis 连接串缺省 → Memory 降级,
    /// IncrementAsync 原子语义与 Redis 口径一致)+ 探针任务/日志任务按具体类注册(调度器按具体类型解析——生产同口径)。
    /// </summary>
    private static ServiceProvider BuildProvider(out MemoryLoggerProvider loggerProvider, string? logRetentionDays = null)
    {
        var vars = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = DbConnectionString,
            ["Yit:WorkerId"] = "1"
        };
        if (logRetentionDays is not null)
        {
            vars["SysJob:LogRetentionDays"] = logRetentionDays;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        var services = new ServiceCollection();
        // WHY 显式 <IConfiguration>:.NET 10 起 ConfigurationBuilder.Build() 返回 IConfigurationRoot,裸 AddSingleton(config)
        // 会以 IConfigurationRoot 为键注册,CleanSysLogJob 注入 IConfiguration 解析不到(实机诊断实证)
        services.AddSingleton<IConfiguration>(config); // CleanSysLogJob 注入 IConfiguration(宿主自动注册,裸容器需补)
        var provider = new MemoryLoggerProvider();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddSqlSugar(config);
        services.AddCache(config);        services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

        services.AddScoped<TestProbeJob>();
        services.AddScoped<CleanSysLogJob>();
        loggerProvider = provider;
        return services.BuildServiceProvider();
    }

    /// <summary>初始化表结构(开发库;sys_job_detail 随 Core 程序集 CodeFirst 自动建表)。</summary>
    private static void InitDb(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<ISqlSugarClient>();
        db.HealColumnCase();
        db.InitTables();
    }

    /// <summary>清理测试行(物理删除,防跨用例串扰)。</summary>
    private static async Task CleanupProbeRowsAsync(ISqlSugarRepository<SysJobDetail> repo, string jobName)
    {
        await repo.DeletePhysicalByAsync(u => u.JobName == jobName);
    }

    /// <summary>泛型日志适配器:JobScheduler 构造要 ILogger&lt;JobScheduler&gt;,内存记录器只产非泛型 ILogger。</summary>
    private sealed class CategoryLogger<T> : ILogger<T> where T : class
    {
        private readonly ILogger _inner;
        public CategoryLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static JobScheduler NewScheduler(IServiceProvider provider, MemoryLoggerProvider loggerProvider)
        => new(provider, provider.GetRequiredService<AdminCore.Core.Cache.ICacheService>(),
            new CategoryLogger<JobScheduler>(loggerProvider.CreateLogger("AdminCore.Core.Job.JobScheduler")),
            new[] { typeof(JobSchedulerTest).Assembly }, TickMs);

    /// <summary>轮询等待条件成立(最多 timeoutMs)。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static void ResetProbe()
    {
        TestProbeJob.DelayMs = 0;
        TestProbeJob.Executions = 0;
        TestProbeJob.Completed = 0;
        TestProbeJob.LastStartedAt = null;
        TestProbeJob.FailWith = null;
    }

    /// <summary>
    /// 坑回归(测试诊断实证):向共享 SqlSugar 客户端传可取消令牌且令牌后被取消 →
    // 命令缓存滞留已取消令牌,后续一切 SQL 直接 OCE(生产停止链路同险)。
    /// 调度器因此禁止向 SQL 传 stoppingToken(见 JobScheduler.LoadSchedulesAsync WHY)——
    /// 本用例验证:完整调度生命周期(预载/触发/停止)结束后,共享客户端仍可用。
    /// 2026-09-30:Interval 3600 单发 + 静默等待——执行后的日志落行须先于停止完成(G7 共享客户端并发不安全)。
    /// </summary>
    [Fact]
    public async Task 调度器停止后共享客户端仍可用()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600, // 2026-09-30:单发(首检即到期一次)——执行后日志落行须先于停止完成
                Enabled = true,
                Remark = "test: 坑回归用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                var started = await WaitUntilAsync(() => TestProbeJob.Executions >= 1, 8000);
                Assert.True(started, "调度器应在 8s 内触发探针任务(生命周期前置条件)");
                await WaitUntilAsync(() => TestProbeJob.Completed >= 1, 8000);
                await Task.Delay(500); // 2026-09-30:等执行后日志落行完成,保证停止时无在途 DB 命令
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }

            // 停止后共享客户端仍可用(若 stoppingToken 滞留命令缓存,此处直接 OCE)
            var n = await repo.DeletePhysicalByAsync(u => u.JobName == ProbeJobName);
            Assert.True(n >= 1, $"停止后应能物理清理探针行,实际删除 {n} 行");
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 到期执行 + LastRunTime 落库:从未执行(LastRunTime 空)的任务首次检查即到点,
    /// 触发后 LastRunTime 写回数据库(开始时刻=下次到点基准——重启恢复依赖该列)。
    /// 2026-09-30:Interval 3600 单发 + 静默等待,DB 读取移到停止后——执行后日志落行须先于读完成(G7 共享客户端并发不安全)。
    /// </summary>
    [Fact]
    public async Task 到期任务执行且LastRunTime落库()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600, // 2026-09-30:单发(首检即到期一次)——执行后日志落行须先于读取完成
                Enabled = true,
                Remark = "test: 到期执行用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                var fired = await WaitUntilAsync(() => TestProbeJob.Executions >= 1, 8000);
                Assert.True(fired, "从未执行的任务应在 8s 内被触发(首次检查即到点)");
                await WaitUntilAsync(() => TestProbeJob.Completed >= 1, 8000);
                await Task.Delay(500); // 2026-09-30:等执行后日志落行完成,保证停止时无在途 DB 命令
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }

            // 2026-09-30:DB 读取移到停止后——停止瞬间的在途日志 INSERT 会让读 OCE(坑回归用例同口径)
            var row = await repo.GetFirstAsync(u => u.JobName == ProbeJobName);
            Assert.NotNull(row?.LastRunTime);
            Assert.InRange(row!.LastRunTime!.Value, DateTime.Now.AddMinutes(-1), DateTime.Now.AddSeconds(5));
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 未到期不执行:LastRunTime 刚写 + Interval 远大于观察窗口 → 观察期内零触发。
    /// </summary>
    [Fact]
    public async Task 未到期任务不执行()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600,
                Enabled = true,
                LastRunTime = DateTime.Now,
                Remark = "test: 未到期用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                await Task.Delay(1500); // 覆盖约 7 个调度 tick,远超 200ms 粒度
                Assert.True(TestProbeJob.Executions == 0,
                    $"间隔 3600s 且 LastRunTime=现在,1.5s 内不应触发,实际 {TestProbeJob.Executions} 次");
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 防重入:执行时长(1500ms)&gt; 间隔(1s)→ 执行期间的到点被跳过(锁未释放),执行完成后调度继续。
    /// 断言双口径:执行在途时计数保持 1(跳过生效)+ 其后计数继续增长(调度未死)。
    /// </summary>
    [Fact]
    public async Task 防重入_执行期间到点跳过且执行后继续调度()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        TestProbeJob.DelayMs = 1500;
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 1,
                Enabled = true,
                Remark = "test: 防重入用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                var firstStarted = await WaitUntilAsync(() => TestProbeJob.Executions >= 1, 8000);
                Assert.True(firstStarted, "首轮应在 8s 内启动");

                // 首轮执行 1500ms;在 t≈1.1s(已过 1s 到点、首轮仍在途)断言计数仍为 1 → 到点被防重入跳过
                await Task.Delay(1100);
                Assert.True(TestProbeJob.Executions == 1,
                    $"执行期间(间隔 1s 已到期)应被防重入跳过,计数应保持 1,实际 {TestProbeJob.Executions}");

                // 执行完成后调度继续(下一到点正常触发)
                var continued = await WaitUntilAsync(() => TestProbeJob.Executions >= 2, 10000);
                Assert.True(continued, "首轮执行完成后,后续到点应继续触发(调度器未停摆)");
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 禁用不执行:Enabled=false + LastRunTime 空(到期条件满足)→ 观察期内零触发(禁用优先于到期)。
    /// </summary>
    [Fact]
    public async Task 禁用任务不执行()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 1,
                Enabled = false,
                Remark = "test: 禁用用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                await Task.Delay(1500);
                Assert.True(TestProbeJob.Executions == 0, "禁用任务不应触发");
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// RunNow 立即执行 + 防重入拒绝:未到期任务 RunNow 立即触发;上一轮在途时再 RunNow 返回 false(拒绝);
    /// 未注册代码的任务名 RunNow 恒 false。
    /// </summary>
    [Fact]
    public async Task RunNow立即执行_上一轮未完成时拒绝_未注册拒绝()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600,
                Enabled = true,
                LastRunTime = DateTime.Now, // 未到期 → 唯一触发源是 RunNow
                Remark = "test: RunNow 用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                // 未注册代码 → 拒绝
                Assert.False(await scheduler.RunNowAsync("ghost_not_registered"), "未注册任务 RunNow 应拒绝");

                // 第一次 RunNow:立即触发(未到期也能执行)
                TestProbeJob.DelayMs = 0;
                var first = await scheduler.RunNowAsync(ProbeJobName);
                Assert.True(first, "已注册任务 RunNow 应触发");
                Assert.True(await WaitUntilAsync(() => TestProbeJob.Completed >= 1, 5000),
                    "RunNow 触发的执行应在 5s 内完成;调度器错误日志: " + string.Join(" | ", logger.Errors));
                await Task.Delay(200); // 2026-09-30:防重入锁在日志落行之后才释放(G7),等锁释放再发下一轮 RunNow

                // 第二轮执行在途(800ms)时再 RunNow → 防重入拒绝
                TestProbeJob.DelayMs = 800;
                var second = await scheduler.RunNowAsync(ProbeJobName);
                Assert.True(second, "上一轮已完成后的 RunNow 应触发");
                var third = await scheduler.RunNowAsync(ProbeJobName);
                Assert.False(third, "上一轮在途时 RunNow 应被防重入拒绝");
                Assert.True(await WaitUntilAsync(() => TestProbeJob.Completed >= 2, 5000), "第二轮执行应在 5s 内完成");
                await Task.Delay(500); // 2026-09-30:等执行后日志落行完成,停止后的清理才不会撞在途日志 INSERT(OCE)
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 重启恢复:构造前库内已有任务行(模拟上次运行留下的 LastRunTime 状态)→
    /// 调度器启动预载后,GetScheduledJobNames 包含该任务(生效集非空),注册表含代码任务。
    /// </summary>
    [Fact]
    public async Task 重启恢复_预载库内任务定义()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);

        try
        {
            // 构造前预置(模拟「上次运行后」的库状态,含历史 LastRunTime)
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600,
                Enabled = true,
                LastRunTime = DateTime.Now.AddMinutes(-5),
                Remark = "test: 重启恢复用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                var loaded = await WaitUntilAsync(
                    () => scheduler.GetScheduledJobNames().Contains(ProbeJobName), 8000);
                Assert.True(loaded, "启动预载后,库内已启用任务应进入生效集(重启恢复)");
                Assert.True(scheduler.GetRegisteredJobNames().Contains(ProbeJobName), "代码注册表应含探针任务");
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 数据库行无代码实现:JobName 无对应 [JobName] 类 → 该行被跳过(不进生效集,不触发),
    /// 且记 ILogger 告警(可观测,不静默)。
    /// </summary>
    [Fact]
    public async Task 无代码实现的库行被跳过并告警()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var loggerProvider);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);
        await CleanupProbeRowsAsync(repo, "ghost_job");

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = "ghost_job",
                Interval = 1,
                Enabled = true,
                Remark = "test: 无代码行用例"
            });

            var scheduler = NewScheduler(provider, loggerProvider);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                await Task.Delay(1500);
                Assert.False(scheduler.GetScheduledJobNames().Contains("ghost_job"),
                    "无代码实现的库行不应进入生效集");
                Assert.True(loggerProvider.Warnings.Any(w => w.Contains("ghost_job")),
                    $"无代码实现的库行应记告警,实际: {string.Join(" | ", loggerProvider.Warnings)}");
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanupProbeRowsAsync(repo, ProbeJobName);
            await CleanupProbeRowsAsync(repo, "ghost_job");
        }
    }

    /// <summary>
    /// 执行日志落库(2026-09-30):每轮触发完成落 1 行 sys_job_log——
    /// 成功行 Success=true/ErrorMsg 空;执行体抛异常 → Success=false/ErrorMsg=异常消息;
    /// Trigger=manual(RunNow 与调度执行同一条触发路径,日志口径同)。
    /// G7 口径:不与调度器并发 DB 操作——内存等待 + 静默,settle 后停止,停止后再读断言(坑回归用例同口径)。
    /// </summary>
    [Fact]
    public async Task 执行日志_成功行与异常行()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        ResetProbe();
        using var provider = BuildProvider(out var logger);
        InitDb(provider);
        using var repoScope = provider.CreateScope();
        var repo = repoScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        using var logScope = provider.CreateScope();
        var logRepo = logScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobLog>>();
        await CleanupProbeRowsAsync(repo, ProbeJobName);
        await logRepo.DeletePhysicalByAsync(u => u.JobName == ProbeJobName); // 启动前预清(调度器未启动 → 无 G7 并发)

        try
        {
            await repo.InsertAsync(new SysJobDetail
            {
                JobName = ProbeJobName,
                Interval = 3600,
                Enabled = true,
                LastRunTime = DateTime.Now, // 未到期 → 唯一触发源是 RunNow
                Remark = "test: 执行日志用例"
            });

            var scheduler = NewScheduler(provider, logger);
            await scheduler.StartAsync(CancellationToken.None);
            try
            {
                // ① 成功执行 → 日志行 Success=true(内存等待:G7 共享客户端禁与调度器并发 DB 操作)
                var first = await scheduler.RunNowAsync(ProbeJobName);
                Assert.True(first, "已注册任务 RunNow 应触发");
                Assert.True(await WaitUntilAsync(() => TestProbeJob.Completed >= 1, 8000),
                    "成功执行应在 8s 内完成;调度器错误日志: " + string.Join(" | ", logger.Errors));
                await Task.Delay(200); // 防重入锁在日志落行之后才释放(G7),等锁释放再发下一轮 RunNow

                // ② 异常执行 → 日志行 Success=false + ErrorMsg
                TestProbeJob.FailWith = "boom_probe_error";
                var second = await scheduler.RunNowAsync(ProbeJobName);
                Assert.True(second, "第一轮完成后的 RunNow 应触发");
                // 探针语义:Completed 只在成功路径自增,失败(抛异常)停在 Executions ⇒ 异常轮以 Executions 为内存信号
                Assert.True(await WaitUntilAsync(() => TestProbeJob.Executions >= 2, 8000),
                    "异常执行应在 8s 内启动;调度器错误日志: " + string.Join(" | ", logger.Errors));
                await Task.Delay(500); // 等两轮日志落行完成(停止瞬间的在途日志 INSERT 会让停止后的读 OCE)
            }
            finally
            {
                TestProbeJob.FailWith = null;
                await scheduler.StopAsync(CancellationToken.None);
            }

            // 停止后共享客户端安全(坑回归用例同口径)→ 再读断言日志行
            var rows = await logRepo.AsQueryable().Where(u => u.JobName == ProbeJobName).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("manual", r.Trigger));
            var okRow = rows.FirstOrDefault(r => r.Success);
            var failRow = rows.FirstOrDefault(r => !r.Success);
            Assert.NotNull(okRow);
            Assert.Null(okRow!.ErrorMsg);
            Assert.NotNull(failRow);
            Assert.Contains("boom_probe_error", failRow!.ErrorMsg);
        }
        finally
        {
            await logRepo.DeletePhysicalByAsync(u => u.JobName == ProbeJobName);
            await CleanupProbeRowsAsync(repo, ProbeJobName);
        }
    }

    /// <summary>
    /// 日志清理保留期(clean_sys_log 核心逻辑):保留期 1 天 →
    /// CreateTime 早于 2 天前的 sys_log_op/sys_job_log 行被物理删除(行消失,非软删),保留期内行保留。
    /// </summary>
    [Fact]
    public async Task 日志清理_保留期外行物理删除_期内行保留()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过定时任务落库用例(同 RepositoryTest 口径)");
        }

        using var provider = BuildProvider(out _, logRetentionDays: "1");
        InitDb(provider);

        var opMarker = $"test_cleanup_108_{Guid.NewGuid():N}";
        using (var scope = provider.CreateScope())
        {
            var opRepo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysLogOp>>();
            // 过期行:CreateTime 显式置 2 天前(AOP 不覆盖非空 CreateTime);期内行:缺省由 AOP 填当前时刻
            var expired = new SysLogOp { ActionName = opMarker + "_expired", ControllerName = "Test", HttpMethod = "GET", RemoteIp = "127.0.0.1", Account = "test", CreateTime = DateTime.Now.AddDays(-2) };
            var recent = new SysLogOp { ActionName = opMarker + "_recent", ControllerName = "Test", HttpMethod = "GET", RemoteIp = "127.0.0.1", Account = "test" };
            await opRepo.InsertAsync(expired);
            await opRepo.InsertAsync(recent);

            // 第 7 表 sys_job_log 同口径:过期行物理删除、期内行保留(2026-09-30)
            var jobMarker = $"test_joblog_108_{Guid.NewGuid():N}";
            var jobLogRepo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobLog>>();
            var jobExpired = new SysJobLog
            {
                JobName = jobMarker + "_expired",
                Trigger = "test",
                TriggeredAt = DateTime.Now.AddDays(-2),
                Success = true,
                InstanceId = "test_instance",
                CreateTime = DateTime.Now.AddDays(-2)
            };
            var jobRecent = new SysJobLog
            {
                JobName = jobMarker + "_recent",
                Trigger = "test",
                TriggeredAt = DateTime.Now,
                Success = true,
                InstanceId = "test_instance"
            };
            await jobLogRepo.InsertAsync(jobExpired);
            await jobLogRepo.InsertAsync(jobRecent);

            var job = scope.ServiceProvider.GetRequiredService<CleanSysLogJob>();
            await job.ExecuteAsync(new JobContext("clean_sys_log", "test", DateTime.Now), CancellationToken.None);

            var expiredAfter = await opRepo.GetFirstAsync(u => u.ActionName == opMarker + "_expired");
            var recentAfter = await opRepo.GetFirstAsync(u => u.ActionName == opMarker + "_recent");
            var jobExpiredAfter = await jobLogRepo.GetFirstAsync(u => u.JobName == jobMarker + "_expired");
            var jobRecentAfter = await jobLogRepo.GetFirstAsync(u => u.JobName == jobMarker + "_recent");

            Assert.Null(expiredAfter);
            Assert.NotNull(recentAfter);
            Assert.Null(jobExpiredAfter);
            Assert.NotNull(jobRecentAfter);
        }
    }
}
