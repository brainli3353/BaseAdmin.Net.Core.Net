using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Log.Dto;

/// <summary>
/// 差异日志列表项(批 X 5.4):投影排除 BeforeData/AfterData 两大字段(参照 Page 口径——
/// 大字段仅详情加载;参照 IgnoreColumns 同义,本仓 record 投影更彻底)。
/// </summary>
public sealed record PageLogDiffOutput(
    long Id,
    string? BusinessData,
    string? DiffType,
    [property: JsonConverter(typeof(NullableLongNumberJsonConverter))] long? Elapsed,
    string? CreateUserName,
    DateTime? CreateTime,
    long? TenantId);

/// <summary>差异日志详情(全字段,含 BeforeData/AfterData 大字段)。</summary>
public sealed record SysLogDiffDetailOutput(
    long Id,
    string? BeforeData,
    string? AfterData,
    string? Sql,
    string? Parameters,
    string? BusinessData,
    string? DiffType,
    [property: JsonConverter(typeof(NullableLongNumberJsonConverter))] long? Elapsed,
    string? CreateUserName,
    DateTime? CreateTime,
    long? TenantId);
