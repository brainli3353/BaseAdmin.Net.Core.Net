using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 角色-菜单(按钮)关联(RBAC 多对多中间表:一个角色可授权多个按钮/菜单节点)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// 角色授权变更(GrantMenu)后须主动失效该角色下全部用户的按钮权限缓存(变更即失效,见 SysRoleService);
    /// 唯一性(RoleId+MenuId 不重复)由服务层校验。
    /// </remarks>
    [SugarTable("sys_role_menu", "角色菜单关联")]
    // WHY 索引:高频查询列——MenuPermService/SysMenuService 权限链路按角色查菜单 Id＋SysRoleService.Delete 清理
    [SugarIndex("i_sys_role_menu_roleid", nameof(RoleId), OrderByType.Asc)]
    // WHY 索引:高频查询列——SysMenuService.Delete 按 MenuId 清理授权＋MenuPermService 反查引用角色
    [SugarIndex("i_sys_role_menu_menuid", nameof(MenuId), OrderByType.Asc)]
    [IncreTable]
    [SysTable]
    public class SysRoleMenu : EntityTenant
    {
        /// <summary>角色 Id(sys_role 主键)。</summary>
        [SugarColumn(ColumnDescription = "角色Id")]
        public long RoleId { get; set; }

        /// <summary>菜单/按钮 Id(sys_menu 主键)。</summary>
        [SugarColumn(ColumnDescription = "菜单Id")]
        public long MenuId { get; set; }
    }
}
