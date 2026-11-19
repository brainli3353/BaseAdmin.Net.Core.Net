namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>字典类型分页请求 {pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), keyword(批 UX:Name/Code 模糊 OR)}。</summary>
    public sealed record PageDictTypeInput(int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, string? Keyword = null) : AdminCore.Core.Models.IPageInput;
}
