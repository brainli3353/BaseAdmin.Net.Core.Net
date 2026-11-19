namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 租户分页请求 {name, pageIndex, pageSize, status?, startDate?, endDate?}(JSON camelCase)。name 可选,提供时模糊(包含)搜索;
    /// status 批 UX:实读实体列 SysTenant.Status(int,0=启用 1=停用),可空参数精确匹配。
    /// 批 UX B1d 时间范围:按 CreateTime 过滤(入库时刻即用户语义"创建时间";与同批 sysFile 口径一致)。
    /// </summary>
    public sealed record PageTenantInput(string? Name, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, int? Status = null, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput;
}
