namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 角色接口黑名单授权请求 {roleId, apiList}(批 A 2.10;全量替换口径:传入即该角色最终禁用集,
    /// 空/缺省 = 解除全部禁用;apiList 为权限标识列表,与 [MenuPerm] 值 / SysMenu.Permission 一致;
    /// 变更后服务主动失效该角色下全部用户的权限缓存——变更即失效,不靠 TTL 兜底)。
    /// </summary>
    public sealed record GrantApiInput(long RoleId, List<string>? ApiList);
}
