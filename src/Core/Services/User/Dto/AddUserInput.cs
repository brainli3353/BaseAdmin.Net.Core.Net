namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 新建用户请求 {account, password, realName, status, roleIds, orgId, posId}(JSON camelCase)。
    /// RoleIds 可选(契约 §7"默认角色"口径:建用户时可一并指定角色,缺省即无角色——系统管理员是内置角色,不开放给普通建号)。
    /// OrgId/PosId 可选(批 B 数据权限收口 B1:建号时一并指定机构/职位归属,缺省即无归属;提供时服务校验存在性防悬挂引用)。
    /// </summary>
    public sealed record AddUserInput(string? Account, string? Password, string? RealName, int? Status, List<long>? RoleIds, long? OrgId, long? PosId);
}
