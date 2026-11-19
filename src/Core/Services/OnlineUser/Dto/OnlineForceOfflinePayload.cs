namespace AdminCore.Core.Services.OnlineUser.Dto
{
    /// <summary>
    /// 跨实例强制下线载荷(批 AU F3):随事件 <see cref="EventBus.EventBusEvents.OnlineForceOffline"/> 跨实例广播。
    /// </summary>
    /// <remarks>
    /// WHY 带 <see cref="SourceInstanceId"/>:事件总线**本地投递是固有行为**——发布方所在实例也会收到自己发的那份
    /// (Redis 副本才被自回环丢弃)。若不区分来源,本实例会"本地直推一次 + 订阅者再推一次"(双推,客户端收到两条通知)。
    /// 故载荷带来源实例 Id,订阅者与自己相同即跳过 ⇒ 本地直推管本机连接、跨实例事件管他机连接,三态互不重复。
    ///
    /// WHY 用 <c>InstanceIdentity.Current</c> 而非配置标签身份:自跳判据只要求"同进程内两侧算得出同一个值",
    /// 与 NodeTag 无关(标签是运维语义);GUID 进程唯一已足够。
    /// </remarks>
    /// <param name="ConnectionId">被踢的连接 Id(SignalR connectionId)。</param>
    /// <param name="SourceInstanceId">发布方实例标识(订阅者据此自跳,防本地双推)。</param>
    /// <param name="Reason">提示文案(缺省由订阅者回落"强制下线")。</param>
    public sealed record OnlineForceOfflinePayload(string ConnectionId, string SourceInstanceId, string? Reason);
}