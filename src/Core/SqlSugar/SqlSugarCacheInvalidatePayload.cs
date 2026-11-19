namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 二级缓存失效广播载荷(批 BC,G22③ 收口):随事件 <see cref="AdminCore.Core.EventBus.EventBusEvents.CacheInvalidate"/>
    /// 跨实例广播,通知他实例"本实例刚写库并失效了二级缓存 ⇒ 请清掉你自己登记的键"。
    /// </summary>
    /// <remarks>
    /// WHY 载荷不带键清单:本实例登记簿里的键**不是**他实例需要的键——各实例登记的是各自写过的键
    /// (SqlSugar 缓存键=实体×SQL 形态,实例间可不重合)。带清单既帮不上对方,又让载荷随缓存规模膨胀;
    /// 故只广播"失效发生了"这一个信号,由接收侧清**自己的**登记簿(共享 Redis 下这才真正让旧值消失)。
    /// <see cref="SourceInstanceId"/> 仅供日志观测(自回环已由总线按信封来源丢弃,不依赖本字段判定)。
    /// </remarks>
    /// <param name="SourceInstanceId">发起实例标识(观测用)。</param>
    /// <param name="Reason">失效原因标签(当前固定 <c>sqlsugar_data_cache</c>;预留更细粒度)。</param>
    /// <param name="At">发起时刻(UTC)。</param>
    public sealed record SqlSugarCacheInvalidatePayload(string SourceInstanceId, string Reason, DateTimeOffset At);
}