namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 角色数据范围授权请求 {roleId, orgIds}(批 B 数据权限收口 B2 Define 自定义范围;全量替换口径:
    /// 传入即该角色最终自定义机构集,空/缺省 = 清空全部自定义机构;orgIds 为 sys_org 主键列表;
    /// 越权防护:所选机构须在操作者自身数据范围集内(服务校验);
    /// 变更后服务主动失效该角色下全部用户数据权限缓存——变更即失效,不靠 TTL 兜底)。
    /// </summary>
    public sealed record GrantOrgInput(long RoleId, List<long>? OrgIds);
}
