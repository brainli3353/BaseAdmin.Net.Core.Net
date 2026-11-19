using System.Collections.Concurrent;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 批 AC 探针集合(两个机制共处一文件;单文件多小类=本仓探针惯例,见 DataScopeProbeEntities/TenantProbeEntities):
/// ① 事件总线 Redis 双通道(81 功能点 5.6):订阅者 + 载荷 + 实例标识 + 就绪握手订阅者;
/// ② ORM 分表(3.1 收尾):贴 [SplitTable]/[SplitField] 的分表探针实体。
/// </summary>
/// <remarks>
/// WHY 探针独立成文件且只在测试装配:两项机制都是「宿主装配期行为」(总线订阅扫描 / CodeFirst 分表建表),
/// 生产代码零改动约束下,取证只能靠测试侧探针(订阅者 + 实体)＋真实 PG/Redis 观测。
/// </remarks>

// ─────────────────────────────────────────────────────────────────────────────
// 机制 A:事件总线 Redis 双通道(批 AC 5.6)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 探针事件载荷:跨实例投递时经 JSON 序列化/反序列化(总线 RedisEnvelope.Payload 承载),
/// 故必须是公共类 + 公共可写属性(与生产事件载荷同口径)。
/// </summary>
public class AcProbePayload
{
    /// <summary>用例唯一标记(核对「B 收到的正是 A 发的那一条」,而非串扰的其它消息)。</summary>
    public string? Tag { get; set; }
}

/// <summary>
/// 实例标识:两个"实例"在地理上同处一个进程(两个 ServiceProvider),而订阅者的计数器是 <c>static</c>(进程级),
/// 光看静态计数无法区分「A 执行了几次 / B 执行了几次」——而本批用例的判定点恰恰是
/// 「A 本地一次 + B 跨实例一次,且 A 不因自回环重复执行」。
/// WHY 用 DI 注入:给每个"实例"的容器注册一个唯一标识,订阅者按标识把执行次数记账到不同桶,
/// 使断言能精确区分实例(否则 static 计数会把两个实例混成一个数,断言退化为同义反复)。
/// </summary>
public sealed class AcProbeInstanceTag
{
    /// <summary>实例标识(如 "A"/"B")。</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// 事件总线订阅探针:两个事件名分别对应「白名单内(应跨实例)」与「白名单外(不应跨实例)」。
/// 计数器为 <c>static</c>(跨用例读回),每用例开头由 <see cref="Reset"/> 归零,避免用例间污染。
/// </summary>
public class AcProbeSubscriber : IEventSubscriber
{
    /// <summary>白名单内事件(ac_probe_cross)的执行总次数(进程级;含本实例本地执行与跨实例投递执行)。</summary>
    public static int CrossCount;

    /// <summary>白名单外事件(ac_probe_local)的执行总次数(进程级)。</summary>
    public static int LocalCount;

    /// <summary>白名单内事件收到的载荷 Tag 序列(核对投递内容,防串扰)。</summary>
    public static readonly ConcurrentQueue<string?> CrossPayloads = new();

    /// <summary>白名单外事件收到的载荷 Tag 序列。</summary>
    public static readonly ConcurrentQueue<string?> LocalPayloads = new();

    /// <summary>白名单内事件「按实例」执行次数(键=实例标识,见 <see cref="AcProbeInstanceTag"/> 的 WHY)。</summary>
    public static readonly ConcurrentDictionary<string, int> CrossByInstance = new(StringComparer.Ordinal);

    /// <summary>白名单外事件「按实例」执行次数。</summary>
    public static readonly ConcurrentDictionary<string, int> LocalByInstance = new(StringComparer.Ordinal);

    private readonly AcProbeInstanceTag _tag;

    /// <summary>构造:注入本实例标识(订阅者由总线路径按具体类型从 DI 作用域解析)。</summary>
    public AcProbeSubscriber(AcProbeInstanceTag tag)
    {
        _tag = tag ?? throw new ArgumentNullException(nameof(tag));
    }

    /// <summary>白名单内事件订阅(EventBus:CrossInstanceEvents 登记项)。</summary>
    [EventSubscribe("ac_probe_cross")]
    public void OnCross(AcProbePayload payload)
    {
        Interlocked.Increment(ref CrossCount);
        CrossByInstance.AddOrUpdate(_tag.Value, 1, (_, v) => v + 1);
        CrossPayloads.Enqueue(payload?.Tag);
    }

    /// <summary>白名单外事件订阅(未登记 → 设计上「不跨实例」,仅本进程内执行)。</summary>
    [EventSubscribe("ac_probe_local")]
    public void OnLocal(AcProbePayload payload)
    {
        Interlocked.Increment(ref LocalCount);
        LocalByInstance.AddOrUpdate(_tag.Value, 1, (_, v) => v + 1);
        LocalPayloads.Enqueue(payload?.Tag);
    }

