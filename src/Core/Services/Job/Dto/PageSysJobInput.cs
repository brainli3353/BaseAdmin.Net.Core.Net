namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务分页查询 {pageIndex, pageSize, name?, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), enabled?(启用状态精确,可空)}:名称模糊(契约 Page:Name 模糊);
    /// 过滤参数缺省(null/空)= 不过滤,与 PageOrgInput 同口径。
    /// </summary>
    public sealed record PageSysJobInput(string? Name, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, bool? Enabled = null) : AdminCore.Core.Models.IPageInput;
}
