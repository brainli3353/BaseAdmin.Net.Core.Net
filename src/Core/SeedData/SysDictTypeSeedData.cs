using AdminCore.Core.Entities;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 字典类型种子(契约 §8 附带):示例字典「账号状态」(Code=account_status,Id=50000)。
    /// 状态码对齐 intel 账号状态机契约(task_009:0 待验号 1 观察 2 养号中 3 停用 4 冻结)。
    /// Id 分段 50000+(与用户-角色关联 40000+ 隔离,防跨实体主键漂移)。
    /// </summary>
    [IncreSeed]
    [SeedData(5)] // 顺序 5:在 RBAC 种子(1-4)之后;被 SysDictDataSeedData(顺序 6)引用,必须先执行
    public class SysDictTypeSeedData : ISqlSugarEntitySeedData<SysDictType>
    {
        public IEnumerable<SysDictType> HasData()
        {
            return new[]
            {
                new SysDictType
                {
                    Id = 50000,
                    Name = "账号状态",
                    Code = "account_status",
                    IsEnum = true,
                    IsEnable = true,
                    Remark = "intel 账号状态机字典(task_009 契约:0 待验号 1 观察 2 养号中 3 停用 4 冻结)",
                    TenantId = SqlSugarConst.DefaultTenantId
                }
            };
        }
    }
}
