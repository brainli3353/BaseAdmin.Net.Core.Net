using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.EventBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 跨实例二级缓存失效订阅者(批 BC,G22③ 收口):收到他实例"我失效了二级缓存"的广播 ⇒
    /// 清掉**本实例登记过**的二级缓存键。
    /// </summary>
    /// <remarks>
    /// WHY 需要它(病灶):SqlSugar 的 `IsAutoRemoveDataCache` 失效路径是"枚举本适配器登记簿 ＋ 逐键删除";
    /// 登记簿是**进程内**的 ⇒ 共享 Redis 下,A 实例写库只会删 A 登记过的键,
    /// B 实例登记过的键(同表同查询,A 从没读过)**留在 Redis 里** ⇒ B 之后读到旧值(跨实例脏读)。
    /// 本订阅者把"清 B 自己的键"这一步交给 B 完成,病灶即消(零新依赖:复用批 AC 事件总线 Redis 双通道)。
    ///
    /// 回环安全:订阅者只做"删缓存值 ＋ 注销登记项",**从不发布**该事件 ⇒ 不会 A→B→A 无限放大
    /// (发布动作只在适配器的失效入口,见 <see cref="SqlSugarDataCacheService.GetAllKey{V}"/>)。
    ///
    /// 白名单:与强踢/在线推送同族,须把 <see cref="EventBusEvents.CacheInvalidate"/> 登记进
    /// <c>EventBus:CrossInstanceEvents</c> 才跨实例生效(未登记时启动有响亮告警——守卫读
    /// <see cref="EventBusEvents.CrossInstanceBuiltIns"/>);未配/未登记 = 退化为本批之前的行为(仅本实例失效)。
    ///
    /// 降级:宿主未注册缓存/登记簿(最小容器/既有测试)时,解析到 null 即静默 no-op——不因可选增强件炸掉订阅执行。
    /// </remarks>
    [Service(ServiceType = typeof(SqlSugarCacheInvalidateSubscriber))]
    public class SqlSugarCacheInvalidateSubscriber : IEventSubscriber
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SqlSugarCacheInvalidateSubscriber> _logger;

        public SqlSugarCacheInvalidateSubscriber(
            IServiceProvider serviceProvider,
            ILogger<SqlSugarCacheInvalidateSubscriber> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>处理跨实例失效广播:清本实例登记簿对应的缓存键(幂等;空登记簿为 no-op)。</summary>
        /// <param name="payload">失效载荷(本处理只依赖"事件发生",字段仅用于日志)。</param>
        /// <remarks>
        /// **SuppressMonitor(2026-09-30 自激循环事故)**:本事件是**每实例每次二级缓存失效**一条的高频低值事件,
        /// 审计落库(写 sys_log_event)会再次触发 IsAutoRemoveDataCache ⇒ GetAllKey ⇒ 又发布一条 ⇒
        /// 消费→审计→失效→发布的自激循环按消费者吞吐(实测 ~17 条/秒)永续,打满 EventBus 有界通道,
        /// 阻塞 op_log 等全部发布 ⇒ 所有控制器响应挂起(测试版登录不可用)。抑制审计后事件处理不落库,循环断开。
        /// </remarks>
        [EventSubscribe(EventBusEvents.CacheInvalidate)]
        [SuppressMonitor]
        public async Task HandleCacheInvalidateAsync(SqlSugarCacheInvalidatePayload payload)
        {
            // WHY 逐次 GetService 而非构造注入:缓存与登记簿都是**可选**依赖(AddCache/AddSqlSugar 各自独立注册),
            // 构造注入会让"没配缓存"的宿主在解析订阅者时直接抛(同 ExceptionMiddleware/批 S 执行器口径)。
            var registry = _serviceProvider.GetService<SqlSugarCacheKeyRegistry>();
            var cache = _serviceProvider.GetService<ICacheService>();
            if (registry is null || cache is null)
            {
                return; // 无二级缓存能力的宿主:纯 no-op(不是错误)
            }

            var before = registry.Count;
            if (before == 0)
            {
                return; // 本实例没缓存过任何键:无事可做(高频路径早退,不打日志)
            }

            try
            {
                await registry.PurgeAsync(cache).ConfigureAwait(false);
                _logger.LogInformation(
                    "跨实例二级缓存失效:已清本实例登记键 {Count} 个(来源 {Source};reason={Reason})",
                    before, payload?.SourceInstanceId ?? "(未知)", payload?.Reason ?? "(未知)");
            }
            catch (Exception ex)
            {
                // 失效失败只记日志:缓存是增强件,残留旧值的最坏后果是"读旧数据",
                // 不能因为清缓存失败把订阅执行链炸了(重试由批 S 执行器按策略处理)
                _logger.LogWarning(ex, "跨实例二级缓存失效清理失败(残留键将在下次失效/过期时消失)");
            }
        }
    }
}