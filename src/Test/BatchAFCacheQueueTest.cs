using System.Diagnostics;
using System.Net.Sockets;
using AdminCore.Core.Cache;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AF(5.10 队列/锁最小原语)测试:被测主体 = <see cref="ICacheService"/> 新增 6 原语
/// (<see cref="ICacheService.LockTakeAsync"/> / <see cref="ICacheService.LockReleaseAsync"/> /
/// <see cref="ICacheService.ListPushAsync"/> / <see cref="ICacheService.ListBlockingPopAsync"/> /
/// <see cref="ICacheService.ListLengthAsync"/> / <see cref="ICacheService.ListRangeAsync"/>)的**两实现**:
/// <see cref="RedisCacheService"/>(真实 Redis 127.0.0.1:6380)与 <see cref="MemoryCacheService"/>(内存降级档)。
/// </summary>
/// <remarks>
/// WHY 直接 new 实现而非走 AddCache 容器:被测的正是"两实现在同接口下语义一致"——
/// 容器注册只会给其中一个实现,两档对照必须各自显式构造(Redis 档构造签名 = IConnectionMultiplexer,
/// Memory 档 = IMemoryCache,均为公开构造,无 IOptions 依赖)。
/// WHY 全部键加唯一前缀 afq_{guid}:Redis 是**进程外共享资源**,固定键名会被历次运行的残留键
/// 或并发用例串扰(锁被"别人"持有 / 队列里混入别的元素),唯一前缀 + finally 逐键 RemoveAsync 保证零污染;
/// 绝不用 KEYS */FLUSHDB(那是全库操作,会毁掉同 Redis 上别的数据)。
/// Redis 不可达时按既有口径 Assert.Fail 显式降级(禁止冒充通过,文案同 BatchACEventBusRedisTest)。
/// </remarks>
public class BatchAFCacheQueueTest
{
    private readonly ITestOutputHelper _output;

