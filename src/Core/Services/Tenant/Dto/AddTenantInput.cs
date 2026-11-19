namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 新建租户请求 {name, plan, expirationTime, orderNo, remark, connection?, dbName?}(JSON camelCase)。
    /// name 必填;plan/expirationTime 可选(expirationTime null=永久有效);租户 Id 雪花自动生成(= TenantId 标识),不由调用方指定。
    /// connection/dbName(批 AB 库级隔离):连接串留空=该租户落主库;非空=数据落独立库(ConfigId=租户 Id),
    /// 加密开关(DbSettings:EnableConnEncrypt)开启时须提交密文。
    /// </summary>
    public sealed record AddTenantInput(string? Name, string? Plan, DateTime? ExpirationTime, int? OrderNo, string? Remark,
        string? Connection = null, string? DbName = null);
}
