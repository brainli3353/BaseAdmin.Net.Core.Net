using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 角色-机构授权(批 B 数据权限收口 B2:DataScope=Define(5) 自定义范围)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段(口径同 SysRoleApi)。
    /// </summary>
    /// <remarks>
    /// 语义:DataScope=5(自定义)角色的可见机构集 = 本表该角色 OrgId 集(多角色并集,
    /// 收敛与缓存见 SysOrgService.GetUserOrgScopeAsync 的 Define 分支);
    /// 全量替换口径变更(SysRoleService.GrantOrgAsync 先清后挂),变更即失效该角色下全部用户数据权限缓存(不靠 TTL 兜底)。
    /// 越权防护(批 B B2):授权端点须校验所选机构在操作者自身数据范围集内(见 SysRoleService.GrantOrgAsync)。
    /// </remarks>
    [SugarTable("sys_role_org", "角色机构授权")]
    [IncreTable]
    [SysTable]
    public class SysRoleOrg : EntityTenant
    {
        /// <summary>角色 Id(sys_role 主键)。</summary>
        [SugarColumn(ColumnDescription = "角色Id")]
        public long RoleId { get; set; }

        /// <summary>授权的机构 Id(sys_org 主键)。</summary>
        [SugarColumn(ColumnDescription = "机构Id")]
        public long OrgId { get; set; }
    }
}
