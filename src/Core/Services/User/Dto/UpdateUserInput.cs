namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 更新用户请求 {id, account, realName, status, orgId, posId}(JSON camelCase)。
    /// 口径:账号唯一性由服务校验;超管账号禁改(契约 §7);密码修改走后续独立端点(迭代项),本端点不接收密码。
    /// OrgId/PosId 部分更新(批 B 数据权限收口 B1):null=不改,提供时服务校验存在性;OrgId 变更即时失效该用户数据权限缓存。
    /// </summary>
    public sealed record UpdateUserInput(long Id, string? Account, string? RealName, int? Status, long? OrgId, long? PosId);
}
