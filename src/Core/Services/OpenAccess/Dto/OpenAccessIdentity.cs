namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 开放接口身份快照(签名/ApiKey 两 Handler 共用,服务层装配):
    /// 身份行字段(AccessKey/AccessSecret/绑定)来自 sys_open_access 缓存;
    /// 绑定用户字段(Account/RealName/AccountType/TokenVersion/OrgId)每次实时查用户行——
    /// 用户停用/改密(TokenVersion+1)对开放接口即时生效,不依赖跨服务缓存失效协调。
    /// BindUserValid=false(用户不存在/已停用)时 Handler 一律拒绝。
    /// </summary>
    public sealed record OpenAccessIdentity(
        string AccessKey, string AccessSecret, long BindUserId, long BindTenantId,
        string? Account, string? RealName, int AccountType, int TokenVersion, long? OrgId, bool BindUserValid);
}
