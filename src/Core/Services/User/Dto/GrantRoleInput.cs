namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 分配角色请求 {userId, roleIds}(全量替换口径:传入即该用户最终角色集,空/缺省 = 清空角色)。
    /// 变更后服务主动失效该用户权限缓存(变更即失效)。
    /// </summary>
    public sealed record GrantRoleInput(long UserId, List<long>? RoleIds);
}
