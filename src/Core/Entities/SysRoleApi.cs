using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 角色-接口黑名单(批 A 2.10 细粒度禁用:角色已授权按钮中显式回收的接口权限标识集合)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// 语义:角色经 SysRoleMenu 获得按钮权限后,仍可在本表对特定权限标识"再禁用"——
    /// 有效权限集 = 角色授权按钮权限 − 本表黑名单(扣减在 MenuPermService.GetPermissionsAsync 统一执行,
    /// 缓存落库即有效集)。黑名单变更须失效该角色下全部用户权限缓存(变更即失效,不靠 TTL 兜底)。
    /// WHY 权限标识维度而非路由维度:本框架 [MenuPerm]/SysMenu.Permission 全链路以权限标识为准
    /// (见 SysMenu 注释),黑名单与授权面同维度才能一致比对。
    /// </remarks>
    [SugarTable("sys_role_api", "角色接口黑名单")]
    [IncreTable]
    [SysTable]
    public class SysRoleApi : EntityTenant
    {
        /// <summary>角色 Id(sys_role 主键)。</summary>
        [SugarColumn(ColumnDescription = "角色Id")]
        public long RoleId { get; set; }

        /// <summary>被禁用的接口权限标识(与 [MenuPerm] 值 / SysMenu.Permission 一致,如 sysUser/delete)。</summary>
        [SugarColumn(ColumnDescription = "接口权限标识(黑名单)", Length = 128, IsNullable = true)]
        public string? Permission { get; set; }
    }
}
