using AdminCore.Core.Entities;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.SeedData;
using AdminCore.Core.Security;
using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// SysUser 种子:首启插入初始管理员(admin/Admin@123,种子时 Pbkdf2Helper.HashPassword 现算哈希,明文不进库),
    /// 并自愈存量 admin 的超管口径(M1 种子未设 AccountType → 存量行落默认 777,M2 契约要求超管 999)
    /// 与租户口径(契约 §5.3 存量数据天然带默认租户值:M1 存量行 TenantId 为 NULL)。
    /// </summary>
    /// <remarks>
    /// 幂等:按账号查重,已存在则跳过(每次启动都重跑,不能重复插)。
    /// 已知边界:查重走全局软删过滤器——若 admin 曾被软删,重跑会再插一行。
    /// M1 无用户管理入口(软删路径未暴露)故无实际影响;用户管理任务落地时应补账号唯一约束根治。
    /// </remarks>
    [IncreSeed]
    public class SysUserSeed : ISqlSugarSeed
    {
        public async Task SeedAsync(ISqlSugarClient db)
        {
            var admin = await db.Queryable<SysUser>().FirstAsync(u => u.Account == "admin");
            if (admin is not null)
            {
                // 存量自愈(M1→M2 语义迁移):M1 种子插入 admin 时未显式设 AccountType,
                // 落库为默认值 777(普通账号);M2 权限体系契约 §2 超管穿透以 999 为凭证,
                // 验收"admin(超管)全放行"要求存量 admin 必须是 999。
                // 只升不降:已为 999 的行不动;999→777 的降级不在此处理(那是业务操作,走用户管理)。
                if (admin.AccountType != 999)
                {
                    await db.Updateable(new SysUser
                    {
                        Id = admin.Id,
                        AccountType = 999
                    })
                    .UpdateColumns(u => u.AccountType)
                    .ExecuteCommandAsync();
                }

                // 存量自愈(契约 §5.3 租户基座:"存量数据天然带默认租户值"):
                // M1 种子插入 admin 时 TenantId 列尚不存在(租户列为本任务增量加列),存量行为 NULL。
                // 只补空不覆盖:已显式带租户值的行不动;NULL/0 → 默认租户,使存量行在租户行级过滤下可见。
                // WHY 用 Ado 原生 SQL 而非 Updateable.UpdateColumns:TenantId 列贴 IsOnlyIgnoreUpdate
                // (业务更新永不改租户),该规则连显式 UpdateColumns 都跳过(实机验证:同段 AccountType 自愈
                // 生效而 TenantId 未生效)。一次性数据迁移必须绕过此规则,故走原生 SQL(参数化防注入)。
                if (admin.TenantId is null)
                {
                    // WHY SugarParameter 显式参数:Ado 的匿名对象参数重载在本版本不做 {0} 占位替换(实机 42601),
                    // @name + SugarParameter[] 为 Ado 原生参数化口径
                    // WHY 列名大小写混用(task_008 fixa):库列名约定=主键小写 id(裸写折小写恰好命中)、
                    // 其余列 PascalCase(必须引号精确引用,裸写会折小写报 column does not exist)
                    await db.Ado.ExecuteCommandAsync(
                        "UPDATE sys_user SET \"TenantId\" = @tid WHERE id = @uid AND (\"TenantId\" IS NULL OR \"TenantId\" = 0)",
                        new SugarParameter("@tid", SqlSugarConst.DefaultTenantId),
                        new SugarParameter("@uid", admin.Id));
                }

                return;
            }

            // 经 db.Insertable 插入 → AOP DataExecuting 自动填雪花主键/CreateTime(种子不手写主键)
            await db.Insertable(new SysUser
            {
                Account = "admin",
                Password = Pbkdf2Helper.HashPassword("Admin@123"),
                RealName = "系统管理员",
                // 显式超管口径:不设则落实体默认 777,权限中间件将按普通账号拦截全部接口(契约 §2 超管穿透失效)
                AccountType = 999,
                Status = 0
            }).ExecuteCommandAsync();
        }
    }
}
