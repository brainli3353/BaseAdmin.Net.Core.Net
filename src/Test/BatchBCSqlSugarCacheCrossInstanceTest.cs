using System.Diagnostics;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.EventBus;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的;批 Q 先例)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 BC(G22③ 收口)SqlSugar 二级缓存**跨实例失效**测试:真实 Redis(127.0.0.1:6380)。
/// </summary>
/// <remarks>
/// 病灶(修前实测口径,见 g12 §7):SqlSugar 的自动失效路径＝<c>GetAllKey</c> 枚举本适配器登记簿 ＋ 逐键 Remove;
/// 登记簿是**进程内**的 ⇒ 共享 Redis 下,A 实例写库只删得掉 A 登记过的键,B 登记的键留在缓存里 ⇒ B 读旧值。
/// 判定对象＝「B 登记的键有没有被清掉」,用两个独立 ServiceProvider + 两个独立 EventBus(同进程、只经 Redis 互通,
/// 与两进程部署同构,同批 AC 口径)模拟两实例。
/// Redis 不可达时诚实降级(Assert.Fail,禁止冒充通过),口径同 BatchACEventBusRedisTest。
/// </remarks>
public class BatchBCSqlSugarCacheCrossInstanceTest
{
    private readonly ITestOutputHelper _output;

    public BatchBCSqlSugarCacheCrossInstanceTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 环境事实:事件总线 Redis 在 6380(无口令);6379 是需口令的业务 Redis,勿混用
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    /// <summary>适配器内的键前缀(私有 const,测试按字面量断言——与批 R/g12 同口径)。</summary>
    private const string KeyPrefix = "sys_sqlSugar:";

    private const string Downgrade = "降级:Redis(127.0.0.1:6380)不可达——本用例未实跑,禁止冒充通过";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromSeconds(1);

