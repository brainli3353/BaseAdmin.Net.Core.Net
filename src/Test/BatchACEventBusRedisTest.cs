﻿using System.Diagnostics;
using System.Net.Sockets;
using AdminCore.Core.EventBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AC 事件总线 Redis 双通道(81 功能点 5.6)测试:真实 Redis(127.0.0.1:6380)。
/// 判定对象 = 「谁执行了几次」(本地订阅 / 跨实例投递 / 自回环丢弃 / 白名单闸门 / 无连接降级),
/// 用两个独立 ServiceProvider + 两个独立 Redis 连接模拟两个宿主实例,逐条对应机制语义。
/// </summary>
/// <remarks>
/// WHY 同进程两容器即算两个"实例":跨实例语义的判据是「不共享内存总线、只经 Redis 互通」——
/// 两个 EventBus 各自持独立 Channel/订阅表/实例标识,彼此只能经 Redis 通道收到对方的广播,
/// 与两进程部署同构;而实例标识在总线内是构造期随机串(_instanceId),同进程不会撞车。
/// WHY 每个用例用唯一 channel 名:Redis 通道是进程外共享资源,固定通道名会被并发/残留订阅串扰
/// (收到别的用例的消息 → 计数被污染);唯一通道 + 用例内独立连接,保证零串扰。
/// WHY 计数器 static 且按实例记账:进程级 static 在生产语义里等价「本实例执行」,但两个"实例"同处一进程,
/// 必须靠注入的实例标识分桶(见 AcProbeInstanceTag 的 WHY),否则 A/B 计数会混成一个数。
/// Redis 不可达时诚实降级(Assert.Fail,禁止冒充通过),口径同 BatchABTenantDbTest 的 PG 降级。
/// </remarks>
public class BatchACEventBusRedisTest
{
    private readonly ITestOutputHelper _output;

    public BatchACEventBusRedisTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 环境事实:事件总线 Redis 在 6380(无口令,已验证 PONG);6379 是需口令的业务 Redis,勿混用
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    // 探针事件名(与 AcProbeEntities 的订阅方法一一对应)
    private const string CrossEvent = "ac_probe_cross";
    private const string LocalEvent = "ac_probe_local";
    private const string WarmupEvent = "ac_probe_warmup";

    // 两个"实例"的标识(注入订阅者,使计数可按实例分桶)
    private const string InstanceA = "A";
    private const string InstanceB = "B";

    // 断言轮询上限(机制是异步投递,固定 sleep 要么脆弱要么拖慢;轮询 + 上限=既快又稳)
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // 反面断言的观察窗口(「不应投递」类断言必须给错误投递留出可观察时间,否则等于没验证)
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromSeconds(1);

    // 诚实降级文案(与 BatchABTenantDbTest 同口径)
    private const string Downgrade = "降级:Redis(127.0.0.1:6380)不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // 1. 白名单内事件:本地一次 + 跨实例一次,自回环不重复
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 双通道_白名单事件跨实例投递且本地不重复()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var config = BuildConfig(enableRedis: true, channel, $"{CrossEvent},{WarmupEvent}");
        AcProbeSubscriber.Reset();
        AcProbeWarmupSubscriber.Reset();

        using var multiplexerA = await ConnectAsync();
        using var multiplexerB = await ConnectAsync();
        using var endpointA = new BusEndpoint(InstanceA, config, multiplexerA);
        using var endpointB = new BusEndpoint(InstanceB, config, multiplexerB);

        await endpointA.Bus.StartAsync(CancellationToken.None);
        await endpointB.Bus.StartAsync(CancellationToken.None);
        try
        {
            // 就绪握手:确保 B 的 Redis 订阅已生效(否则发布早于订阅生效,消息被 Pub/Sub 丢弃 → 假红)
            await WaitForRemoteSubscriptionAsync(endpointA.Bus);
            Assert.True(AcProbeWarmupSubscriber.ArrivedAt(InstanceB),
                "跨实例通道未就绪:5s 内 B 未收到任何 Redis 投递(无法完成跨实例判定)");

            var tag = $"ac-cross-{Guid.NewGuid():N}";
            await endpointA.Bus.PublishAsync(CrossEvent, new AcProbePayload { Tag = tag });

            var delivered = await WaitUntilAsync(
                () => AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceB) >= 1);
            Assert.True(delivered, "B 应在 5s 内经 Redis 收到跨实例事件");

