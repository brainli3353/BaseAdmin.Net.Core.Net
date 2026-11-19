namespace AdminCore.Core.Services.Print.Dto
{
    /// <summary>打印模板分页请求 {name?, pageIndex, pageSize, orderBy/desc, startDate?, endDate?}(JSON camelCase)。Name 模糊(包含)过滤。
    /// 批 UX B1d 时间范围:按 CreateTime 过滤(模板创建时刻,列表已有 CreateTime 输出列)。</summary>
    public sealed record PageSysPrintInput(string? Name, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput;
}