    /// <summary>读某实例的执行次数(未记账=0)。</summary>
    public static int CountOf(ConcurrentDictionary<string, int> map, string instance)
        => map.TryGetValue(instance, out var count) ? count : 0;

    /// <summary>归零全部静态态(每用例开头调用;进程级静态在用例间共享,不重置会串读)。</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref CrossCount, 0);
        Interlocked.Exchange(ref LocalCount, 0);
        CrossPayloads.Clear();
        LocalPayloads.Clear();
        CrossByInstance.Clear();
        LocalByInstance.Clear();
    }
}

/// <summary>
/// 跨实例通道「就绪握手」探针(与判定计数完全隔离的第三个事件名 <c>ac_probe_warmup</c>)。
/// WHY 必须握手:两个"实例"各持独立 Redis 连接时,SUBSCRIBE 的生效与随后 PUBLISH 的到达之间存在
/// 微小时序窗口——若发布早于订阅生效,Redis Pub/Sub 的该条消息被直接丢弃(无持久化),用例会偶发假红。
/// 握手协议:A 反复发布 warmup 事件,直到「B 记账到 warmup」(B 的 Redis 订阅确实生效)才发正式事件;
/// 因 A 的自回环消息被丢弃,只有经 Redis 投递到 B 才能让 B 记账 → 该信号无法被本地执行伪造(不产生假就绪)。
/// 握手计数不参与任何断言,故重复发布无副作用。
/// </summary>
public class AcProbeWarmupSubscriber : IEventSubscriber
{
    /// <summary>warmup 事件「按实例」接收次数(仅作就绪信号,不作判定)。</summary>
    public static readonly ConcurrentDictionary<string, int> ReceivedByInstance = new(StringComparer.Ordinal);

    private readonly AcProbeInstanceTag _tag;

    /// <summary>构造:注入本实例标识。</summary>
    public AcProbeWarmupSubscriber(AcProbeInstanceTag tag)
    {
        _tag = tag ?? throw new ArgumentNullException(nameof(tag));
    }

    /// <summary>warmup 订阅:仅记账实例标识(证明该实例的 Redis 订阅已生效)。</summary>
    [EventSubscribe("ac_probe_warmup")]
    public void OnWarmup(AcProbePayload payload)
        => ReceivedByInstance.AddOrUpdate(_tag.Value, 1, (_, v) => v + 1);

    /// <summary>该实例是否已记账到 warmup(=其 Redis 订阅已生效)。</summary>
    public static bool ArrivedAt(string instance) => ReceivedByInstance.ContainsKey(instance);

    /// <summary>归零(每用例开头调用)。</summary>
    public static void Reset() => ReceivedByInstance.Clear();
}

// ─────────────────────────────────────────────────────────────────────────────
// 机制 B:ORM 分表(批 AC 3.1 收尾)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 分表探针实体(按 <see cref="SplitType.Month"/> 按月分表)。
/// </summary>
/// <remarks>
/// WHY 表名带时间占位符且必须恰好以 <c>_{year}{month}{day}</c> 结尾(反编译 SqlSugar 5.1.4.217 实测):
/// ① <c>DateSplitTableService.GetAllTables</c> 首行即 <c>CheckTableName</c> —— 表名必须**同时**含
///    {year}/{month}/{day} 三个占位符(缺任一即抛「分表表名需要占位符 …」),占位符相邻位不能是数字;
/// ② <c>UtilMethods.StartCustomSplitTable</c> 只在表名以 <c>_{year}{month}{day}</c> 结尾时才做自定义分表服务清理,
///    即这是 SqlSugar 认的规范模板;
/// ③ 子表名 = 模板把三个占位符按分表粒度替换(Month 粒度下 day 恒 "01",如 ac_split_probe_20260901)。
/// 因此本探针表名不能写成裸 "ac_split_probe"(那样调分表入口会直接抛异常,建不出子表)。
/// WHY [SplitField] 落在自有列 SplitTime:EntityBase 的 CreateTime 是框架审计列(不可改生产代码),
/// 分表字段须由实体显式声明;SplitTime 为空时 SqlSugar 回落 db.GetDate()(即当前月子表)。
/// </remarks>
[SugarTable("ac_split_probe_{year}{month}{day}", "批AC分表探针")]
[SplitTable(SplitType.Month)]
[IncreTable]
public class AcSplitProbeEntity : EntityBase
{
    /// <summary>分表时间字段(SqlSugar 按此值决定写哪张月表;null → 当前月)。</summary>
    [SplitField]
    [SugarColumn(ColumnDescription = "分表时间", IsNullable = true)]
    public DateTime? SplitTime { get; set; }

    /// <summary>探针标记(用例唯一值,便于按名过滤与逐子表取证)。</summary>
    [SugarColumn(ColumnDescription = "名称", Length = 64, IsNullable = true)]
    public string? Name { get; set; }
}