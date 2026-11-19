namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// Http 出站日志分页请求 {account?, url?, httpMethod?, statusCode?, startTime?, endTime?, pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)}(JSON camelCase)。
    /// Account 精确匹配、Url 模糊(包含)、HttpMethod 精确、StatusCode 精确、时间范围按创建时间闭区间(与 SysLogOp/SysLogEx 同口径)。
    /// </summary>
    public sealed record PageSysLogHttpInput(string? Account, string? Url, string? HttpMethod, int? StatusCode,
        DateTime? StartTime, DateTime? EndTime, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null) : AdminCore.Core.Models.IPageInput;
}