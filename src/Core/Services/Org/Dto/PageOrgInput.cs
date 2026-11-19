namespace AdminCore.Core.Services.Org.Dto
{
    /// <summary>
    /// 机构分页查询 {pageIndex, pageSize, name?, code?, status?}:名称/编码支持模糊(契约 Page:Name/Code 模糊)。
    /// 过滤参数缺省(null/空)= 不过滤,与 PageUserInput 账号模糊同口径;status 精确(0 启用/1 禁用,可空)。
    /// </summary>
    public sealed record PageOrgInput(string? Name, string? Code, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, int? Status = null) : AdminCore.Core.Models.IPageInput;
}
