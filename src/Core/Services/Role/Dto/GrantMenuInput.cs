namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 角色授权请求 {roleId, menuIds}(全量替换口径:传入即该角色最终授权集,空/缺省 = 清空授权;
    /// 变更后服务主动失效该角色下全部用户的权限缓存——契约 §2 变更即失效,不靠 7 天过期兜底)。
    /// </summary>
    public sealed record GrantMenuInput(long RoleId, List<long>? MenuIds);
}
