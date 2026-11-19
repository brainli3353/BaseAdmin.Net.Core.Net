using System.Text.Encodings.Web;
using System.Text.Json;
using StackExchange.Redis;

namespace AdminCore.Core.Cache
{
    /// <summary>
    /// ICacheService 的 Redis 实现:ConnectionMultiplexer 进程级单例(CacheSetup 注册),
    /// 值统一 JSON 序列化(camelCase + 中文不转义,与 ApiResult 口径一致,键值跨实现可换用)。
    /// </summary>
    public class RedisCacheService : ICacheService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IDatabase _db;

        public RedisCacheService(IConnectionMultiplexer multiplexer)
        {
            _db = multiplexer.GetDatabase();
        }

        public Task SetAsync(string key, object? value, TimeSpan? expiration = null)
        {
            // 3.x 的 (TimeSpan?, When) 重载无默认参,When 必须显式传
            return _db.StringSetAsync(key, Serialize(value), expiration, When.Always);
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            var raw = await _db.StringGetAsync(key);
            if (raw == RedisValue.Null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(raw.ToString(), JsonOptions);
        }

        public Task RemoveAsync(string key)
            => _db.KeyDeleteAsync(key);

        public Task<bool> ExistKeyAsync(string key)
            => _db.KeyExistsAsync(key);

        public Task<TimeSpan?> GetExpireAsync(string key)
        {
            // 3.x KeyTimeToLiveAsync:正值为剩余时间;键不存在/无过期归一为 null(业务按 null=不可续期 兜底)
            return _db.KeyTimeToLiveAsync(key);
        }

        public async Task<long> IncrementAsync(string key, TimeSpan? expiration = null)
        {
            // 3.x 的 StringIncrementAsync 无 value 默认参,显式传 1
            var value = await _db.StringIncrementAsync(key, 1L);
            if (expiration is { } exp)
            {
                // TTL 只在键新建(尚无 TTL)时写:窗口从首次错误起固定 30 分钟。
                // WHY 不用"每次自增都续期":连续慢速攻击(如每 29 分钟错 1 次)可让窗口永不失效,锁定形同虚设。
                // 3.x 方法名由 TimeToLiveAsync 改为 KeyTimeToLiveAsync
                var ttl = await _db.KeyTimeToLiveAsync(key);
                // 无 TTL(null = 永不过期)才补写;TimeSpan 无 int 隐式转换,常量须用 TimeSpan.Zero
                if (ttl is null)
                {
                    await _db.KeyExpireAsync(key, exp);
                }
            }

            return value;
        }

        public Task HashSetAsync(string key, string field, object? value)
            => _db.HashSetAsync(key, field, Serialize(value));

        public async Task<T?> HashGetAsync<T>(string key, string field)
        {
            var raw = await _db.HashGetAsync(key, field);
            if (raw == RedisValue.Null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(raw.ToString(), JsonOptions);
        }

        public async Task<List<T>> HashGetAllAsync<T>(string key)
        {
            var raws = await _db.HashGetAllAsync(key);
            return raws.Select(r => JsonSerializer.Deserialize<T>(r.Value.ToString(), JsonOptions)!)
                .ToList();
        }

        public Task HashDeleteAsync(string key, string field)
            => _db.HashDeleteAsync(key, field).ContinueWith(_ => { });

        public Task SetExpireAsync(string key, TimeSpan? expiration)
            => expiration is { } exp
                ? _db.KeyExpireAsync(key, exp).ContinueWith(_ => { })
                : _db.KeyPersistAsync(key).ContinueWith(_ => { });

        // ── 批 AF 5.10 队列/锁最小原语:Redis 原生语义(SET NX PX / List / BRPOP) ──

        public Task<bool> LockTakeAsync(string key, string token, TimeSpan expiry)
            => _db.StringSetAsync(key, token, expiry, When.NotExists);

        /// <summary>
        /// 释放锁:用 Lua 把"比对 token + DEL"做成**单次原子操作**——
        /// WHY 不用 GET 再 DEL:两步之间锁可能过期并被他人取得,那时 DEL 会误删他人锁(经典错锁)。
        /// </summary>
        public async Task<bool> LockReleaseAsync(string key, string token)
        {
            const string Script = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
            var result = await _db.ScriptEvaluateAsync(Script, new RedisKey[] { key }, new RedisValue[] { token });
            return (long)result == 1;
        }

        public async Task<long> ListPushAsync(string key, string value, bool leftPush = true)
            => leftPush ? await _db.ListLeftPushAsync(key, value) : await _db.ListRightPushAsync(key, value);

        /// <summary>
        /// 阻塞弹出:走原生 <c>BRPOP</c>(服务端阻塞,不轮询、不占 CPU)。
        /// WHY 用 ExecuteAsync 而非 ListRightPopAsync:后者是**非阻塞**弹出(空队列立即返回 null);
        /// StackExchange.Redis 未暴露 BRPOP 的强类型方法,故按命令名直发。
        /// </summary>
        public async Task<string?> ListBlockingPopAsync(string key, TimeSpan timeout)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
            var result = await _db.ExecuteAsync("BRPOP", key, seconds);
            if (result.IsNull)
            {
                return null; // 超时:返回 null 而非抛(BRPOP 超时是正常分支)
            }

            var parts = (RedisResult[]?)result;
            return parts is { Length: >= 2 } ? parts[1].ToString() : null;
        }

        public async Task<long> ListLengthAsync(string key) => await _db.ListLengthAsync(key);

        public async Task<List<string>> ListRangeAsync(string key, long start = 0, long stop = -1)
        {
            var values = await _db.ListRangeAsync(key, start, stop);
            return values.Select(v => v.ToString()).ToList();
        }

        private static string Serialize(object? value) => JsonSerializer.Serialize(value, JsonOptions);
    }
}
