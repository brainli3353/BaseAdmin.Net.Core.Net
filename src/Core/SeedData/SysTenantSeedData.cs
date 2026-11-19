using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 系统租户种子(批 C 多租户业务模块 C1):默认租户(Id=SqlSugarConst.DefaultTenantId,启用、永久有效)。
    /// 为何必须种:登录校验(1.4)与 JwtAuthHandler 回查均要求用户租户行存在——存量用户(admin 等)
    /// TenantId 已自愈为默认租户值(契约 §5.3),若无数值对应行会被判"租户不存在"而全量拒登。
    /// 幂等:按显式主键缺行插入、已有跳过(2026-10-30 起,不再 upsert 覆写);重启命中同一行即跳过。
    /// </summary>
    [IncreSeed]
    [SeedData(8)] // 顺序 8:独立种子(不被/不引用其他种子),取字典/职位种子(顺序 1-7)之后的空闲位
    public class SysTenantSeedData : ISqlSugarEntitySeedData<SysTenant>
    {
        public IEnumerable<SysTenant> HasData()
        {
            return new[]
            {
                new SysTenant
                {
                    Id = AdminCore.Core.SqlSugarConst.DefaultTenantId,
                    Name = "默认租户",
                    Plan = "标准版",
                    Status = 0,
                    ExpirationTime = null, // 永久有效
                    OrderNo = 1,
                    Remark = "平台默认租户:存量数据天然归属(契约 §5.3),不可停用/删除(服务层保护)"
                }
            };
        }
    }
}
