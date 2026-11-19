using AdminCore.Core.Cache;
using AdminCore.Core.EventBus;
using AdminCore.Core.Utils;
using Microsoft.Extensions.Logging;

// SqlSugar.ICacheService(数据缓存接口)与 Core ICacheService(缓存抽象)同名——
// 本文件全限定引用两者,短名 ICacheService 恒指 Core 缓存抽象(本仓既有别名先例口径)
using SqlSugarCacheAdapter = SqlSugar.ICacheService;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// SqlSugar 二级缓存适配器(批 R 3.5,参照 Admin.NET SqlSugarCache 移植;批 BC 补跨实例失效):
    /// 实现 SqlSugar 数据缓存接口,委托本仓 <see cref="AdminCore.Core.Cache.ICacheService"/>
    /// (Redis/Memory 双实现,业务与二级缓存共享同一键空间底层,前缀隔离);
    /// 挂点=SqlSugarSetup 的 ConnectionConfig.ConfigureExternalServices.DataInfoCacheService +
    /// ConnMoreSettings.IsAutoRemoveDataCache(增删改自动失效,详见 g12)。
    /// 参照缺陷修复:参照挂钩后全仓 WithCache() 零消费(死挂钩)——本仓测试证闭环(g12 §4),
    /// 业务查询 opt-in 启用(.WithCache()),框架层零默认启用(#8,与批 P 同口径)。
    /// </summary>
    public class SqlSugarDataCacheService : SqlSugarCacheAdapter
    {
        /// <summary>键前缀(与参照 CacheConst.SqlSugar 同值):隔离二级缓存键与业务键</summary>
        private const string KeyPrefix = "sys_sqlSugar:";

        private readonly AdminCore.Core.Cache.ICacheService _cache;

        /// <summary>
        /// 键登记簿(批 R 的进程内登记表;批 BC 提为可注入单例,见 <see cref="SqlSugarCacheKeyRegistry"/>):
        /// GetAllKey 的数据源。WHY 需要:SqlSugar 的 RemoveDataCache 失效路径=GetAllKey 枚举 + 逐键 Remove
        /// (实机诊断 g12 §7:空 GetAllKey = 增删改自动失效零生效);本仓缓存抽象无键枚举接口
        /// (前缀枚举不开放给业务),适配器靠登记表补足 SqlSugar 失效路径所需枚举能力。
        /// 未注入时自建一份(直构场景/单测):与批 R 行为逐字一致(独立登记表,单实例语义)。
        /// </summary>
        private readonly SqlSugarCacheKeyRegistry _registry;

        /// <summary>跨实例失效发布器(批 BC,可选):未注册事件总线的宿主=null ⇒ 退化为仅本实例失效。</summary>
        private readonly IEventPublisher? _publisher;

        private readonly ILogger<SqlSugarDataCacheService>? _logger;

        public SqlSugarDataCacheService(
            AdminCore.Core.Cache.ICacheService cache,
            SqlSugarCacheKeyRegistry? registry = null,
            IEventPublisher? publisher = null,
            ILogger<SqlSugarDataCacheService>? logger = null)
        {
            _cache = cache;
            _registry = registry ?? new SqlSugarCacheKeyRegistry();
            _publisher = publisher;
            _logger = logger;
        }

        public void Add<V>(string key, V value)
        {
            _registry.Track(KeyPrefix + key);
            _cache.SetAsync(KeyPrefix + key, value).GetAwaiter().GetResult();
        }

        public void Add<V>(string key, V value, int cacheDurationInSeconds)
        {
            _registry.Track(KeyPrefix + key);
            _cache.SetAsync(KeyPrefix + key, value, ToExpiration(cacheDurationInSeconds)).GetAwaiter().GetResult();
        }

        public bool ContainsKey<V>(string key)
            => _cache.ExistKeyAsync(KeyPrefix + key).GetAwaiter().GetResult();

        public V Get<V>(string key)
            => _cache.GetAsync<V>(KeyPrefix + key).GetAwaiter().GetResult()!; // 键不存在返回 default(V),SqlSugar 数据缓存语义允许

        // 枚举全量键(含前缀——SqlSugar RemoveDataCache 失效路径依赖:枚举 + 逐键 Remove)。
        // 数据源=本实例登记表(见 _registry 注释)。
        // **批 BC:本方法是"失效发生"的唯一入口**(实机诊断 g12 §7:SqlSugar 只在失效路径调它)⇒
        // 在这里广播一次跨实例失效通知,持有自己登记键的其它实例收到后清各自的键。
        // WHY 挂在枚举而不是 Remove:Remove 是"逐键"调用(一次失效会来 N 次),挂在枚举上=一次失效一次广播,
        // 且订阅侧只删不发布 ⇒ 无 A→B→A 回环。
        // **2026-09-30 修复(自激循环事故):仅当本实例登记簿非空才广播**——空登记簿意味着本实例
        // 没有任何二级缓存可失效,"我失效了"的广播是纯噪音;而消费侧(审计落库)的每次写库又会
        // 触发本方法(IsAutoRemoveDataCache),噪音事件自我放大打满 EventBus 有界通道,
        // 把 op_log 等所有发布堵死 ⇒ 全部控制器响应挂起(登录不可用)。见 docs/框架索引.md g12。
        public IEnumerable<string> GetAllKey<V>()
        {
            var keys = _registry.Snapshot();
            if (keys.Count > 0)
            {
                NotifyCrossInstanceInvalidate();
            }

            return keys;
        }

        public V GetOrCreate<V>(string key, Func<V> create, int cacheDurationInSeconds = int.MaxValue)
        {
            var fullKey = KeyPrefix + key;
            if (_cache.ExistKeyAsync(fullKey).GetAwaiter().GetResult())
            {
                return _cache.GetAsync<V>(fullKey).GetAwaiter().GetResult()!; // 键不存在返回 default(V),SqlSugar 数据缓存语义允许
            }

            var value = create();
            _registry.Track(fullKey);
            _cache.SetAsync(fullKey, value, ToExpiration(cacheDurationInSeconds)).GetAwaiter().GetResult();
            return value;
        }

        // 与参照同口径:SqlSugar 调 Remove 时 key 已含前缀(来自 GetAllKey 枚举值),此处不再加前缀——
        // 写路径(Add/Get)传裸键、删/枚举路径传全键,是 SqlSugar 数据缓存的固有不对称约定
        public void Remove<V>(string key)
        {
            _registry.Forget(key);
            _cache.RemoveAsync(key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 广播"本实例刚失效二级缓存"(批 BC):fire-and-forget,**失败只告警不抛出**——
        /// 失效是增强语义,广播失败的最坏后果是"他实例留旧值"(与本批之前同等),不该让写库路径失败。
        /// </summary>
        private void NotifyCrossInstanceInvalidate()
        {
            if (_publisher is null)
            {
                return; // 无事件总线(最小宿主/单测直构):仅本实例失效,行为同批 BC 之前
            }

            try
            {
                var payload = new SqlSugarCacheInvalidatePayload(
                    InstanceIdentity.Current.InstanceId, "sqlsugar_data_cache", DateTimeOffset.UtcNow);
                _ = _publisher
                    .PublishAsync(EventBusEvents.CacheInvalidate, payload)
                    .ContinueWith(
                        t => _logger?.LogWarning(t.Exception, "跨实例二级缓存失效广播失败(本次仅本实例失效)"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                // PublishAsync 同步段抛(如入队校验失败):同样只告警
                _logger?.LogWarning(ex, "跨实例二级缓存失效广播发起失败(本次仅本实例失效)");
            }
        }

        // int.MaxValue = 永不过期(本仓 SetAsync(null) 口径)
        private static TimeSpan? ToExpiration(int seconds)
            => seconds == int.MaxValue ? null : TimeSpan.FromSeconds(seconds);
    }
}