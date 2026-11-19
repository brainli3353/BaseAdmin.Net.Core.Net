namespace AdminCore.Core.Cache
{
    /// <summary>
    /// 缓存抽象(task_007 契约):业务代码只依赖本接口、不感知 Redis/Memory 实现。
    /// 值统一 JSON 序列化(camelCase + 中文不转义,与 Web.Core ApiResult 同一套序列化口径);
    /// 过期时间可选(null = 永不过期)。
    /// </summary>
    public interface ICacheService
    {
        /// <summary>写(JSON 序列化值;expiration 可选,null = 无过期)</summary>
        Task SetAsync(string key, object? value, TimeSpan? expiration = null);

        /// <summary>读(JSON 反序列化;键不存在/已过期返回 default)</summary>
        Task<T?> GetAsync<T>(string key);

        /// <summary>删除键</summary>
        Task RemoveAsync(string key);

        /// <summary>
        /// 读键的剩余 TTL("保 TTL 写回"场景用,如挑战失败写回不续期寿命)。
        /// 键不存在/无过期返回 null;正值为剩余时间。两实现语义须一致(业务按 null=不可续期 兜底)。
        /// </summary>
        Task<TimeSpan?> GetExpireAsync(string key);

        /// <summary>键是否存在(不存在/已过期为 false)</summary>
        Task<bool> ExistKeyAsync(string key);

        /// <summary>
        /// 原子自增(键不存在时从 0 起,返回自增后的值),供错误计数等计数场景。
        /// WHY 必须是原子原语而非"读-改-写":并发登录失败计数会丢更新,读改写非原子。
        /// </summary>
        Task<long> IncrementAsync(string key, TimeSpan? expiration = null);

        /// <summary>写哈希字段(JSON 序列化值)</summary>
        Task HashSetAsync(string key, string field, object? value);

        /// <summary>读哈希字段(JSON 反序列化;字段不存在返回 default)</summary>
        Task<T?> HashGetAsync<T>(string key, string field);

        /// <summary>读哈希全部字段(键不存在/已过期返回空列表;值 JSON 反序列化。在线用户枚举等场景,批 F 1.8)</summary>
        Task<List<T>> HashGetAllAsync<T>(string key);

        /// <summary>删哈希字段(幂等;连接断开注销/强踢,批 F 1.8)</summary>
        Task HashDeleteAsync(string key, string field);

        /// <summary>设哈希键过期(滑动续期场景,如在线用户表;expiration=null 去除过期。与 KV 的 Set 过期同语义,批 F 1.8)</summary>
        Task SetExpireAsync(string key, TimeSpan? expiration);

        // ── 批 AF 5.10 队列/锁最小原语集(零新依赖:Redis 档走 SET NX PX + List/BRPOP;Memory 档降级见各实现注释)──

        /// <summary>
        /// 取分布式锁(原子 <c>SET key token NX PX expiry</c>):拿到返回 true,已被他人持有返回 false(不阻塞)。
        /// token 为持锁者标识(通常 GUID):释放时须回传同一 token,**只有持有者能解锁**(防误释放他人锁)。
        /// 业务用法见框架使用指南 §36;键前缀约定 <see cref="CacheConst.KeyQueuePrefix"/> 同族(调用方自行拼接完整键)。
        /// </summary>
        Task<bool> LockTakeAsync(string key, string token, TimeSpan expiry);

        /// <summary>
        /// 释放分布式锁(校验 token 后删除,Redis 档用 Lua 保证"比对+删除"原子):非持有者/锁已过期返回 false。
        /// </summary>
        Task<bool> LockReleaseAsync(string key, string token);

        /// <summary>List 入队:<paramref name="leftPush"/>=true 左入(配 <see cref="ListBlockingPopAsync"/> 右出＝FIFO),false 右入。返回队列入队后长度。</summary>
        Task<long> ListPushAsync(string key, string value, bool leftPush = true);

        /// <summary>
        /// List 阻塞弹出(Redis 档＝BRPOP,Memory 档＝超时轮询):取右侧元素;超时无元素返回 null(不抛)。
        /// 语义边界:这是**点对点/竞争消费**原语(一条只被一个消费者取走)——与事件总线"广播给每实例"语义正交,勿混用(见指南 §36)。
        /// </summary>
        Task<string?> ListBlockingPopAsync(string key, TimeSpan timeout);

        /// <summary>List 长度(键不存在=0)。</summary>
        Task<long> ListLengthAsync(string key);

        /// <summary>List 范围(默认全部,按插入序;start/stop 语义同 Redis LRANGE,支持负数)。</summary>
        Task<List<string>> ListRangeAsync(string key, long start = 0, long stop = -1);
    }
}