    public BatchAFCacheQueueTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 环境事实:事件总线 Redis 在 6380(无口令,已实测 PONG);6379 是需口令的业务 Redis,勿混用
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    // 诚实降级文案(与 BatchACEventBusRedisTest 同口径)
    private const string Downgrade = "降级:Redis 127.0.0.1:6380 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // 1. 锁:互斥、非持有者释放无效、持有者释放后立即可重取
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 锁_互斥与释放()
    {
        if (!await TcpReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var multiplexer = await ConnectRedisAsync();
        var cache = new RedisCacheService(multiplexer);
        var keys = NewKeys("lock");
        try
        {
            var key = keys[0];

            Assert.True(await cache.LockTakeAsync(key, "t1", TimeSpan.FromSeconds(30)), "空键应可加锁");

            // 已被他人持有:不阻塞、直接 false(不是等待)
            Assert.False(await cache.LockTakeAsync(key, "t2", TimeSpan.FromSeconds(30)), "同键第二次加锁应失败");

            // 非持有者释放:false 且**不得误删他人锁**
            Assert.False(await cache.LockReleaseAsync(key, "t2"), "非持有者释放应返回 false");
            Assert.False(await cache.LockTakeAsync(key, "t3", TimeSpan.FromSeconds(30)), "非持有者释放后锁应仍在");

            // 持有者释放:true,键被删除
            Assert.True(await cache.LockReleaseAsync(key, "t1"), "持有者释放应返回 true");
            Assert.False(await cache.ExistKeyAsync(key), "释放后锁键应不存在");

            // 释放后可立即再取(无残留 TTL 阻塞)
            Assert.True(await cache.LockTakeAsync(key, "t4", TimeSpan.FromSeconds(30)), "释放后应可重新加锁");

            _output.WriteLine("判定:互斥 true/false、非持有者释放 false 且锁仍在、持有者释放 true 后可重取");
        }
        finally
        {
            await RemoveKeysAsync(cache, keys);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 锁:到期自动失效,可被他人夺取;旧 token 不得误删新持有者的锁
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 锁_过期后可再取()
    {
        if (!await TcpReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var multiplexer = await ConnectRedisAsync();
        var cache = new RedisCacheService(multiplexer);
        var keys = NewKeys("lockexp");
        try
        {
            var key = keys[0];

            Assert.True(await cache.LockTakeAsync(key, "t1", TimeSpan.FromMilliseconds(100)));
            await Task.Delay(300); // 越过 100ms 的锁寿命(SET NX PX)

            Assert.True(await cache.LockTakeAsync(key, "t2", TimeSpan.FromSeconds(30)), "过期锁应可被夺取");

            // 关键:已过期的旧持有者释放时必须失败(Lua 比对 token 才删)——否则会误删新持有者的锁
            Assert.False(await cache.LockReleaseAsync(key, "t1"), "过期持有者释放应返回 false");
            Assert.True(await cache.ExistKeyAsync(key), "新持有者的锁不应被旧 token 释放掉");
            Assert.False(await cache.LockTakeAsync(key, "t3", TimeSpan.FromSeconds(30)), "新持有者仍在持锁");

            Assert.True(await cache.LockReleaseAsync(key, "t2"));
            _output.WriteLine("判定:100ms 锁过期后 t2 可取;旧 token t1 释放返回 false 且新锁未被误删");
        }
        finally
        {
            await RemoveKeysAsync(cache, keys);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 队列:左入右出 = FIFO;长度/范围语义
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 队列_FIFO与长度与范围()
    {
        if (!await TcpReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var multiplexer = await ConnectRedisAsync();
        var cache = new RedisCacheService(multiplexer);
        var keys = NewKeys("queue");
        try
        {
            var key = keys[0];

            Assert.Equal(1, await cache.ListPushAsync(key, "a")); // 默认左入
            Assert.Equal(2, await cache.ListPushAsync(key, "b"));
            Assert.Equal(2, await cache.ListLengthAsync(key));

            // 左入语义:最新入队在表头 → 范围序为 [b, a](与 Redis LPUSH + LRANGE 同构)
            Assert.Equal(new List<string> { "b", "a" }, await cache.ListRangeAsync(key));

            // BRPOP 右出:先入先出 → 先弹 a 再弹 b
            Assert.Equal("a", await cache.ListBlockingPopAsync(key, TimeSpan.FromSeconds(1)));
            Assert.Equal("b", await cache.ListBlockingPopAsync(key, TimeSpan.FromSeconds(1)));

            Assert.Equal(0, await cache.ListLengthAsync(key));
            Assert.Empty(await cache.ListRangeAsync(key)); // 空/不存在键 → 空列表(不抛)

            _output.WriteLine("判定:左入 [b,a]、长度 2→0、右出序 a→b(FIFO)");
        }
        finally
        {
            await RemoveKeysAsync(cache, keys);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 空队列阻塞弹出:超时返回 null(不抛),且确实阻塞过
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 队列_空队列超时返回null()
    {
        if (!await TcpReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var multiplexer = await ConnectRedisAsync();
        var cache = new RedisCacheService(multiplexer);
        var keys = NewKeys("emptyqueue");
        try
        {
            var key = keys[0];
            Assert.Equal(0, await cache.ListLengthAsync(key)); // 键根本不存在

            var stopwatch = Stopwatch.StartNew();
            var value = await cache.ListBlockingPopAsync(key, TimeSpan.FromMilliseconds(500));
            stopwatch.Stop();

            Assert.Null(value); // 超时是正常分支:返回 null 而非抛
            // 证明是"真阻塞"而非"空队列立即返回 null"(后者会让消费循环空转吃 CPU)
            Assert.True(stopwatch.ElapsedMilliseconds >= 400,
                $"空队列弹出应阻塞至超时,实际仅 {stopwatch.ElapsedMilliseconds}ms");
            // Redis 档 BRPOP 只有秒粒度,实现把 <1s 的入参向上取整到 1s → 实际约 1s(口径已知,非缺陷)
            Assert.True(stopwatch.ElapsedMilliseconds < 5000,
                $"阻塞耗时应为秒级(实现向上取整),实际 {stopwatch.ElapsedMilliseconds}ms");

            Assert.Equal(0, await cache.ListLengthAsync(key));

            _output.WriteLine($"判定:空队列弹出返回 null 且耗时 {stopwatch.ElapsedMilliseconds}ms(秒粒度向上取整)");
        }
        finally
        {
            await RemoveKeysAsync(cache, keys);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. Memory 档:同接口的锁/队列语义(单实例口径)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Memory档_同接口可用()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryCacheService(memoryCache);
        // 锁键与队列键各一条(同一键空间前缀下两个不同键)
        var keys = NewKeys("mem", "memlock", "memqueue");
        try
        {
            // ① 锁:互斥 + 非持有者释放 false + 持有者释放 true 后可重取(与 Redis 档同断言)
            var lockKey = keys[0];
            Assert.True(await cache.LockTakeAsync(lockKey, "t1", TimeSpan.FromSeconds(30)));
            Assert.False(await cache.LockTakeAsync(lockKey, "t2", TimeSpan.FromSeconds(30)));
            Assert.False(await cache.LockReleaseAsync(lockKey, "t2"));
            Assert.False(await cache.LockTakeAsync(lockKey, "t3", TimeSpan.FromSeconds(30)));
            Assert.True(await cache.LockReleaseAsync(lockKey, "t1"));
            Assert.True(await cache.LockTakeAsync(lockKey, "t4", TimeSpan.FromSeconds(30)));

            // ② 队列:左入右出 FIFO + 长度/范围
            var queueKey = keys[1];
            Assert.Equal(1, await cache.ListPushAsync(queueKey, "a"));
            Assert.Equal(2, await cache.ListPushAsync(queueKey, "b"));
            Assert.Equal(2, await cache.ListLengthAsync(queueKey));
            Assert.Equal(new List<string> { "b", "a" }, await cache.ListRangeAsync(queueKey));
            Assert.Equal("a", await cache.ListBlockingPopAsync(queueKey, TimeSpan.FromSeconds(1)));
            Assert.Equal("b", await cache.ListBlockingPopAsync(queueKey, TimeSpan.FromSeconds(1)));
            Assert.Equal(0, await cache.ListLengthAsync(queueKey));

            // 已知边界(不断言,只披露):Memory 档的锁与队列都只活在**单个 MemoryCacheService 实例的进程内字典**里,
            // 不跨进程、不跨实例(多实例部署时各自持锁/各自入队)——故此处刻意不建第二个实例做"跨实例可见"断言
            // (生产多实例必须 Redis 档,见 MemoryCacheService 类注释)。
            _output.WriteLine("判定:Memory 档锁互斥/释放/重取与队列 FIFO 等同 Redis 档;已知边界=不跨进程(未断言跨实例)");
        }
        finally
        {
            await RemoveKeysAsync(cache, keys);
        }
    }

    // ── 探针工具 ─────────────────────────────────────────────────────────────

    /// <summary>唯一键空间前缀(每次调用唯一):防残留键/并发用例串扰。</summary>
    private static List<string> NewKeys(params string[] tags)
        => tags.Select(tag => $"afq_{Guid.NewGuid():N}:{tag}").ToList();

    /// <summary>逐键物理删(finally 清理);绝不做 KEYS */FLUSH 之类的全库操作。</summary>
    private static async Task RemoveKeysAsync(ICacheService cache, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            await cache.RemoveAsync(key);
        }
    }

    /// <summary>连接真实 Redis(6380)。探测已确认可达,故此处失败属真实故障,降级文案须与"不可达"区分。</summary>
    private static async Task<IConnectionMultiplexer> ConnectRedisAsync()
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { { RedisHost, RedisPort } },
                // 探测已通过:此处不为瞬时抖动放弃(生产 CacheSetup 走默认 abortConnect=true,是启动期快速失败口径)
                AbortOnConnectFail = false,
                ConnectTimeout = 2000
            });
        }
        catch (Exception ex)
        {
            Assert.Fail($"Redis {RedisHost}:{RedisPort} 端口可达但连接失败:{ex.Message}——本用例未实跑,禁止冒充通过");
            throw; // Assert.Fail 之后的兜底(不可达分支必须让用例红,不得静默通过)
        }
    }

    /// <summary>2 秒 TCP 探测(与 BatchACEventBusRedisTest 同口径)。</summary>
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
}