            // 判定点①:A 只执行一次 —— 若自回环未按实例标识丢弃,A 会把自己广播的副本再执行一次(=2)
            var countA = AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceA);
            var countB = AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceB);
            Assert.Equal(1, countA);
            // 判定点②:B 恰一次(重复订阅/重复投递会 >1)
            Assert.Equal(1, countB);
            // 判定点③:进程级总执行数=本地 1 + 跨实例 1(第三个执行必属重复触发)
            Assert.Equal(2, Volatile.Read(ref AcProbeSubscriber.CrossCount));
            // 判定点④:投递内容就是 A 发的那一条(防串扰/防空载荷)
            Assert.True(AcProbeSubscriber.CrossPayloads.Contains(tag), "B 收到的载荷 Tag 应与发布值一致");
            _output.WriteLine($"双通道判定:A={countA} B={countB} 总执行={Volatile.Read(ref AcProbeSubscriber.CrossCount)} 通道={channel}");
        }
        finally
        {
            await endpointA.Bus.StopAsync(CancellationToken.None);
            await endpointB.Bus.StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 白名单外事件:只本地,不跨实例(防审计类事件重复落库)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 白名单外事件不跨实例()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        // 白名单只登记 ac_probe_cross:warmup 也登记(便于与用例 1 同口径),local 刻意**不**登记
        var config = BuildConfig(enableRedis: true, channel, $"{CrossEvent},{WarmupEvent}");
        AcProbeSubscriber.Reset();
        AcProbeWarmupSubscriber.Reset();

        using var multiplexerA = await ConnectAsync();
        using var multiplexerB = await ConnectAsync();
        using var endpointA = new BusEndpoint(InstanceA, config, multiplexerA);
        using var endpointB = new BusEndpoint(InstanceB, config, multiplexerB);

        await endpointA.Bus.StartAsync(CancellationToken.None);
        await endpointB.Bus.StartAsync(CancellationToken.None);
        try
        {
            // 先握手:证「B 的跨实例接收链路是通的」——否则 B=0 可能只是链路没起来(反面断言失去意义)
            await WaitForRemoteSubscriptionAsync(endpointA.Bus);
            Assert.True(AcProbeWarmupSubscriber.ArrivedAt(InstanceB), "跨实例通道未就绪,B=0 无法归因于白名单闸门");

            var tag = $"ac-local-{Guid.NewGuid():N}";
            await endpointA.Bus.PublishAsync(LocalEvent, new AcProbePayload { Tag = tag });

            var localRan = await WaitUntilAsync(
                () => AcProbeSubscriber.CountOf(AcProbeSubscriber.LocalByInstance, InstanceA) >= 1);
            Assert.True(localRan, "白名单外事件在本实例内仍应正常执行(本地订阅照旧)");

            // 反面观察窗口:通道是通的(B 已收到过 warmup),给跨实例投递留 1s 仍收不到 → 白名单闸门生效
            await Task.Delay(NegativeWindow);

            Assert.Equal(1, AcProbeSubscriber.CountOf(AcProbeSubscriber.LocalByInstance, InstanceA));
            Assert.Equal(0, AcProbeSubscriber.CountOf(AcProbeSubscriber.LocalByInstance, InstanceB));
            Assert.Equal(1, Volatile.Read(ref AcProbeSubscriber.LocalCount));
            _output.WriteLine($"白名单判定:local 事件 A=1 B=0(warmup 已证通道可用),通道={channel}");
        }
        finally
        {
            await endpointA.Bus.StopAsync(CancellationToken.None);
            await endpointB.Bus.StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 开关关闭:即使给了 Redis 连接也纯本地
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关关闭_纯本地()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var config = BuildConfig(enableRedis: false, channel, CrossEvent);
        AcProbeSubscriber.Reset();
        AcProbeWarmupSubscriber.Reset();

        using var multiplexerA = await ConnectAsync();
        using var multiplexerB = await ConnectAsync();
        using var endpointA = new BusEndpoint(InstanceA, config, multiplexerA);
        using var endpointB = new BusEndpoint(InstanceB, config, multiplexerB);

        await endpointA.Bus.StartAsync(CancellationToken.None);
        await endpointB.Bus.StartAsync(CancellationToken.None);
        try
        {
            var tag = $"ac-off-{Guid.NewGuid():N}";
            // 白名单内事件 + 连接齐备,唯开关关 → 必须不广播(开关是总闸)
            await endpointA.Bus.PublishAsync(CrossEvent, new AcProbePayload { Tag = tag });

            var localRan = await WaitUntilAsync(
                () => AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceA) >= 1);
            Assert.True(localRan, "开关关闭不该影响本地订阅(A 应执行一次)");

            await Task.Delay(NegativeWindow);
            Assert.Equal(0, AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceB));
            _output.WriteLine($"开关关闭判定:A=1 B=0 通道={channel}(双通道未订阅也未发布)");
        }
        finally
        {
            await endpointA.Bus.StopAsync(CancellationToken.None);
            await endpointB.Bus.StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 开关开但无 Redis 连接:降级纯本地,构造与启停都不抛
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 无Redis连接_降级纯本地不抛()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var config = BuildConfig(enableRedis: true, channel, CrossEvent);
        AcProbeSubscriber.Reset();
        AcProbeWarmupSubscriber.Reset();

        // A = 缺连接实例(EnableRedis=true 但 multiplexer=null,如宿主未注册 IConnectionMultiplexer);
        // B = 连接齐备的健康远端实例 —— 这样 B=0 才是有意义的反面证据(证明 A 确实没有广播出去)
        using var multiplexerB = await ConnectAsync();
        using var endpointA = new BusEndpoint(InstanceA, config, multiplexer: null);
        using var endpointB = new BusEndpoint(InstanceB, config, multiplexerB);

        // 判定点①:构造不抛(缺连接只降级告警)
        await endpointA.Bus.StartAsync(CancellationToken.None);
        await endpointB.Bus.StartAsync(CancellationToken.None);
        try
        {
            var tag = $"ac-degrade-{Guid.NewGuid():N}";
            await endpointA.Bus.PublishAsync(CrossEvent, new AcProbePayload { Tag = tag });

            var localRan = await WaitUntilAsync(
                () => AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceA) >= 1);
            Assert.True(localRan, "降级实例的本地订阅仍应执行(A=1)");

            await Task.Delay(NegativeWindow);
            // 判定点②:无广播 → 健康远端实例收不到(B=0)
            Assert.Equal(0, AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceB));
            _output.WriteLine($"降级判定:A 缺连接仍本地执行 B=0 通道={channel}(未抛异常)");
        }
        finally
        {
            await endpointA.Bus.StopAsync(CancellationToken.None);
            await endpointB.Bus.StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 启停幂等:Stop 二次调用不抛
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 启停幂等_Stop不抛()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var config = BuildConfig(enableRedis: true, channel, CrossEvent);
        AcProbeSubscriber.Reset();
        AcProbeWarmupSubscriber.Reset();

        using var multiplexer = await ConnectAsync();
        using var endpointA = new BusEndpoint(InstanceA, config, multiplexer);

        await endpointA.Bus.StartAsync(CancellationToken.None);
        try
        {
            await endpointA.Bus.PublishAsync(CrossEvent, new AcProbePayload { Tag = $"ac-stop-{Guid.NewGuid():N}" });
            var localRan = await WaitUntilAsync(
                () => AcProbeSubscriber.CountOf(AcProbeSubscriber.CrossByInstance, InstanceA) >= 1);
            Assert.True(localRan, "启动后应能正常消费(本地执行一次)");
        }
        finally
        {
            await endpointA.Bus.StopAsync(CancellationToken.None);
        }

        // 判定点:停机后再次 Stop 必须幂等(不抛、不重复注销导致异常)
        await endpointA.Bus.StopAsync(CancellationToken.None);
        _output.WriteLine("启停幂等判定:Start→Publish→Stop→再 Stop 全程无异常");
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:配置 / 端点(单"实例") / 轮询 / 探测
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 双通道配置(与生产键名一致):EnableRedis 总闸 / Channel 通道名 / CrossInstanceEvents 白名单。
    /// 配置对象本就只放进内存集合,绝不落到 appsettings(生产开关由部署侧决定)。
    /// </summary>
    private static IConfiguration BuildConfig(bool enableRedis, string channel, string crossInstanceEvents)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventBus:EnableRedis"] = enableRedis ? "true" : "false",
                ["EventBus:Channel"] = channel,
                ["EventBus:CrossInstanceEvents"] = crossInstanceEvents
            })
            .Build();

    /// <summary>用例唯一通道名(防与其它用例/残留订阅串扰)。</summary>
    private static string UniqueChannel() => $"ac_probe_{Guid.NewGuid():N}";

    /// <summary>连事件总线 Redis(连接建立失败由用例降级处理,故此处不吞异常)。</summary>
    private static async Task<ConnectionMultiplexer> ConnectAsync()
        => await ConnectionMultiplexer.ConnectAsync($"{RedisHost}:{RedisPort}");

    /// <summary>
    /// 就绪握手:用发布端反复发 warmup,直到 B 记账(见 AcProbeWarmupSubscriber 的 WHY)。
    /// 内层 200ms 小窗口避免空转刷屏发布;总上限 5s,超时由调用方断言失败(不静默通过)。
    /// </summary>
    private static async Task WaitForRemoteSubscriptionAsync(EventBus publisher)
    {
        var total = Stopwatch.StartNew();
        while (!AcProbeWarmupSubscriber.ArrivedAt(InstanceB) && total.Elapsed < WaitTimeout)
        {
            await publisher.PublishAsync(WarmupEvent, new AcProbePayload { Tag = "ac-warmup" });
            var window = Stopwatch.StartNew();
            while (!AcProbeWarmupSubscriber.ArrivedAt(InstanceB) && window.Elapsed < TimeSpan.FromMilliseconds(200))
            {
                await Task.Delay(20);
            }
        }
    }

    /// <summary>轮询等待条件成立(上限 <see cref="WaitTimeout"/>);避免固定长 sleep 的脆弱等待。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < WaitTimeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>短超时 TCP 探测(2 秒):Redis 不可达时用例诚实降级,防沙箱黑洞挂死。</summary>
    private static async Task<bool> TcpReachableAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(2000));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 单个"实例":独立 ServiceProvider(独立 DI 作用域→独立订阅者实例) + 独立 Redis 连接 + 独立 EventBus。
    /// WHY 显式 new EventBus 而非 [Service] 自动注册:DI 构造入口只扫 Core 程序集,测试探针在 Test 程序集,
    /// 须走显式 Assembly[] 构造(与 EventBusTest/BatchSExecutorTest 同口径);config/multiplexer 为可选参数。
    /// </summary>
    private sealed class BusEndpoint : IDisposable
    {
        private readonly ServiceProvider _provider;

        /// <summary>本实例总线(BackgroundService:StartAsync/StopAsync 由用例显式控制启停)。</summary>
        public EventBus Bus { get; }

        public BusEndpoint(string instanceTag, IConfiguration config, IConnectionMultiplexer? multiplexer)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(config);
            // 实例标识:使订阅者的计数可按实例分桶(见 AcProbeInstanceTag 的 WHY)
            services.AddSingleton(new AcProbeInstanceTag { Value = instanceTag });
            services.AddScoped<AcProbeSubscriber>();
            services.AddScoped<AcProbeWarmupSubscriber>();
            // 事件执行器(batch S 5.8):裸容器无 SqlSugar → 审计写库降级禁用(仅重试),本批测的是总线投递机制
            services.AddSingleton<IEventHandlerExecutor, RetryEventHandlerExecutor>();
            _provider = services.BuildServiceProvider();

            Bus = new EventBus(
                _provider,
                _provider.GetRequiredService<ILogger<EventBus>>(),
                _provider.GetRequiredService<IEventHandlerExecutor>(),
                new[] { typeof(AcProbeSubscriber).Assembly },
                config,
                multiplexer);
        }

        public void Dispose() => _provider.Dispose();
    }
}