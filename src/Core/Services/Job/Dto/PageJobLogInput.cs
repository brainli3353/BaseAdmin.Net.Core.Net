namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务执行日志分页查询 {pageIndex, pageSize, jobName?, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)}:jobName 精确(任务行「查看记录」弹窗入口);
    /// 过滤参数缺省(null/空)= 不过滤,与 PageSysJobInput 同口径。
    /// </summary>
    public sealed record PageJobLogInput(string? JobName, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null) : AdminCore.Core.Models.IPageInput;
}
