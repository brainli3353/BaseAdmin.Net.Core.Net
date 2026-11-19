namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 更新租户请求 {id, name, plan, expirationTime, orderNo, remark, connection?, dbName?}(JSON camelCase;部分更新口径:null=不改该字段,
    /// 与 UpdateUserInput/OrgId 同口径)。status 不入本请求——启停走 setStatus 端点(统一缓存失效挂钩)。
    /// connection(批 AB 库级隔离):null=不改;非空=替换连接串并即时失效已注册的租户库连接(下次访问按新串重连)。
    /// 另注:不支持"清空连接串退回主库"(部分更新口径不承载"置空"语义,与 expirationTime 同)——
    /// 如需退回主库请删除租户后重建(批 AB 裁决留痕)。
    /// </summary>
    public sealed record UpdateTenantInput(long Id, string? Name, string? Plan, DateTime? ExpirationTime, int? OrderNo, string? Remark,
        string? Connection = null, string? DbName = null);
}
