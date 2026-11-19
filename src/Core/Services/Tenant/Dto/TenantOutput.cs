namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 租户分页行输出 {id, name, plan, status, expirationTime, orderNo, remark, createTime, dbName, hasConnection}
    /// (敏感字段如连接串永不外泄——批 AB 起 SysTenant 承载 Connection,但输出只回 hasConnection 布尔与库名)。
    /// hasConnection=false → 该租户数据落主库;true → 落独立库(ConfigId=租户 Id)。
    /// </summary>
    public sealed record TenantOutput(long Id, string? Name, string? Plan, int Status, DateTime? ExpirationTime, int OrderNo, string? Remark, DateTime? CreateTime,
        string? DbName = null, bool HasConnection = false);
}
