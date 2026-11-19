using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 角色-菜单授权种子(契约 §8):系统管理员 = 全部菜单(目录/菜单/按钮全量授权)。
    /// 跨种子引用(契约指定写法):new SysRoleSeedData().HasData() 取角色 Id、
    /// new SysMenuSeedData().HasData() 取全部菜单 Id——种子间外键一致性(对齐 baseadmin)。
    /// </summary>
    /// <remarks>
    /// 关联行 Id 分段 30000+:授权行主键确定性生成(30000 + 菜单按 Id 升序的序号),
    /// 菜单集合固定时序号恒定,按主键缺行插入、已有跳过,幂等(2026-10-30 起不覆写既有授权行)。
    /// </remarks>
    [IncreSeed]
    [SeedData(3)] // 顺序 3:依赖菜单种子(1)与角色种子(2)的 Id 定义
    public class SysRoleMenuSeedData : ISqlSugarEntitySeedData<SysRoleMenu>
    {
        public IEnumerable<SysRoleMenu> HasData()
        {
            var adminRole = new SysRoleSeedData().HasData()
                .Single(r => r.Code == "sys_admin");

            var menus = new SysMenuSeedData().HasData()
                .OrderBy(m => m.Id)
                .ToList();

            // 关联行 Id = 30000 + 序号(菜单按 Id 升序):确定性映射,重启按主键缺行插入、已有跳过(幂等)
            return menus.Select((m, index) => new SysRoleMenu
            {
                Id = 30000 + index,
                RoleId = adminRole.Id,
                MenuId = m.Id,
                TenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId
            });
        }
    }
}
