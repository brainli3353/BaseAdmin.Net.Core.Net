namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 开放接口改绑入参 {id, bindUserId, bindTenantId}(JSON camelCase)。
    /// AccessKey/AccessSecret 建号后不可改(密钥轮换 = 重置/删除重建),本入参仅可改绑定。
    /// </summary>
    public sealed record UpdateOpenAccessInput(long Id, long BindUserId, long BindTenantId);
}
