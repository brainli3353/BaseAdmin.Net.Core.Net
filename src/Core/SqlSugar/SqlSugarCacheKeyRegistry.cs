using System.Collections.Concurrent;
using AdminCore.Core.Cache;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// SqlSugar 二级缓存**键登记簿**(批 R 引入为适配器私有字段;批 BC 提为 DI 单例)。
    /// 记录"本实例写过哪些二级缓存键"——SqlSugar 的失效路径＝`GetAllKey` 枚举 ＋ 逐键 `Remove`,
    /// 而本仓缓存抽象**不开放前缀枚举**(不给业务 SCAN 口子),故需此登记表补足枚举能力。
    /// </summary>
    /// <remarks>
    /// WHY 提为单例(批 BC):跨实例失效要由**订阅者**清"本实例登记的键",订阅者必须拿到与适配器**同一个**
    /// 登记簿实例——原先是适配器私有字段,订阅者无从触及;改用静态字段则违反本仓"零静态态"口径(批 P/批 AE)。
    /// 语义:只增不删(失效/Remove 时移除);过期键残留无害(底层键已过期,枚举到后删除幂等)。
    /// 体量:受二级缓存键基数约束(实体×查询形态);**Memory 档**下它只在本进程可见——多实例须 Redis 档(差距清单 G18 同族边界)。
    /// </remarks>
    public sealed class SqlSugarCacheKeyRegistry
    {
        private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

        /// <summary>本实例已登记的键数(诊断/断言用)。</summary>
        public int Count => _keys.Count;

        /// <summary>登记一个全键(含 <c>sys_sqlSugar:</c> 前缀;幂等)。</summary>
        public void Track(string fullKey) => _keys.TryAdd(fullKey, 0);

        /// <summary>注销一个全键(幂等)。</summary>
        public void Forget(string fullKey) => _keys.TryRemove(fullKey, out _);

        /// <summary>当前登记键的快照(数组拷贝;枚举期间并发写安全)。</summary>
        public IReadOnlyCollection<string> Snapshot() => _keys.Keys.ToArray();

        /// <summary>
        /// 清本实例登记的**全部**键:逐键删底层缓存值并同步注销登记项。
        /// 执行体＝跨实例失效订阅者(批 BC):他实例写库时会广播"我要失效二级缓存",
        /// 本实例收到后清掉**自己**登记过的键(共享 Redis 下这一步才让别的实例的缓存真正消失)。
        /// 失败语义:单键删除异常只向上抛给订阅者(由其记日志),不在这里吞。
        /// </summary>
        public async Task PurgeAsync(ICacheService cache, CancellationToken cancellationToken = default)
        {
            foreach (var key in Snapshot())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await cache.RemoveAsync(key).ConfigureAwait(false);
                Forget(key);
            }
        }
    }
}