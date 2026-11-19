namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 租户启停请求 {id, status}(JSON camelCase;status 0=启用 1=停用,与 SysUser.Status 同口径)。
    /// 停用时服务主动失效该租户下全部用户的 token 版本缓存(变更即失效,不靠 TTL 兜底——
    /// JwtAuthHandler 缓存缺失回查租户态即拒绝)。
    /// </summary>
    public sealed record SetTenantStatusInput(long Id, int Status);
}
