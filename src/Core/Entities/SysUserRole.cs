using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 用户-角色关联(RBAC 多对多中间表:一个用户可挂多个角色)。继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// 用户按钮权限集解析链路:SysUserRole → SysRoleMenu → SysMenu(Permission),见 PermissionMiddleware 权限链路;
    /// 唯一性(UserId+RoleId 不重复)由服务层校验。
    /// </remarks>
    [SugarTable("sys_user_role", "用户角色关联")]
    // WHY 索引:高频查询列——MenuPermService/SysMenuService 权限链路按用户查角色(用户→SysUserRole→SysRoleMenu→SysMenu)
    [SugarIndex("i_sys_user_role_userid", nameof(UserId), OrderByType.Asc)]
    // WHY 索引:高频查询列——SysRoleService 按 RoleId 查授权用户/删角色清理关联(Where u.RoleId==id)
    [SugarIndex("i_sys_user_role_roleid", nameof(RoleId), OrderByType.Asc)]
    [IncreTable]
    [SysTable]
    public class SysUserRole : EntityTenant
    {
        /// <summary>用户 Id(sys_user 主键)。</summary>
        [SugarColumn(ColumnDescription = "用户Id")]
        public long UserId { get; set; }

        /// <summary>角色 Id(sys_role 主键)。</summary>
        [SugarColumn(ColumnDescription = "角色Id")]
        public long RoleId { get; set; }
    }
}
