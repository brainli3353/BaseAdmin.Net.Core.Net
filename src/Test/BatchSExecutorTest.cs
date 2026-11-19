﻿using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.EventBus;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.User;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 S(5.8 事件执行器)测试(参照 Admin.NET RetryEventHandlerExecutor 移植,见 g13):
/// SysLogEvent 执行审计(Insert Running→终态 Update)+精细重试(按订阅特性 NumRetries/RetryTimeout/
/// ExceptionTypes,缺省回落全局)+SuppressMonitor 审计 opt-out。
/// 真实 PG 探针事件载荷/审计行 finally 清理;PG 不可达时用例降级 Assert.Fail。
/// </summary>
public class BatchSExecutorTest : IAsyncLifetime
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private ServiceProvider? _provider;
    private IEventPublisher? _publisher;
    private ISqlSugarClient? _db;
    private bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        if (!IsPostgresReachable())
        {
            return;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "t13s_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "t13s-test",
                ["Jwt:Audience"] = "t13s-test-api",
                ["Cryptogram:Key"] = "dDEzbl90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM="
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSqlSugar(config);
        services.AddCache(config);
        services.AddProjectOptions(config);
        services.AddAutoServices(typeof(SysUserService).Assembly);
        services.AddAutoServices(typeof(BatchSExecutorTest).Assembly); // 探针订阅者 [Service] 自注册

        // 显式构造总线并扫描 Test 程序集(探针订阅者所在)——DI 入口构造只扫 Core(生产订阅者域),
        // 测试探针须走显式 Assembly[] 构造(设计注释口径);后注册覆盖 [Service] 自动注册条目
        var built = services.BuildServiceProvider();
        var bus = new EventBus(built, built.GetRequiredService<ILogger<EventBus>>(),
            built.GetRequiredService<IEventHandlerExecutor>(), new[] { typeof(BatchSExecutorTest).Assembly });
        services.AddSingleton<IEventPublisher>(bus);
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(_db);
        _publisher = _provider.GetRequiredService<IEventPublisher>();

        // 启动消费者(总线 BackgroundService;测试宿主手动启动)
        await ((IHostedService)bus).StartAsync(default);
        _pgReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (_pgReachable && _db is not null)
        {
            // 清理本批探针审计行(按 EventName 前缀)
            _db.Deleteable<SysLogEvent>().Where(e => e.EventName.StartsWith($"t13s_")).ExecuteCommand();
        }

        if (_pgReachable)
        {
            var bus = (IHostedService)_provider!.GetRequiredService<IEventPublisher>();
            await bus.StopAsync(default);
        }

        _provider?.Dispose();
    }

    [Fact]
    public async Task S1_成功事件_审计落Success行()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        await _publisher!.PublishAsync("t13s_ok", new ProbePayload { Mark = _p });
        var row = await WaitForAuditAsync("t13s_ok");
        Assert.True(row is not null, "成功事件应有审计行");
        Assert.True(row!.Status == EventStatusEnum.Success, $"成功事件审计应为 Success,实际={row.Status}");
        Assert.True(row.Elapsed is not null && row.Elapsed.Value >= 0, $"审计应记录耗时,实际={row.Elapsed}");
        Assert.True(row.HandlerMethod.Contains("S1ProbeSubscriber"), $"审计应记录触发方法,实际={row.HandlerMethod}");
    }

    [Fact]
    public async Task S2_失败重试后成功_审计Retry转Success含重试记录()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 探针订阅者:前 2 次抛异常,第 3 次成功(全局重试 ≤2 次 → 恰好成功)
        RetryProbeSubscriber.Reset(2);
        var evt = "t13s_retry";
        await _publisher!.PublishAsync(evt, new ProbePayload { Mark = _p });
        var row = await WaitForAuditAsync(evt);
        Assert.True(row is not null, "重试成功事件应有审计行");
        Assert.True(row!.Status == EventStatusEnum.Success, $"重试后成功终态应为 Success,实际={row.Status}");
        Assert.True(string.IsNullOrEmpty(row.RetryRecord) is false && row.RetryRecord!.Contains("t13s_probe_fail"),
            $"审计应含重试记录,实际={row.RetryRecord}");
    }

    [Fact]
    public async Task S3_恒失败_审计Error含全部尝试()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 全局重试 ≤2 → 共 3 次尝试全失败 → Error
        var evt = "t13s_fail";
        await _publisher!.PublishAsync(evt, new ProbePayload { Mark = _p });
        var row = await WaitForAuditAsync(evt);
        Assert.True(row is not null, "失败事件应有审计行");
        Assert.True(row!.Status == EventStatusEnum.Error, $"重试耗尽终态应为 Error,实际={row.Status}");
        Assert.True(row.RetryRecord!.Split('\n').Length >= 3, $"审计重试记录应含全部 3 次尝试,实际={row.RetryRecord}");
        Assert.True(string.IsNullOrEmpty(row.Exception) is false, "审计应记录最终异常");
    }

    [Fact]
    public async Task S4_精细重试_特性NumRetries覆盖全局()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 探针订阅者特性 NumRetries=1(覆盖全局 2)→ 共 2 次尝试全失败 → Error
        var evt = "t13s_custom";
        await _publisher!.PublishAsync(evt, new ProbePayload { Mark = _p });
        var row = await WaitForAuditAsync(evt);
        Assert.True(row is not null, "精细重试事件应有审计行");
        Assert.True(row!.Status == EventStatusEnum.Error, $"应终态 Error,实际={row.Status}");
        Assert.True(row.RetryRecord!.Split('\n').Length == 2, $"NumRetries=1 应共 2 次尝试,实际={row.RetryRecord}");
    }

    [Fact]
    public async Task S5_SuppressMonitor_无审计行且订阅照常执行()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var evt = "t13s_suppress";
        var executed = false;
        SuppressProbeSubscriber.Executed = () => executed = true;
        await _publisher!.PublishAsync(evt, new ProbePayload { Mark = _p });
        await WaitUntilAsync(() => executed, "SuppressMonitor 订阅应照常执行");
        var row = await WaitForAuditAsync(evt, timeoutMs: 2000);
        Assert.True(row is null, "SuppressMonitor 订阅不应有审计行");
    }

    // 等审计行达终态(消费者异步:审计行先 Insert(Running)后 Update 终态,中间态 Running/Retry 须继续等;
    // 按 EventName + 载荷 Mark 精确定位本用例行,防跨用例串读)
    private async Task<SysLogEvent?> WaitForAuditAsync(string eventName, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var row = await _db!.Queryable<SysLogEvent>()
                .Where(e => e.EventName == eventName && e.Payload!.Contains(_p))
                .OrderByDescending(e => e.Id)
                .FirstAsync();
            if (row is not null
                && (row.Status == EventStatusEnum.Success || row.Status == EventStatusEnum.Error))
            {
                return row;
            }

            await Task.Delay(200);
        }

        // 超时:返回最新行(可能为中间态或 null)供断言定位
        return await _db!.Queryable<SysLogEvent>()
            .Where(e => e.EventName == eventName && e.Payload!.Contains(_p))
            .OrderByDescending(e => e.Id)
            .FirstAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> cond, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !cond())
        {
            await Task.Delay(100);
        }

        Assert.True(cond(), what);
    }

    private static bool IsPostgresReachable()
    {
        try
        {
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(DbHost, DbPort);
            tcp.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// 探针载荷
public sealed class ProbePayload
{
    public string Mark { get; set; } = string.Empty;
}

// S1 探针:恒成功
[AdminCore.Core.Attributes.Service(ServiceType = typeof(S1ProbeSubscriber))]
public class S1ProbeSubscriber : IEventSubscriber
{
    [EventSubscribe("t13s_ok")]
    public Task HandleAsync(ProbePayload p) => Task.CompletedTask;
}

// S2/S3 探针:按事件名前缀切换行为(全局重试缺省)
[AdminCore.Core.Attributes.Service(ServiceType = typeof(RetryProbeSubscriber))]
public class RetryProbeSubscriber : IEventSubscriber
{
    private static int _failTimes;
    private static int _count;

    public static void Reset(int failTimes)
    {
        _failTimes = failTimes;
        _count = 0;
    }

    [EventSubscribe("t13s_retry")]
    public Task HandleRetryAsync(ProbePayload p) => ExecuteOnce("t13s_probe_fail");

    [EventSubscribe("t13s_fail")]
    public Task HandleFailAsync(ProbePayload p) => ExecuteAlwaysFail();

    private static async Task ExecuteOnce(string failMessage)
    {
        _count++;
        if (_count <= _failTimes)
        {
            throw new InvalidOperationException(failMessage);
        }
    }

    private static Task ExecuteAlwaysFail()
    {
        throw new InvalidOperationException("t13s_always_fail");
    }
}

// S4 探针:特性精细重试 NumRetries=1
[AdminCore.Core.Attributes.Service(ServiceType = typeof(CustomRetryProbeSubscriber))]
public class CustomRetryProbeSubscriber : IEventSubscriber
{
    [EventSubscribe("t13s_custom", NumRetries = 1)]
    public Task HandleAsync(ProbePayload p) => throw new InvalidOperationException("t13s_custom_fail");
}

// S5 探针:SuppressMonitor 审计抑制
[AdminCore.Core.Attributes.Service(ServiceType = typeof(SuppressProbeSubscriber))]
public class SuppressProbeSubscriber : IEventSubscriber
{
    public static Func<bool> Executed { get; set; } = () => false;

    [EventSubscribe("t13s_suppress")]
    [SuppressMonitor]
    public Task HandleAsync(ProbePayload p)
    {
        // 调用测试注入的闭包置位(不可覆盖闭包本身——覆盖后测试侧局部变量永远收不到信号)
        Executed();
        return Task.CompletedTask;
    }
}
