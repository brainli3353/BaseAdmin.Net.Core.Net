using AdminCore.Core.Entities;

namespace AdminCore.Core.Services.OnlineUser.Dto
{
    /// <summary>
    /// 跨实例在线推送载荷(批 AV F2):随事件 <see cref="EventBus.EventBusEvents.OnlinePush"/> 跨实例广播,
    /// 覆盖三类推送——通知公告、站内消息广播、站内消息单播。
    /// </summary>
    /// <remarks>
    /// WHY 用可空字段按 <see cref="Kind"/> 二选一(而非 string 里嵌 JSON):批 AJ 已实证反模式——
    /// 预序列化串会变成"串中串",Memory 档读侧反序列化直接失败;对象字段随信封整体序列化,两档形态一致。
    ///
    /// WHY 带 <see cref="SourceInstanceId"/>:事件总线**本地投递是固有行为**(发布方实例也会收到自己那份,
    /// Redis 副本才被自回环丢弃)⇒ 订阅者按来源自跳,避免"本地直推 + 订阅重推"双推(同批 AU 口径)。
    ///
    /// WHY 用 <see cref="List{T}"/> 而不是 <c>IReadOnlyList</c>/接口类型:接口在 System.Text.Json 反序列化侧
    /// 需要额外约定,跨实例信封以具体集合类型最稳(读侧还原后仍是可枚举的具体表)。
    /// </remarks>
    /// <param name="SourceInstanceId">发布方实例标识(订阅者据此自跳)。</param>
    /// <param name="Kind">类型标签,取 <see cref="EventBus.OnlinePushKinds"/> 常量。</param>
    /// <param name="ConnectionIds">目标连接集(kind=notice 必填;kind=message 且非广播时必填)。</param>
    /// <param name="BroadcastAll">kind=message 时是否推给本实例**全部**连接(等价 <c>Clients.All</c>)。</param>
    /// <param name="Notice">kind=notice 的载荷(通知公告实体)。</param>
    /// <param name="Message">kind=message 的载荷(站内消息上下文,原样透传给客户端)。</param>
    public sealed record OnlinePushPayload(
        string SourceInstanceId,
        string Kind,
        List<string>? ConnectionIds,
        bool BroadcastAll,
        SysNotice? Notice,
        object? Message);
}