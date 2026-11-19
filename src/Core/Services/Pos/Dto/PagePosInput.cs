namespace AdminCore.Core.Services.Pos.Dto
{
    /// <summary>职位分页请求 {pageIndex, pageSize, name?, code?, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), status?(状态精确,可空:0 正常/1 停用)}。</summary>
    public sealed record PagePosInput(int PageIndex = 1, int PageSize = 10, string? Name = null, string? Code = null, string? OrderBy = null, bool? Desc = null, int? Status = null) : AdminCore.Core.Models.IPageInput;
}
