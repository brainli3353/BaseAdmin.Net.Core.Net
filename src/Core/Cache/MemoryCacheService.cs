using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace AdminCore.Core.Cache
{
    /// <summary>
    /// ICacheService 的内存实现(IMemoryCache):Redis 未配置/不可用时的兜底(Cache:Type=Memory)。
    /// 序列化口径与 Redis 实现一致(键值语义可换用,业务无感)。
    /// 已知边界:仅单实例(多实例部署时计数/黑名单/Token 版本各自为政)——生产必须 Redis,本实现只保开发可用。
    /// </summary>
    public class MemoryCacheService : ICacheService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IMemoryCache _cache;

        // 内存实现的哈希桶:主键 → (字段 → JSON 值)(IMemoryCache 无哈希结构,自持)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _hashes = new();

        // 内存自增的进程内锁:兜底场景并发低,锁即可(生产走 Redis 的 INCR 原子性)
        private readonly object _incrementSync = new();

        // 绝对过期登记簿:IMemoryCache 无公开 API 回读过期时间(GetExpireTimeUtc 为内部接口),
        // 写入时自持 key → 绝对过期时刻供 GetExpireAsync。已知边界:容量淘汰/进程内过期不经过 RemoveAsync,
        // 登记簿条目可能短暂滞留——剩余时间算得 ≤0 时业务按"已过期"兜底,且本实现仅单实例兜底场景使用
        private readonly ConcurrentDictionary<string, DateTimeOffset> _expires = new();

        public MemoryCacheService(IMemoryCache cache)
        {
            _cache = cache;
        }

        public Task SetAsync(string key, object? value, TimeSpan? expiration = null)
        {
            _cache.Set(key, Serialize(value), Options(expiration));
            RecordExpire(key, expiration);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out object? val) && val is string json)
            {
                return Task.FromResult(JsonSerializer.Deserialize<T>(json, JsonOptions));
            }

            return Task.FromResult<T?>(default);
        }

        public Task RemoveAsync(string key)
        {
            _cache.Remove(key);
            _expires.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistKeyAsync(string key)
            => Task.FromResult(_cache.TryGetValue(key, out _));

        public Task<TimeSpan?> GetExpireAsync(string key)
        {
            // 语义对齐 Redis 实现:未登记过期(键不存在/无过期)→ null;否则剩余时间,不足归零
            if (!_expires.TryGetValue(key, out var expireUtc))
            {
                return Task.FromResult<TimeSpan?>(null);
            }

            var remaining = expireUtc - DateTimeOffset.UtcNow;
            return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        public Task<long> IncrementAsync(string key, TimeSpan? expiration = null)
        {
            // 语义对齐 Redis INCR:键不存在时从 0 起(首次自增返回 1)
            lock (_incrementSync)
            {
                long value = 1;
                if (_cache.TryGetValue(key, out object? old) && old is string s && long.TryParse(s, out var cur))
                {
                    value = cur + 1;
                }

                _cache.Set(key, value.ToString(), Options(expiration));
                RecordExpire(key, expiration);
                return Task.FromResult(value);
            }
        }

        public Task HashSetAsync(string key, string field, object? value)
        {
            var bucket = _hashes.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>());
            bucket[field] = Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> HashGetAsync<T>(string key, string field)
        {
            if (TryGetHashBucket(key, out var bucket) && bucket.TryGetValue(field, out var raw))
            {
                return Task.FromResult(JsonSerializer.Deserialize<T>(raw, JsonOptions));
            }

            return Task.FromResult<T?>(default);
        }

        public Task<List<T>> HashGetAllAsync<T>(string key)
        {
            if (TryGetHashBucket(key, out var bucket))
            {
                return Task.FromResult(bucket.Values
                    .Select(raw => JsonSerializer.Deserialize<T>(raw, JsonOptions)!).ToList());
            }

            return Task.FromResult(new List<T>());
        }

        public Task HashDeleteAsync(string key, string field)
        {
            if (_hashes.TryGetValue(key, out var bucket))
            {
                bucket.TryRemove(field, out _);
            }

            return Task.CompletedTask;
        }

        public Task SetExpireAsync(string key, TimeSpan? expiration)
        {
            RecordExpire(key, expiration);
            return Task.CompletedTask;
        }

        // 哈希桶过期判定:登记簿剩余时间 ≤0 → 整桶移除(对齐 Redis 键过期;桶级而非字段级)
        private bool TryGetHashBucket(string key, out ConcurrentDictionary<string, string> bucket)
        {
            if (_hashes.TryGetValue(key, out bucket!))
            {
                if (_expires.TryGetValue(key, out var expireUtc) && expireUtc <= DateTimeOffset.UtcNow)
                {
                    _hashes.TryRemove(key, out _);
                    _expires.TryRemove(key, out _);
                    return false;
                }

                return true;
            }

            return false;
        }

        // 维护绝对过期登记簿(与写入同口径):有过期 → 登记绝对时刻;无过期 → 撤销登记
        private void RecordExpire(string key, TimeSpan? expiration)
        {
            if (expiration is { } exp)
            {
                _expires[key] = DateTimeOffset.UtcNow + exp;
            }
            else
            {
                _expires.TryRemove(key, out _);
            }
        }

        private static MemoryCacheEntryOptions? Options(TimeSpan? expiration)
            => expiration is { } exp ? new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = exp } : null;

        // ── 批 AF 5.10 队列/锁最小原语(内存降级档) ──
        // 语义边界(必须知悉):本档**只在单进程内成立**——锁不跨进程(多实例部署时各自持锁)、
        // 队列不跨实例(生产多实例请用 Redis 档)。阻塞弹出用**超时轮询**(50ms 粒度)模拟 BRPOP。
        private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expire)> _locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<string>> _listQueues = new(StringComparer.Ordinal);
        private readonly object _lockGate = new();
        private readonly object _listGate = new();

        public Task<bool> LockTakeAsync(string key, string token, TimeSpan expiry)
        {
            lock (_lockGate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_locks.TryGetValue(key, out var current) && current.Expire > now)
                {
                    return Task.FromResult(false); // 他人持有且未过期
                }

                _locks[key] = (token, now + expiry); // 无锁/已过期 → 取得(覆盖过期条目)
                return Task.FromResult(true);
            }
        }

        public Task<bool> LockReleaseAsync(string key, string token)
        {
            lock (_lockGate)
            {
                if (_locks.TryGetValue(key, out var current) && string.Equals(current.Token, token, StringComparison.Ordinal))
                {
                    _locks.TryRemove(key, out _);
                    return Task.FromResult(true);
                }

                return Task.FromResult(false); // 非持有者(或已过期被他人接管):不解他人锁
            }
        }

        public Task<long> ListPushAsync(string key, string value, bool leftPush = true)
        {
            lock (_listGate)
            {
                var list = _listQueues.GetOrAdd(key, _ => new List<string>());
                if (leftPush)
                {
                    list.Insert(0, value);
                }
                else
                {
                    list.Add(value);
                }

                return Task.FromResult((long)list.Count);
            }
        }

        public async Task<string?> ListBlockingPopAsync(string key, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                lock (_listGate)
                {
                    if (_listQueues.TryGetValue(key, out var list) && list.Count > 0)
                    {
                        var value = list[^1]; // 右出(与 Redis 档 BRPOP 同向):配 leftPush 即 FIFO
                        list.RemoveAt(list.Count - 1);
                        return value;
                    }
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null; // 超时:与 Redis 档一致返回 null
                }

                await Task.Delay(50);
            }
        }

        public Task<long> ListLengthAsync(string key)
        {
            lock (_listGate)
            {
                return Task.FromResult(_listQueues.TryGetValue(key, out var list) ? (long)list.Count : 0L);
            }
        }

        public Task<List<string>> ListRangeAsync(string key, long start = 0, long stop = -1)
        {
            lock (_listGate)
            {
                if (!_listQueues.TryGetValue(key, out var list) || list.Count == 0)
                {
                    return Task.FromResult(new List<string>());
                }

                var from = start < 0 ? (int)Math.Max(0, list.Count + start) : (int)Math.Min(start, list.Count);
                var to = stop < 0 ? list.Count - 1 : (int)Math.Min(stop, list.Count - 1);
                return Task.FromResult(to < from ? new List<string>() : list.GetRange(from, to - from + 1));
            }
        }

        private static string Serialize(object? value) => JsonSerializer.Serialize(value, JsonOptions);
    }
}
