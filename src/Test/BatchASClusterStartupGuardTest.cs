using System.Net.Sockets;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using StackExchange.Redis;

namespace AdminCore.Test;

/// <summary>
/// 批 AS「集群安全基线」验证件:被测主体 =
/// ① <see cref="StartupSchemaInitializer"/>——启动建表/种子的跨实例锁(三态判据 F2 + 锁面口径 F3);
/// ② <see cref="WorkerIdGuard"/>——雪花 WorkerId 占用登记与冲突检出(F4 三态)。
/// </summary>
/// <remarks>
/// WHY 真实依赖:本批改的就是"多实例同刻启动"的**协调语义**,替身只能验证"我喂什么返回什么"——
/// 判据①②必须由真实 Redis 档缓存承载(Memory 档的锁与计数键不跨进程,同差距清单 G18),
/// 判据"执行态"必须真落到 PG(建表 + 种子幂等,与每次应用启动同一条路径,同 BatchADDatabaseTest.DbScope 口径)。
/// 档位边界另设纯 Memory 用例(不依赖外部资源,保证本机零依赖时仍有一段真跑的证据)。
/// 零污染:只清本文件自造的键(锁键 sys_startup_schema 为生产常量,每用例 finally 逐键删;
/// WorkerId 键用 60/61 两个不与真实实例(缺省 1)相撞的机器码)——绝不 KEYS */FLUSHDB。
/// 依赖不可达一律 Assert.Fail 显式降级(禁止冒充通过)。
/// </remarks>
public class BatchASClusterStartupGuardTest
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 环境事实(同 BatchAFCacheQueueTest/BatchAK):6380 是无口令的 eventbus 实例;6379 是需口令的业务 Redis,勿混
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    private const string PgDowngrade = "降级:PostgreSQL 127.0.0.1:5432 不可达——本用例未实跑,禁止冒充通过";
    private const string RedisDowngrade = "降级:Redis 127.0.0.1:6380 不可达——本用例未实跑,禁止冒充通过";

    /// <summary>实体程序集(与 Program.cs 同口径传 Core + Business;Test 工程引用了 Business 占位类)。</summary>
    private static Assembly[] EntityAssemblies { get; } =
        [typeof(SqlSugarSetup).Assembly, typeof(AdminCore.Business.Class1).Assembly];

    // ── F2 态①:取到锁 ⇒ 执行建表 + 种子,完成后锁已释放(真实 PG + 真实 Redis 档) ──────────────
    [Fact]
    public async Task F2态1_取到锁_执行建表与种子且完成后释放锁()
    {
        if (!IsPostgresReachable() || !await TcpReachableAsync())
        {
            Assert.Fail(!IsPostgresReachable() ? PgDowngrade : RedisDowngrade);
        }

        await using var scope = new DbScope();
        using var redis = await ConnectRedisAsync();
        var cache = new RedisCacheService(redis);
        var (factory, sink) = Capture();
        var token = $"test-a:{Environment.ProcessId}:{Guid.NewGuid():N}";

        try
        {
            var outcome = await StartupSchemaInitializer.InitAsync(scope.Db, cache, factory.CreateLogger("startup"), token, EntityAssemblies);

            Assert.Equal(StartupSchemaInitOutcome.Executed, outcome);
            // 两行完成日志齐(它们只在锁内的 RunSchemaAndSeeds 里打 ⇒ 存在即证明 schema 段真跑了)
            Assert.Contains(sink, m => m.Contains("初始化表完成", StringComparison.Ordinal));
            Assert.Contains(sink, m => m.Contains("种子执行完成", StringComparison.Ordinal));
            Assert.DoesNotContain(sink, m => m.Contains("跳过", StringComparison.Ordinal));
            // 判据③(F3):执行完必须释放,不留残锁(否则第二台重启会永久跳过)
            Assert.False(await cache.ExistKeyAsync(CacheConst.KeyStartupSchema));
        }
        finally
        {
            await cache.RemoveAsync(CacheConst.KeyStartupSchema);
            factory.Dispose();
        }
    }

    // ── F2 态②:他实例持锁 ⇒ 整段跳过,不碰 schema(真实 PG + 真实 Redis 档) ────────────────────
    [Fact]
    public async Task F2态2_他实例持锁_整段跳过且不落schema日志()
    {
        if (!IsPostgresReachable() || !await TcpReachableAsync())
        {
            Assert.Fail(!IsPostgresReachable() ? PgDowngrade : RedisDowngrade);
        }

        await using var scope = new DbScope();
        using var redis = await ConnectRedisAsync();
        var cache = new RedisCacheService(redis);
        var (factory, sink) = Capture();
        var holderToken = "test-holder";
        var lateToken = $"test-late:{Guid.NewGuid():N}";

        try
        {
            // 模拟"另一实例已在建表":先用同一把键占锁(真实 Redis 键跨连接可见,这就是跨实例语义)
            Assert.True(await cache.LockTakeAsync(CacheConst.KeyStartupSchema, holderToken, StartupSchemaInitializer.SchemaLockTtl));

            var outcome = await StartupSchemaInitializer.InitAsync(scope.Db, cache, factory.CreateLogger("startup"), lateToken, EntityAssemblies);

            Assert.Equal(StartupSchemaInitOutcome.SkippedHeldByOther, outcome);
            Assert.Contains(sink, m => m.Contains("启动建表/种子跳过", StringComparison.Ordinal));
            // 关键反证:锁内日志一条都不许出现——出现即说明"跳过"是假的,并发 DDL 风险仍在
            Assert.DoesNotContain(sink, m => m.Contains("初始化表完成", StringComparison.Ordinal));
            Assert.DoesNotContain(sink, m => m.Contains("种子执行完成", StringComparison.Ordinal));

            // 跳过方不得解他人锁(token 不同 ⇒ LockRelease 判假),持锁方仍可自行释放
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyStartupSchema));
            Assert.False(await cache.LockReleaseAsync(CacheConst.KeyStartupSchema, lateToken));
            Assert.True(await cache.LockReleaseAsync(CacheConst.KeyStartupSchema, holderToken));
        }
        finally
        {
            await cache.RemoveAsync(CacheConst.KeyStartupSchema);
            factory.Dispose();
        }
    }

    // ── F2 态③:缓存本身异常 ⇒ 保守照常执行 + 响亮告警(真实 PG,锁面用抛异常探针) ──────────────
    [Fact]
    public async Task F2态3_缓存不可用_保守照常执行并告警()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail(PgDowngrade);
        }

        await using var scope = new DbScope();
        var (factory, sink) = Capture();
        var cache = new ProbeCache(new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()))) { ThrowOnLock = true };

        try
        {
            var outcome = await StartupSchemaInitializer.InitAsync(scope.Db, cache, factory.CreateLogger("startup"), "test-cache-down", EntityAssemblies);

            Assert.Equal(StartupSchemaInitOutcome.ExecutedWithoutLock, outcome);
            Assert.Contains(sink, m => m.Contains("锁获取失败", StringComparison.Ordinal));
            Assert.Contains(sink, m => m.Contains("初始化表完成", StringComparison.Ordinal));
            // 未取到锁 ⇒ 无权释放:全程不碰 LockRelease(否则会把他人锁解掉)
            Assert.Equal(0, cache.ReleaseCalls);
        }
        finally
        {
            factory.Dispose();
        }
    }

    // ── F3:锁键名/ TTL / token 逐条对账(装饰真实 Redis 档,记录调用参数) ──────────────────────
    [Fact]
    public async Task F3_锁键名与TTL与token逐条对账()
    {
        if (!IsPostgresReachable() || !await TcpReachableAsync())
        {
            Assert.Fail(!IsPostgresReachable() ? PgDowngrade : RedisDowngrade);
        }

        await using var scope = new DbScope();
        using var redis = await ConnectRedisAsync();
        var (factory, _) = Capture();
        var token = "test-token-f3";
        var probe = new ProbeCache(new RedisCacheService(redis));

        try
        {
            var outcome = await StartupSchemaInitializer.InitAsync(scope.Db, probe, factory.CreateLogger("startup"), token, EntityAssemblies);

            Assert.Equal(StartupSchemaInitOutcome.Executed, outcome);
            // ① 键名走 CacheConst 单点(不散字面量)② TTL 用本件常量(非 0/非无限)③ token 原样透传
            Assert.Equal(CacheConst.KeyStartupSchema, probe.TakeKey);
            Assert.Equal(StartupSchemaInitializer.SchemaLockTtl, probe.TakeExpiry);
            Assert.Equal(token, probe.TakeToken);
            // ④ 释放与取锁同 token(仅持有者可解),且只解一次
            Assert.Equal(1, probe.ReleaseCalls);
            Assert.Equal(token, probe.ReleaseToken);
            Assert.True(StartupSchemaInitializer.SchemaLockTtl > TimeSpan.Zero);
        }
        finally
        {
            await probe.RemoveAsync(CacheConst.KeyStartupSchema);
            factory.Dispose();
        }
    }

    // ── F4:WorkerId 占用登记三态(真实 Redis 档;机器码取 60/61,不与真实实例缺省 1 相撞) ────────
    [Fact]
    public async Task F4_WorkerId占用_首占成功_次占冲突_缓存异常则跳过检测()
    {
        if (!await TcpReachableAsync())
        {
            Assert.Fail(RedisDowngrade);
        }

        const int workerId = 60;
        var key = CacheConst.KeyWorkerId + workerId;
        using var redis = await ConnectRedisAsync();
        var cache = new RedisCacheService(redis);
        var (factory, sink) = Capture();
        var logger = factory.CreateLogger("worker");

        try
        {
            await cache.RemoveAsync(key); // 前置清残留:上一轮崩溃留下的键会把"首占"压成冲突,判据失真

            // ① 首个实例登记 ⇒ Registered + 启动日志留身份(供冲突方指认)
            Assert.Equal(WorkerIdGuardOutcome.Registered,
                await WorkerIdGuard.TryRegisterAsync(cache, workerId, "node-a:100:aaa", logger));
            Assert.Contains(sink, m => m.Contains("占用登记成功", StringComparison.Ordinal));

            // ② 第二个实例同 WorkerId ⇒ Conflict + 告警含可检索归因点
            Assert.Equal(WorkerIdGuardOutcome.Conflict,
                await WorkerIdGuard.TryRegisterAsync(cache, workerId, "node-b:200:bbb", logger));
            Assert.Contains(sink, m => m.Contains("主键", StringComparison.Ordinal) && m.Contains("冲突", StringComparison.Ordinal));

            // 不同 WorkerId 不相互干扰(各占各槽)
            Assert.Equal(WorkerIdGuardOutcome.Registered,
                await WorkerIdGuard.TryRegisterAsync(cache, 61, "node-c:300:ccc", logger));

            // ③ 缓存不可用 ⇒ Unavailable,不外抛、不阻断(诊断件不得变成启动硬门)
            var throwing = new ProbeCache(cache) { ThrowOnIncrement = true };
            Assert.Equal(WorkerIdGuardOutcome.Unavailable,
                await WorkerIdGuard.TryRegisterAsync(throwing, 62, "node-d:400:ddd", logger));
        }
        finally
        {
            await cache.RemoveAsync(key);
            await cache.RemoveAsync(CacheConst.KeyWorkerId + 61);
            factory.Dispose();
        }
    }

    // ── 档位边界:Memory 档下锁与计数只在单进程内成立(无外部依赖,本机零资源也能实跑) ────────────
    [Fact]
    public async Task 边界_Memory档单进程锁互斥成立_但跨实例不可判()
    {
        var (factory, sink) = Capture();
        // 同一 MemoryCacheService 实例 = 同一进程内:两把 token 竞争一把锁 ⇒ 第二者被挡(单机语义不退化)
        var sharedMemory = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));
        Assert.True(await sharedMemory.LockTakeAsync(CacheConst.KeyStartupSchema, "p1", StartupSchemaInitializer.SchemaLockTtl));

        var outcome = await StartupSchemaInitializer.InitAsync(UnreachableDb(), sharedMemory, factory.CreateLogger("startup"), "p2", EntityAssemblies);
        Assert.Equal(StartupSchemaInitOutcome.SkippedHeldByOther, outcome);
        Assert.Contains(sink, m => m.Contains("启动建表/种子跳过", StringComparison.Ordinal));

        // 档位边界(差距清单 G18):**另一个** MemoryCacheService 实例(等价于另一进程)持同一键名不冲突
        // ⇒ Memory 档配多实例 = 每台照常各自建表,检测与互斥都无意义(本断言钉的是"边界",不是缺陷)
        var otherProcessSim = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));
        Assert.True(await otherProcessSim.LockTakeAsync(CacheConst.KeyStartupSchema, "p3", StartupSchemaInitializer.SchemaLockTtl));

        // WorkerId 检测同理:Memory 档两实例各自得 Registered ⇒ 永不告警
        Assert.Equal(WorkerIdGuardOutcome.Registered,
            await WorkerIdGuard.TryRegisterAsync(otherProcessSim, 63, "mem-node:1", factory.CreateLogger("worker")));

        factory.Dispose();
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 负探针客户端:指向不可达端口(127.0.0.1:1)。跳过路径**不得触碰它**——
    /// 一旦误执行 DDL 必抛,用例即红(fail-loud);配合"锁内日志不出现"双重钉住"整段跳过"。
    /// </summary>
    private static ISqlSugarClient UnreachableDb() =>
        new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=ap_none;Username=ap;Password=ap;Timeout=2",
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });

    /// <summary>日志捕获器(同 BatchJSlowSqlTest 口径):断言"说了什么"而不只是"返回了什么"。</summary>
    private static (ILoggerFactory Factory, List<string> Sink) Capture()
    {
        var sink = new List<string>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingProvider(sink)));
        return (factory, sink);
    }

    private sealed class CapturingProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add($"{logLevel}:{formatter(state, exception)}");
                }
            }
        }
    }

    /// <summary>
    /// 装饰器:委托真实实现并记录锁面调用参数(F3 对账用);两个开关把某类调用改成抛异常(F2③/F4③ 用)。
    /// </summary>
    private sealed class ProbeCache(ICacheService inner) : ICacheService
    {
        public bool ThrowOnLock { get; init; }
        public bool ThrowOnIncrement { get; init; }

        public string? TakeKey { get; private set; }
        public string? TakeToken { get; private set; }
        public TimeSpan? TakeExpiry { get; private set; }
        public int ReleaseCalls { get; private set; }
        public string? ReleaseToken { get; private set; }

        public async Task<bool> LockTakeAsync(string key, string token, TimeSpan expiry)
        {
            if (ThrowOnLock)
            {
                throw new InvalidOperationException("probe: 缓存不可用(锁面)");
            }

            TakeKey = key;
            TakeToken = token;
            TakeExpiry = expiry;
            return await inner.LockTakeAsync(key, token, expiry);
        }

        public async Task<bool> LockReleaseAsync(string key, string token)
        {
            if (ThrowOnLock)
            {
                throw new InvalidOperationException("probe: 缓存不可用(锁面)");
            }

            ReleaseCalls++;
            ReleaseToken = token;
            return await inner.LockReleaseAsync(key, token);
        }

        public async Task<long> IncrementAsync(string key, TimeSpan? expiration = null)
        {
            if (ThrowOnIncrement)
            {
                throw new InvalidOperationException("probe: 缓存不可用(计数)");
            }

            return await inner.IncrementAsync(key, expiration);
        }

        public Task SetAsync(string key, object? value, TimeSpan? expiration = null) => inner.SetAsync(key, value, expiration);
        public Task<T?> GetAsync<T>(string key) => inner.GetAsync<T>(key);
        public Task RemoveAsync(string key) => inner.RemoveAsync(key);
        public Task<TimeSpan?> GetExpireAsync(string key) => inner.GetExpireAsync(key);
        public Task<bool> ExistKeyAsync(string key) => inner.ExistKeyAsync(key);
        public Task HashSetAsync(string key, string field, object? value) => inner.HashSetAsync(key, field, value);
        public Task<T?> HashGetAsync<T>(string key, string field) => inner.HashGetAsync<T>(key, field);
        public Task<List<T>> HashGetAllAsync<T>(string key) => inner.HashGetAllAsync<T>(key);
        public Task HashDeleteAsync(string key, string field) => inner.HashDeleteAsync(key, field);
        public Task SetExpireAsync(string key, TimeSpan? expiration) => inner.SetExpireAsync(key, expiration);
        public Task<long> ListPushAsync(string key, string value, bool leftPush = true) => inner.ListPushAsync(key, value, leftPush);
        public Task<string?> ListBlockingPopAsync(string key, TimeSpan timeout) => inner.ListBlockingPopAsync(key, timeout);
        public Task<long> ListLengthAsync(string key) => inner.ListLengthAsync(key);
        public Task<List<string>> ListRangeAsync(string key, long start = 0, long stop = -1) => inner.ListRangeAsync(key, start, stop);
    }

    /// <summary>DB 容器(与生产同一条 AddSqlSugar + AddCache 路径;BatchADDatabaseTest.DbScope 同口径)。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public DbScope()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1"
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddCache(config);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
        }

        public ISqlSugarClient Db { get; }

        public ValueTask DisposeAsync()
        {
            (Db as IDisposable)?.Dispose();
            _provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(DbHost, DbPort).Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TcpReachableAsync()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(RedisHost, RedisPort).WaitAsync(TimeSpan.FromMilliseconds(2000));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>连接真实 Redis(6380,无口令)。端口可达但连接失败 = 真实故障,降级文案与"不可达"区分(同 BatchAF 口径)。</summary>
    private static async Task<IConnectionMultiplexer> ConnectRedisAsync()
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { { RedisHost, RedisPort } },
                AbortOnConnectFail = false,
                ConnectTimeout = 2000
            });
        }
        catch (Exception ex)
        {
            Assert.Fail($"Redis {RedisHost}:{RedisPort} 端口可达但连接失败:{ex.Message}——本用例未实跑,禁止冒充通过");
            throw;
        }
    }
}
