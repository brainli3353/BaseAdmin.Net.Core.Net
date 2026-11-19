namespace AdminCore.Core.Services.Log.Dto;

/// <summary>
/// 差异日志分页入参(批 X 5.4):CreateTime 闭区间过滤,Id 倒序(参照 PageLogInput 口径);orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)。
/// </summary>
public sealed record PageLogDiffInput(DateTime? StartTime, DateTime? EndTime, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null) : AdminCore.Core.Models.IPageInput;
