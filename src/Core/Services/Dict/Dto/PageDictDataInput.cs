namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>字典值分页请求 {pageIndex, pageSize, dictTypeId?, keyword?}(可选按类型过滤;keyword 批 UX:Label/Value 模糊 OR;管理页默认全量按 OrderNo 展示)。</summary>
    public sealed record PageDictDataInput(int PageIndex = 1, int PageSize = 10, long? DictTypeId = null, string? OrderBy = null, bool? Desc = null, string? Keyword = null) : AdminCore.Core.Models.IPageInput;
}
