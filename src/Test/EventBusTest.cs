using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using SqlSugar;
using Xunit.Abstractions;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log;

namespace AdminCore.Test;

// ============================================================================
// 测试订阅者与载荷(定义在本测试程序集,供 EventBus 显式 Assembly[] 构造反射扫描)。
// WHY 落测试程序集而非 Core:验证「新增订阅者贴 [EventSubscribe] 即被自动登记」
// 这一契约——不写任何手写注册代码,仅靠声明式特性 + DI 可解析即生效。
// 这些类型不贴 [Service](生产 [Service] 只扫 Core),故不影响生产注册;测试内显式按具体类注册。
// ============================================================================

// 测试载荷:携带一个整型值,供订阅者断言。
public sealed class EventBusTestPayload
{
    public int Value { get; }
    public EventBusTestPayload(int value) => Value = value;
}

// 扫描测试订阅者:记录收到的载荷值(验证订阅方法被正确调用且载荷透传)。
public sealed class EventBusScanSubscriber : IEventSubscriber
{
    public static volatile int Received = int.MinValue;

    [EventSubscribe("test.scan")]
    public void OnScan(EventBusTestPayload payload) => Received = payload.Value;
}

// 重试测试订阅者:每次调用计数并抛异常(验证总线重试≤2 共 3 次尝试后记告警、不阻断队列)。
public sealed class EventBusThrowingSubscriber : IEventSubscriber
{
    public static int Attempts = 0;

    [EventSubscribe("test.throw")]
    public void OnThrow(object payload)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("测试用异常(验证重试)");
    }
}

/// <summary>
/// 事件总线测试(task_014 F4):订阅扫描 / 失败重试 / 有界背压 / op_log 异步落库。
/// 前三项为纯单元(不依赖 PG);op_log 落库依赖 PostgreSQL,不可达时诚实降级(同 RepositoryTest 口径)。
/// </summary>
public class EventBusTest
{
    private readonly ITestOutputHelper _output;

