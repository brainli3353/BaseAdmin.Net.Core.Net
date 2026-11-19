using AdminCore.Core.Entities;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 用户-角色关联种子(契约 §8):admin 用户关联系统管理员角色。
    /// </summary>
    /// <remarks>
    /// WHY 用 HasData(ISqlSugarClient) 重载(而非契约无参 HasData()):admin 用户主键是存量雪花 Id
    /// (M1 SysUserSeed 按账号查重插入,Id 不固定),只能按账号查库解析——无参数据源做不到。
    /// 关联行主键确定性(Id=30000 段,与 SysRoleMenuSeedData 的 30000+ 索引段不同段:
    /// 用户-角色关联单独占 40000 段,防与角色-菜单关联主键撞车)。
    /// 幂等:按主键缺行插入、已有跳过(2026-10-30 起不覆写);admin 不存在(首启时序:本种子 Order=4,
    /// 存量 SysUserSeed 在命令式种子阶段先执行)时返回空集,跳过。
    /// </remarks>
    [IncreSeed]
    [SeedData(4)] // 顺序 4:最后执行(admin 用户由存量 SysUserSeed 先行创建;角色由顺序 2 种子创建)
    public class SysUserRoleSeedData : ISqlSugarEntitySeedData<SysUserRole>
    {
        /// <summary>契约无参签名占位:数据源需库内状态,见 HasData(ISqlSugarClient) 重载。</summary>
        public IEnumerable<SysUserRole> HasData()
        {
            return Array.Empty<SysUserRole>();
        }

        public IEnumerable<SysUserRole> HasData(ISqlSugarClient db)
        {
            // 按账号解析存量 admin(软删过滤器自动生效:已删 admin 不补关联)
            var admin = db.Queryable<SysUser>().First(u => u.Account == "admin");
            if (admin is null)
            {
                return Array.Empty<SysUserRole>();
            }

            var adminRole = new SysRoleSeedData().HasData().Single(r => r.Code == "sys_admin");

            return new[]
            {
                new SysUserRole
                {
                    Id = 40000,
                    UserId = admin.Id,
                    RoleId = adminRole.Id,
                    TenantId = AdminCore.Core.SqlSugarConst.DefaultTenantId
                }
            };
        }
    }
}
