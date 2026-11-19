using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 系统角色种子(契约 §8):系统管理员(Code=sys_admin,Id=20000,SysFlag 内置标记)。
    /// 内置角色禁删(服务层保护);Id 分段 20000+(与菜单 10000+ 隔离,防跨实体主键漂移)。
    /// </summary>
    [IncreSeed]
    [SeedData(2)] // 顺序 2:被 SysRoleMenuSeedData/SysUserRoleSeedData(顺序 3/4)引用,必须先执行
    public class SysRoleSeedData : ISqlSugarEntitySeedData<SysRole>
    {
        public IEnumerable<SysRole> HasData()
        {
            return new[]
            {
                new SysRole
                {
                    Id = 20000,
                    Name = "系统管理员",
                    Code = "sys_admin",
                    Remark = "内置角色:全部菜单权限,账号删除/权限变更受服务层保护",
                    SysFlag = true,
                    TenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId
                }
            };
        }
    }
}
