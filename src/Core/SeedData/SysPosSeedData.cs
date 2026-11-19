using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 职位初始种子(task_020 契约 §4):首启插入 管理员 职位(Code=admin,Id=70000)。
    /// Id 段 70000+(与菜单 10000+/角色 20000+/授权 30000+/用户关联 40000+/字典类型 50000/字典值 60000+ 隔离,防跨实体主键漂移)。
    /// </summary>
    /// <remarks>幂等:[IncreSeed]+[SeedData] 主键缺行插入、已有跳过(2026-10-30 起不再覆写),重启重跑不重复;显式 Id 保证跨重启稳定。</remarks>
    [IncreSeed]
    [SeedData(7)] // 顺序 7:独立种子,不被/不引用其他种子,取 RBAC+字典种子(顺序 1-6)之后的空闲位
    public class SysPosSeedData : ISqlSugarEntitySeedData<SysPos>
    {
        public IEnumerable<SysPos> HasData()
        {
            return new[]
            {
                new SysPos
                {
                    Id = 70000,
                    TenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId,
                    Name = "管理员",
                    Code = "admin",
                    Sort = 1,
                    Status = 0
                }
            };
        }
    }
}
