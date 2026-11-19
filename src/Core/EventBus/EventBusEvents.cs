using System.Collections.ObjectModel;

namespace AdminCore.Core.EventBus
{
    /// <summary>
    /// 框架内建**跨实例事件名**常量单点(批 AU 起,批 AV 扩清单)。
    /// </summary>
    /// <remarks>
    /// WHY 单点:跨实例事件名同时出现在三个地方——① **发布侧**(服务调用 <c>PublishAsync</c>)② **订阅侧**
    /// (<c>[EventSubscribe(...)]</c>)③ **部署白名单**(配置 <c>EventBus:CrossInstanceEvents</c>,逗号分隔)。
    /// 三处散字面量时,任何一处拼错都表现为"看着配了却不生效"(静默失效,最难查)——故由常量收口,
    /// 文档与部署配置直接引用本值(逐字一致)。
    ///
    /// 与批 AC 口径的关系:白名单**仍是唯一准入闸**(双侧同判据,防手工 PUBLISH 注入旁路);
    /// 本件只是把"内建可用事件"的名字集中登记,不改变白名单语义。
    ///
    /// <see cref="CrossInstanceBuiltIns"/> 是"清单一处改、守卫与文档同步"的载体:EventBus 构造期遍历它逐项判定,
    /// 部署检查单与使用指南 §31.5 也照它写(新增内建跨实例事件时**只改这里 + 加订阅者**)。
    /// </remarks>
    public static class EventBusEvents
    {
        /// <summary>
        /// 跨实例强制下线(批 AU):A 实例踢连接时广播,持有该连接的实例收到后向本机客户端推
        /// <c>forceOffline</c>——解「SignalR 无 backplane ⇒ 强踢接口 200 但用户不掉线」。
        /// **部署须把它登记进** <c>EventBus:CrossInstanceEvents</c>(未登记时启动有响亮告警,见 EventBus 守卫)。
        /// </summary>
        public const string OnlineForceOffline = "online_force_offline";

        /// <summary>
        /// 跨实例在线推送(批 AV,**一个事件名覆盖三类推送**):通知公告(<c>PublicNotice</c>)、
        /// 站内消息广播(<c>ReceiveMessage</c> 给全部连接)、站内消息单播(<c>ReceiveMessage</c> 给指定连接)。
        /// 由 <see cref="OnlinePushKinds"/> 区分类型、由载荷的 <c>ConnectionIds</c>/<c>BroadcastAll</c> 区分目标。
        /// WHY 合成一个事件名而非三个:三者同构(都是"他实例代我推本机连接"),合并后**白名单只登记一个名字**,
        /// 运维成本与出错面都更小(部署检查单一行)。
        /// </summary>
        public const string OnlinePush = "online_push";

        /// <summary>
        /// 跨实例二级缓存失效(批 BC,G22③):A 实例写库触发 SqlSugar 数据缓存失效时广播,持有"自己登记过
        /// 二级缓存键"的实例收到后清掉本实例的键——解「共享 Redis 下 A 只删得掉自己登记过的键,
        /// B 登记的键留在缓存里 ⇒ B 读到旧值」。
        /// **部署须把它登记进** <c>EventBus:CrossInstanceEvents</c>(未登记时启动有响亮告警,见 EventBus 守卫);
        /// 未登记＝退化为"仅本实例失效"(与本批之前行为一致,属未启用而非故障)。
        /// </summary>
        public const string CacheInvalidate = "sqlsugar_cache_invalidate";

        /// <summary>
        /// 内建跨实例事件清单(事件名 → 用途标签):守卫文案与文档清单的唯一数据源。
        /// </summary>
        public static readonly ReadOnlyDictionary<string, string> CrossInstanceBuiltIns = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OnlineForceOffline] = "强踢",
                [OnlinePush] = "在线推送",
                [CacheInvalidate] = "二级缓存失效"
            });
    }

    /// <summary>
    /// 跨实例在线推送的**类型标签**(批 AV F1):随载荷 <c>Kind</c> 传输,订阅侧据此分派。
    /// </summary>
    /// <remarks>
    /// WHY 用字符串常量而非枚举:枚举经 System.Text.Json 缺省序列化为**数字**——一旦将来增删枚举成员,
    /// 新旧实例对同一个数字的理解会错位(滚动发布期间新旧并存时尤其危险);字符串则天然向后兼容(未知值可告警跳过)。
    /// </remarks>
    public static class OnlinePushKinds
    {
        /// <summary>通知公告:逐连接推 <c>PublicNotice</c>(载荷取 <c>Notice</c>)。</summary>
        public const string Notice = "notice";

        /// <summary>站内消息:按 <c>BroadcastAll</c>/<c>ConnectionIds</c> 推 <c>ReceiveMessage</c>(载荷取 <c>Message</c>)。</summary>
        public const string Message = "message";
    }
}