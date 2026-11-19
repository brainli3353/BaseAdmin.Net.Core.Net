using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities;

/// <summary>
/// 系统差异日志表(批 X 5.4,参照 Admin.NET SysLogDiff 移植):
/// 每次写操作(增/删/改)的操作前/后整行 JSON 快照＋SQL/参数/耗时,经 SqlSugar
/// <c>Aop.OnDiffLogEvent</c> 自动落库(开关 <c>DbSettings:EnableDiffLog</c>,缺省关闭)。
/// <c>[LogTable]</c> 路由日志库(未配置回落主库);Page 不载大字段、GetDetail 全量。
/// </summary>
[SugarTable("sys_log_diff", "系统差异日志表")]
// WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋差异日志分页时间过滤排序
[SugarIndex("i_sys_log_diff_createtime", nameof(CreateTime), OrderByType.Desc)]
[IncreTable]
[LogTable]
public class SysLogDiff : EntityTenant
{
    /// <summary>操作前记录(字段描述/列名/值/表名/表描述 JSON;大字段)。</summary>
    [SugarColumn(ColumnDescription = "操作前记录", ColumnDataType = "text", IsNullable = true)]
    public string? BeforeData { get; set; }

    /// <summary>操作后记录(同上;大字段)。</summary>
    [SugarColumn(ColumnDescription = "操作后记录", ColumnDataType = "text", IsNullable = true)]
    public string? AfterData { get; set; }

    /// <summary>SQL 原文(大字段)。</summary>
    [SugarColumn(ColumnDescription = "Sql", ColumnDataType = "text", IsNullable = true)]
    public string? Sql { get; set; }

    /// <summary>参数(参数名/值/类型 JSON;大字段)。</summary>
    [SugarColumn(ColumnDescription = "参数", ColumnDataType = "text", IsNullable = true)]
    public string? Parameters { get; set; }

    /// <summary>业务对象(Insertable(obj, businessData) 手动传入;未传 = 表名;大字段)。</summary>
    [SugarColumn(ColumnDescription = "业务对象", ColumnDataType = "text", IsNullable = true)]
    public string? BusinessData { get; set; }

    /// <summary>操作类型(INSERT/UPDATE/DELETE)。</summary>
    [SugarColumn(ColumnDescription = "操作类型", Length = 16, IsNullable = true)]
    public string? DiffType { get; set; }

    /// <summary>耗时(毫秒)。</summary>
    [SugarColumn(ColumnDescription = "耗时", IsNullable = true)]
    public long? Elapsed { get; set; }
}
