namespace AdminCore.Core.Services.Tenant.Dto
{
    /// <summary>
    /// 租户库初始化结果(批 AB C4):{tenantId, configId, tableCount, seedCount, database}。
    /// tableCount = 本次处理的 [Tenant] 实体表数量(0 = 该租户库无租户级实体,属正常);
    /// seedCount = 本次执行的租户级种子类数量;database = 目标库名(连接串解析所得,解析不出为 null)。
    /// 幂等口径:可重复调用——建表为 CodeFirst 增量(只补新列),种子为主键缺行插入、已有跳过(2026-10-30 起)。
    /// </summary>
    public sealed record InitTenantDbOutput(long TenantId, string ConfigId, int TableCount, int SeedCount, string? Database);
}