    public EventBusTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 Web.Entry/appsettings.json 同源,仅开发用;Timeout=10 防沙箱 PG 黑洞挂死)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // ---- 内存日志记录器:捕获总线告警(重试耗尽/解析失败等),供断言 ----
    private sealed class MemoryLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new MemoryLogger(this);

        public void Dispose() { }

        internal void AddWarning(string message)
        {
            lock (_warnings) _warnings.Add(message);
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
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// 构建最小 DI 容器:日志(可捕获告警)+ 测试订阅者按「具体类」注册。
    /// WHY 按具体类而非 [Service] 自动匹配:总线按订阅者具体类型解析(见 EventBus.DispatchAsync),
    /// 省略 ServiceType 会注册成 IEventSubscriber→具体类,具体类反解析不到。此处显式对齐生产口径。
    /// </summary>
    private static ServiceProvider BuildProvider(out MemoryLoggerProvider loggerProvider)
    {
        var services = new ServiceCollection();
        // 先落局部变量再捕获:out 参数不可在 lambda 内直接引用(CS1628)
        var provider = new MemoryLoggerProvider();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddSingleton<EventBusScanSubscriber>();
        services.AddSingleton<EventBusThrowingSubscriber>();
        // 事件执行器(批 S 5.8 构造签名变更):裸容器无 SqlSugar → DI 可选参数注入 null → 审计降级禁用,纯重试
        // (本类测总线机制非审计;审计口径由 BatchSExecutorTest 真实库覆盖)
        services.AddSingleton<IEventHandlerExecutor, RetryEventHandlerExecutor>();
        loggerProvider = provider;
        return services.BuildServiceProvider();
    }

    /// <summary>轮询等待条件成立(最多 timeoutMs);供异步消费断言,避免固定 sleep 的脆弱性。</summary>
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

    /// <summary>
    /// 订阅扫描:新增订阅者(贴 [EventSubscribe])被反射扫描自动登记并执行,载荷正确透传。
    /// 本用例不写任何注册代码(仅 DI 具体类注册),验证「声明式订阅、启动自动登记」契约。
    /// </summary>
    [Fact]
    public async Task 订阅扫描_新订阅者自动登记并执行()
    {
        EventBusScanSubscriber.Received = int.MinValue;
        using var provider = BuildProvider(out _);
        using var bus = new EventBus(provider, provider.GetRequiredService<ILogger<EventBus>>(), provider.GetRequiredService<IEventHandlerExecutor>(), new[] { typeof(EventBusTest).Assembly });

        await bus.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync("test.scan", new EventBusTestPayload(42));
            var ok = await WaitUntilAsync(() => EventBusScanSubscriber.Received == 42, 3000);
            Assert.True(ok, "订阅者应在 3s 内收到载荷 42(反射扫描自动登记并执行)");
            Assert.Equal(42, EventBusScanSubscriber.Received);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 失败重试:抛异常订阅者首次执行 + 重试 2 次(共 3 次尝试,每次间隔 1s),仍失败记 ILogger 告警且不上抛(不阻断队列)。
    /// </summary>
    [Fact]
    public async Task 失败重试_抛异常订阅者重试2次后告警且不阻断()
    {
        EventBusThrowingSubscriber.Attempts = 0;
        using var provider = BuildProvider(out var loggerProvider);
        using var bus = new EventBus(provider, provider.GetRequiredService<ILogger<EventBus>>(), provider.GetRequiredService<IEventHandlerExecutor>(), new[] { typeof(EventBusTest).Assembly });

        await bus.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync("test.throw", new object());
            // 3 次尝试 = 首次 + 2 次重试,间隔 1s → 约 2s 完成;给 8s 余量
            var ok = await WaitUntilAsync(() => Volatile.Read(ref EventBusThrowingSubscriber.Attempts) >= 3, 8000);
            Assert.True(ok, "重试应在 8s 内完成 3 次尝试");
            // xunit v2 的 Assert.Equal 无用户消息重载(第三参是 comparer/precision),断言+说明改用 Assert.True
            Assert.True(EventBusThrowingSubscriber.Attempts == 3, $"应恰好 3 次尝试(首次 + 重试≤2),实际 {EventBusThrowingSubscriber.Attempts}");

            // 重试耗尽须记告警(模板含「重试 {Retries} 次后仍失败」)
            var warnings = loggerProvider.Warnings;
            _output.WriteLine("捕获告警: " + string.Join(" | ", warnings));
            Assert.True(warnings.Any(w => w.Contains("重试") && w.Contains("仍失败")), "重试耗尽应记 ILogger 告警(含「重试…仍失败」)");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 有界队列:连续发布超过容量(1000)时,第 1001 条被背压「等待」而非丢弃/立即完成。
    /// 口径:满则等待(审计数据不可静默丢)。不启动消费者 → 通道只进不出,确定性填满后验证阻塞。
    /// </summary>
    [Fact]
    public async Task 有界队列_超容量第1001条被背压阻塞而非丢弃()
    {
        using var provider = BuildProvider(out _);
        // 不 StartAsync(消费者不运行)→ 通道只进不出,可确定性填满容量 1000
        using var bus = new EventBus(provider, provider.GetRequiredService<ILogger<EventBus>>(), provider.GetRequiredService<IEventHandlerExecutor>(), new[] { typeof(EventBusTest).Assembly });

        // 填满容量 1000(每条 WriteAsync 因有空位而立即完成)
        for (var i = 0; i < 1000; i++)
        {
            await bus.PublishAsync("test.flood", new EventBusTestPayload(i));
        }

        // 第 1001 条:无空位 → 背压等待(不完成);若为「丢弃」或「无界」则会立即完成
        var overflow = bus.PublishAsync("test.flood", new EventBusTestPayload(-1));
        await Task.Delay(300);
        Assert.False(overflow.IsCompleted, "第 1001 条应被背压阻塞(等待空间)而非丢弃/立即完成");

        // 清理:启动消费者排空队列,使阻塞中的入队得以完成,避免遗留挂起(try/finally 确保 StopAsync)
        await bus.StartAsync(CancellationToken.None);
        try
        {
            var drained = await WaitUntilAsync(() => overflow.IsCompleted, 5000);
            Assert.True(drained, "启动消费者后,背压阻塞的第 1001 条应随空位释放而完成(证明是等待而非丢弃)");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// op_log 异步落库:经总线发布 "op_log"(SysLogOp)→ OpLogSubscriber 后台消费 → 落 sys_log_op 表。
    /// 验证「请求链路只发布、落库由后台异步完成」的解耦口径;延迟应 <1s(契约)。
    /// 依赖 PostgreSQL:不可达时诚实降级(同 RepositoryTest,Assert.Fail 显式声明,不静默冒充通过)。
    /// </summary>
    [Fact]
    public async Task op_log_发布后由订阅者异步落库()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar(config);
        // 事件执行器(批 S 5.8):本容器有 SqlSugar → DI 注入仓储 → 完整审计(顺带验证 op_log 审计落库不伤主档)
        services.AddSingleton<IEventHandlerExecutor, RetryEventHandlerExecutor>();
        // OpLogSubscriber 按具体类注册(与生产 [Service(ServiceType=typeof(OpLogSubscriber))] 同口径),供总线作用域解析
        services.AddScoped<OpLogSubscriber>();
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ISqlSugarClient>();
        db.HealColumnCase();
        db.InitTables(); // 缺省扫 Core → 建 sys_log_op 表

        // 预热连接池:避免首条查询的连接初始化延迟计入落库延迟度量
        db.Ado.GetInt("select 1");

        using var bus = new EventBus(provider, provider.GetRequiredService<ILogger<EventBus>>(), provider.GetRequiredService<IEventHandlerExecutor>(), new[] { typeof(OpLogSubscriber).Assembly });
        await bus.StartAsync(CancellationToken.None);

        var actionName = $"task_014_oplog_{Guid.NewGuid():N}"; // 唯一标记,防串读残留行
        var log = new SysLogOp
        {
            ControllerName = "EventBusTest",
            ActionName = actionName,
            HttpMethod = "POST",
            RemoteIp = "127.0.0.1",
            ElapsedMs = 1,
            Account = "tester",
            Success = true
        };

        try
        {
            var sw = Stopwatch.StartNew();
            await bus.PublishAsync("op_log", log);
            // 轮询落库结果(后台消费异步完成);最多等 5s
            // 独立 SqlSugarClient 查询:测试线程与消费者(订阅者落库)若共享同一 scope 连接会触发
            // NpgsqlOperationInProgressException(同连接并发命令,即 G7 flake);独立连接池彻底隔离
            var checkDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = DbConnectionString,
                DbType = DbType.PostgreSQL
            });
            var found = false;
            while (sw.ElapsedMilliseconds < 5000)
            {
                // 原生 SQL 显式 PascalCase 列名(独立 client 无 case 修复;按唯一 actionName 定位本用例)
                found = await checkDb.Ado.GetIntAsync(
                    "select count(*) from sys_log_op where \"ActionName\" = @name", new { name = actionName }) > 0;
                if (found)
                {
                    break;
                }
                await Task.Delay(50);
            }
            sw.Stop();
            var latency = sw.Elapsed;
            _output.WriteLine($"op_log 发布→落库延迟: {latency.TotalMilliseconds:F0} ms");

            Assert.True(found, "op_log 应在 5s 内由订阅者异步落库到 sys_log_op");
            Assert.True(latency < TimeSpan.FromSeconds(1), $"落库延迟 {latency.TotalMilliseconds:F0}ms 应 <1s(契约)");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
            // 清理本用例插入行(原生 SQL 绕软删过滤器,防残留影响重入)
            db.Ado.ExecuteCommand("delete from sys_log_op where \"ActionName\" = @name", new { name = actionName });
            provider.Dispose();
        }
    }

    /// <summary>
    /// DI 一致性(回归锁死):按 Program.cs 同款方式 AddAutoServices(Core 程序集)+AddSqlSugar 组装完整 DI 容器,
    /// 断言 IEventPublisher 与 IHostedService 解析到「同一」EventBus 单例实例——发布者=消费者,共享同一 Channel。
    /// WHY 必须锁死:此前 AddAutoServices 的 HostedService 分支曾直接注册 IHostedService→实现(第二条直接单例描述符),
    /// 而 .NET DI 按 (ServiceType, ImplementationType) 缓存单例,两条描述符会分裂成两个 EventBus 实例:
    /// 发布者(IEventPublisher)入队实例 A 的 Channel,宿主消费者(IHostedService)却读实例 B 的空 Channel,
    /// 事件永不消费且无任何告警(live 实证:两实例 hash 不同、op_log 零落库)。修复=宿主侧改「工厂经主服务类型解析」。
    /// 本用例若被未来改回直接 IHostedService→实现,会立即失败,锁住该回归。
    /// </summary>
    [Fact]
    public void DI一致性_IEventPublisher与IHostedService解析到同一EventBus实例()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar(config);
        // 与 Program.cs 同口径:扫 Core 程序集自动发现 [Service](含 EventBus/JobScheduler 的 IHostedService 补注册);
        // AddCache 为 Core 内 [Service] 组件(JobScheduler 防重入锁)的生产前置依赖,测试容器须对齐,否则构造期快速失败。
        services.AddCache(config);        services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

        services.AddAutoServices(typeof(EventBus).Assembly);
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetService<IEventPublisher>();
        var hostedBus = provider.GetServices<IHostedService>().OfType<EventBus>().FirstOrDefault();
        // xunit v2 的 Assert.NotNull 无用户消息重载,断言+说明改用 Assert.True
        Assert.True(publisher is not null, "IEventPublisher 应可解析(EventBus 经 [Service] 显式锁定 ServiceType)");
        Assert.True(hostedBus is not null, "IHostedService 应可解析出 EventBus(宿主需自动发现并启动后台消费者)");
        Assert.True(ReferenceEquals(publisher, hostedBus),
            "IEventPublisher 与 IHostedService 必须解析到同一 EventBus 单例(发布者=消费者,共享同一 Channel);若分裂成两实例,op_log 事件永不消费");
    }

    /// <summary>短超时 TCP 探测 PG 端口(2s),防沙箱 PG 黑洞挂死(同 RepositoryTest)。</summary>
    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(DbHost, DbPort);
            return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
