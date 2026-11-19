namespace AdminCore.Core.Services.Config.Dto
{
    /// <summary>
    /// 配置分页请求 {name?, code?, groupCode?, pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验)}(JSON camelCase)。
    /// 三个过滤项均可选,提供时模糊(包含)搜索。
    /// </summary>
    public sealed record PageConfigInput(string? Name, string? Code, string? GroupCode, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null) : AdminCore.Core.Models.IPageInput;
}