    // ─────────────────────────────────────────────────────────────
    // 1. 核心判据:他实例登记的键,在本实例失效后被清掉
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 跨实例_他实例登记的键被清掉()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var keyA = $"{KeyPrefix}bc_a_{Guid.NewGuid():N}";
        var keyB = $"{KeyPrefix}bc_b_{Guid.NewGuid():N}";

        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{RedisHost}:{RedisPort}");
        using var a = new Endpoint(channel, EventBusEvents.CacheInvalidate, mux);
        using var b = new Endpoint(channel, EventBusEvents.CacheInvalidate, mux);
        try
        {
            await a.Bus.StartAsync(default);
            await b.Bus.StartAsync(default);
            await Task.Delay(300); // 订阅建立窗口(下面还有重复发布兜底)

            // A/B 各自缓存一个键(同表同查询不同实例 ⇒ 键不同,A 从没登记过 B 的键)
            a.Adapter.GetOrCreate(keyA[KeyPrefix.Length..], () => "va", 300);
            b.Adapter.GetOrCreate(keyB[KeyPrefix.Length..], () => "vb", 300);
            Assert.True(await a.Cache.ExistKeyAsync(keyA), "前置:A 的键应已入缓存");
            Assert.True(await b.Cache.ExistKeyAsync(keyB), "前置:B 的键应已入缓存");

            // A 侧发生失效(SqlSugar 失效入口＝GetAllKey):重复一小会儿,覆盖订阅建立延迟
            var sw = Stopwatch.StartNew();
            while (await b.Cache.ExistKeyAsync(keyB) && sw.Elapsed < WaitTimeout)
            {
                a.Adapter.GetAllKey<object>().ToList(); // 枚举＋广播(生产路径由 SqlSugar 失效触发)
                await Task.Delay(200);
            }

            Assert.False(await b.Cache.ExistKeyAsync(keyB), "B 实例登记的键应被跨实例失效清掉(修前会残留⇒跨实例脏读)");
            Assert.False(await a.Cache.ExistKeyAsync(keyA), "A 自己的键也应在本地失效路径里清掉");
            Assert.Equal(0, b.Registry.Count); // B 的登记簿同步清空
            _output.WriteLine($"跨实例失效耗时 {sw.ElapsedMilliseconds}ms(A 广播 → B 清自己的键)");
        }
        finally
        {
            await mux.GetDatabase().KeyDeleteAsync(new RedisKey[] { keyA, keyB });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 反证:白名单未登记该事件 ⇒ 不跨实例(B 的键残留)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 白名单未登记_不跨实例_键残留()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        var channel = UniqueChannel();
        var keyA = $"{KeyPrefix}bc_neg_a_{Guid.NewGuid():N}";
        var keyB = $"{KeyPrefix}bc_neg_b_{Guid.NewGuid():N}";

        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{RedisHost}:{RedisPort}");
        // 双通道开启但白名单为空(＝部署漏登记):事件被接收侧按白名单丢弃
        using var a = new Endpoint(channel, crossEvents: string.Empty, mux);
        using var b = new Endpoint(channel, crossEvents: string.Empty, mux);
        try
        {
            await a.Bus.StartAsync(default);
            await b.Bus.StartAsync(default);
            await Task.Delay(300);

            a.Adapter.GetOrCreate(keyA[KeyPrefix.Length..], () => "va", 300);
            b.Adapter.GetOrCreate(keyB[KeyPrefix.Length..], () => "vb", 300);

            a.Adapter.GetAllKey<object>().ToList();
            await Task.Delay(NegativeWindow); // 给"本不该投递"的错误投递留观察时间

            Assert.True(await b.Cache.ExistKeyAsync(keyB), "白名单未登记时不应跨实例失效——这正是部署漏登记的后果(启动有守卫告警)");
            Assert.Equal(1, b.Registry.Count);
        }
        finally
        {
            await mux.GetDatabase().KeyDeleteAsync(new RedisKey[] { keyA, keyB });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 无发布器(未接事件总线):仅本实例失效,不抛
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 无发布器_仅本实例失效且不抛()
    {
        var provider = BuildMemoryCacheProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        var registry = new SqlSugarCacheKeyRegistry();
        var adapter = new SqlSugarDataCacheService(cache, registry); // publisher = null

        adapter.GetOrCreate("bc_nopub", () => "v", 300);
        Assert.Equal(1, registry.Count);
        Assert.True(await cache.ExistKeyAsync($"{KeyPrefix}bc_nopub"));

        var keys = adapter.GetAllKey<object>().ToList(); // 不应抛(无发布器=直接返回)
        Assert.Single(keys);
        adapter.Remove<object>($"{KeyPrefix}bc_nopub");
        Assert.Equal(0, registry.Count);
        Assert.False(await cache.ExistKeyAsync($"{KeyPrefix}bc_nopub"));
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 登记簿语义:Purge 清底层键 + 幂等;Track/Forget 幂等
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登记簿_Purge清底层键且幂等()
    {
        var provider = BuildMemoryCacheProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        var registry = new SqlSugarCacheKeyRegistry();

        registry.Track($"{KeyPrefix}k1");
        registry.Track($"{KeyPrefix}k1"); // 幂等
        registry.Track($"{KeyPrefix}k2");
        await cache.SetAsync($"{KeyPrefix}k1", "v1", TimeSpan.FromMinutes(5));
        await cache.SetAsync($"{KeyPrefix}k2", "v2", TimeSpan.FromMinutes(5));
        Assert.Equal(2, registry.Count);

        await registry.PurgeAsync(cache);
        Assert.Equal(0, registry.Count);
        Assert.False(await cache.ExistKeyAsync($"{KeyPrefix}k1"));
        Assert.False(await cache.ExistKeyAsync($"{KeyPrefix}k2"));

        await registry.PurgeAsync(cache); // 空登记簿再清一次:幂等不抛
        registry.Forget($"{KeyPrefix}不存在"); // 幂等
        Assert.Equal(0, registry.Count);
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 装配契约:AddSqlSugar 注册登记簿单例,且适配器复用同一实例
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 装配_登记簿为DI单例且适配器复用()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionConfigs:0:ConfigId"] = "main",
                ["ConnectionConfigs:0:ConnectionString"] = DevPgConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Cache:Type"] = "Memory" // 避开 Redis:本用例只验装配与同名实例
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddCache(config);
        services.AddSqlSugar(config);
        using var provider = services.BuildServiceProvider();

        var singleton = provider.GetRequiredService<SqlSugarCacheKeyRegistry>();
        var db = provider.GetRequiredService<ISqlSugarClient>();
        var adapter = db.CurrentConnectionConfig.ConfigureExternalServices.DataInfoCacheService as SqlSugarDataCacheService;
        Assert.NotNull(adapter);

        adapter!.GetOrCreate("bc_di_probe", () => 1, 60);
        // 适配器写的键出现在 DI 单例里 ⇒ 二者是同一登记簿(订阅者清的就是适配器登记的那一批)
        Assert.Equal(1, singleton.Count);
        Assert.True(await provider.GetRequiredService<ICacheService>().ExistKeyAsync($"{KeyPrefix}bc_di_probe"));

        adapter.Remove<object>($"{KeyPrefix}bc_di_probe");
        Assert.Equal(0, singleton.Count);
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 启动守卫:双通道开着却没登记本事件 ⇒ 响亮告警(部署漏登记的可见性)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 守卫_双通道开而白名单未登记本事件时告警()
    {
        if (!await TcpReachableAsync(RedisHost, RedisPort))
        {
            Assert.Fail(Downgrade);
        }

        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{RedisHost}:{RedisPort}");
        var logs = new List<(LogLevel Level, string Message)>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventBus:EnableRedis"] = "true",
                ["EventBus:Channel"] = UniqueChannel(),
                ["EventBus:CrossInstanceEvents"] = "online_push" // 故意漏登记本事件
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CapturingLoggerProvider(logs));
        });
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IEventHandlerExecutor, RetryEventHandlerExecutor>();
        services.AddScoped<SqlSugarCacheInvalidateSubscriber>();
        using var provider = services.BuildServiceProvider();

        // 构造即触发守卫(不启动消费者)
        _ = new EventBus(
            provider,
            provider.GetRequiredService<ILogger<EventBus>>(),
            provider.GetRequiredService<IEventHandlerExecutor>(),
            new[] { typeof(SqlSugarCacheInvalidateSubscriber).Assembly },
            config,
            mux);

        var hit = logs.Any(l => l.Level >= LogLevel.Warning && l.Message.Contains(EventBusEvents.CacheInvalidate));
        Assert.True(hit, "未登记白名单时应有告警包含事件名 " + EventBusEvents.CacheInvalidate +
                         "; 实际日志=" + string.Join(" | ", logs.Select(l => l.Message)));
    }

    // ─────────────────────────────────────────────────────────────
    // 7-9. 2026-09-30 自激循环事故回归(测试版登录不可用):
    //    空登记簿失效枚举不广播 + 订阅审计抑制——消费侧审计落库会再触发失效 ⇒ GetAllKey ⇒ 再发布,
    //    按消费者吞吐永续,打满 EventBus 有界通道,阻塞 op_log 等全部发布 ⇒ 控制器响应全挂
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void 空登记簿_失效枚举不广播()
    {
        var provider = BuildMemoryCacheProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        var registry = new SqlSugarCacheKeyRegistry();
        var publisher = new CountingPublisher();
        var adapter = new SqlSugarDataCacheService(cache, registry, publisher);

        var keys = adapter.GetAllKey<object>().ToList();

        Assert.Empty(keys);
        Assert.True(publisher.Count == 0,
            "空登记簿=本实例无二级缓存可失效,广播是纯噪音——会被消费侧审计落库再触发失效,自激循环打满 EventBus 通道(2026-09-30 事故);实际发布次数=" + publisher.Count);
    }

    [Fact]
    public void 非空登记簿_一次失效枚举广播一次()
    {
        var provider = BuildMemoryCacheProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        var registry = new SqlSugarCacheKeyRegistry();
        var publisher = new CountingPublisher();
        var adapter = new SqlSugarDataCacheService(cache, registry, publisher);

        registry.Track($"{KeyPrefix}bc_loop_k");
        adapter.GetAllKey<object>().ToList();
        adapter.GetAllKey<object>().ToList();

        Assert.True(publisher.Count == 2, "登记簿非空时每次失效枚举广播一次(一次失效一次广播的既有语义,不因 2026-09-30 修复改变);实际发布次数=" + publisher.Count);
    }

    [Fact]
    public void 跨实例失效订阅_审计抑制防回归()
    {
        var method = typeof(SqlSugarCacheInvalidateSubscriber)
            .GetMethod(nameof(SqlSugarCacheInvalidateSubscriber.HandleCacheInvalidateAsync))!;

        Assert.True(method.GetCustomAttribute<SuppressMonitorAttribute>() is not null,
            "cache_invalidate 订阅必须 [SuppressMonitor]:事件处理不落库,消费→审计→失效→发布的自激循环才断(2026-09-30 事故)");
    }

    // ─────────────────────────────────────────────────────────────
    // 装置
    // ─────────────────────────────────────────────────────────────

    private const string DevPgConnectionString =
        TestPg.ConnectionString;

    /// <summary>
    /// 一个"实例":独立登记簿 + 独立缓存客户端(同一 Redis DB＝共享缓存) + 独立事件总线(只经 Redis 互通)。
    /// </summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly ServiceProvider _provider;

        public EventBus Bus { get; }
        public ICacheService Cache { get; }
        public SqlSugarCacheKeyRegistry Registry { get; }
        public SqlSugarDataCacheService Adapter { get; }

        public Endpoint(string channel, string crossEvents, IConnectionMultiplexer mux)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EventBus:EnableRedis"] = "true",
                    ["EventBus:Channel"] = channel,
                    ["EventBus:CrossInstanceEvents"] = crossEvents
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<ICacheService>(new RedisCacheService(mux)); // 两个实例各持客户端,共用同一 Redis DB
            services.AddSingleton<SqlSugarCacheKeyRegistry>();
            services.AddSingleton<IEventHandlerExecutor, RetryEventHandlerExecutor>();
            services.AddScoped<SqlSugarCacheInvalidateSubscriber>();
            _provider = services.BuildServiceProvider();

            Bus = new EventBus(
                _provider,
                _provider.GetRequiredService<ILogger<EventBus>>(),
                _provider.GetRequiredService<IEventHandlerExecutor>(),
                new[] { typeof(SqlSugarCacheInvalidateSubscriber).Assembly },
                config,
                mux);
            Cache = _provider.GetRequiredService<ICacheService>();
            Registry = _provider.GetRequiredService<SqlSugarCacheKeyRegistry>();
            Adapter = new SqlSugarDataCacheService(
                Cache, Registry, Bus, _provider.GetRequiredService<ILogger<SqlSugarDataCacheService>>());
        }

        public void Dispose()
        {
            try
            {
                Bus.StopAsync(default).GetAwaiter().GetResult();
            }
            catch
            {
                /* 停总线失败不影响断言结果 */
            }

            _provider.Dispose();
        }
    }

    private static ServiceProvider BuildMemoryCacheProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:Type"] = "Memory" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddCache(config);
        return services.BuildServiceProvider();
    }

    private static string UniqueChannel() => $"bc_probe_{Guid.NewGuid():N}";

    private static async Task<bool> TcpReachableAsync(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(1500));
            return done == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>捕获型 logger(批 J 先例):断言守卫文案,不依赖外部日志设施。</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _logs;

        public CapturingLoggerProvider(List<(LogLevel Level, string Message)> logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);

        public void Dispose()
        {
        }
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

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _logs.Add((logLevel, $"[{_category}] {formatter(state, exception)}"));
    }

    /// <summary>计数型发布器(2026-09-30 回归用例):只数发布次数,不入队。</summary>
    private sealed class CountingPublisher : IEventPublisher
    {
        public int Count;

        public Task PublishAsync<T>(string eventName, T payload)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}