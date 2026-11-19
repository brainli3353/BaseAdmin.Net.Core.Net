namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>用户分页请求 {account(模糊,可空), orgId(指定机构含子树,可空), status(状态精确,可空:0 正常/1 停用), pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)}。</summary>
    public sealed record PageUserInput(string? Account, int PageIndex = 1, int PageSize = 10, long? OrgId = null, string? OrderBy = null, bool? Desc = null, int? Status = null) : AdminCore.Core.Models.IPageInput;
}
