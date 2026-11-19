namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 异常日志分页请求 {account?, controllerName?, remoteIp?, startTime?, endTime?, pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), logLevel?(批 UX:按实体列 SysLogEx.LogLevel string 精确匹配)}(JSON camelCase)。
    /// Account 精确匹配、ControllerName 模糊(包含)、RemoteIp 精确、时间范围可选过滤(与 SysLogOp 同口径)。
    /// </summary>
    public sealed record PageSysLogExInput(string? Account, string? ControllerName, string? RemoteIp, DateTime? StartTime, DateTime? EndTime, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, string? LogLevel = null) : AdminCore.Core.Models.IPageInput;
}
