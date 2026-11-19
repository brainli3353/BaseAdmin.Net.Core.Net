namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 操作日志分页请求 {account?, controllerName?, success?, startTime?, endTime?, pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)}(JSON camelCase)。
    /// Account 精确匹配、ControllerName 模糊(包含)、Success/时间范围可选过滤。
    /// </summary>
    public sealed record PageSysLogOpInput(string? Account, string? ControllerName, bool? Success, DateTime? StartTime, DateTime? EndTime, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null) : AdminCore.Core.Models.IPageInput;
}
