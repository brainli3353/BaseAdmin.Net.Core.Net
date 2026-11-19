using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统菜单/按钮(RBAC 权限资源表:目录/菜单/按钮三级树,Permission 为按钮级接口权限标识)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// Permission 即接口路由权限键:请求 Path 去掉 /api 前缀(如 /api/sysUser/page → sysUser/page),
    /// 与用户按钮权限集(角色→SysRoleMenu→SysMenu Permission)比对决定 403/放行(见 PermissionMiddleware)。
    /// </remarks>
    [SugarTable("sys_menu", "系统菜单")]
    // WHY 索引:高频查询列——SysMenuService.Delete 子节点校验(IsAnyAsync u.Pid==id)＋同父查重(Pid==pid)
    [SugarIndex("i_sys_menu_pid", nameof(Pid), OrderByType.Asc)]
    [IncreTable]
    [SysTable]
    public class SysMenu : EntityTenant
    {
        /// <summary>父节点 Id(0=顶级;树形结构)。</summary>
        [SugarColumn(ColumnDescription = "父节点Id")]
        public long Pid { get; set; } = 0;

        /// <summary>节点名称(标识名,如 sysUser)。</summary>
        [SugarColumn(ColumnDescription = "节点名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>节点标题(展示名,如 用户管理)。</summary>
        [SugarColumn(ColumnDescription = "节点标题", Length = 64)]
        public string? Title { get; set; }

        /// <summary>类型:1 目录,2 菜单,3 按钮(只有按钮参与接口权限校验,目录/菜单仅组织树形)。</summary>
        [SugarColumn(ColumnDescription = "类型(1目录2菜单3按钮)")]
        public int Type { get; set; }

        /// <summary>接口权限标识(按钮级,如 sysUser/page;菜单/目录可为空)。</summary>
        [SugarColumn(ColumnDescription = "接口权限标识", Length = 128, IsNullable = true)]
        public string? Permission { get; set; }

        /// <summary>排序号(同级升序展示)。</summary>
        [SugarColumn(ColumnDescription = "排序号")]
        public int OrderNo { get; set; } = 0;

        /// <summary>图标(前端展示用,本任务只存不用)。</summary>
        [SugarColumn(ColumnDescription = "图标", Length = 64, IsNullable = true)]
        public string? Icon { get; set; }

        /// <summary>状态:0 启用,1 停用(批 CH G28:停用节点及其整棵子树不进登录菜单树;管理端全量树不过滤,可再启用)。
        /// DefaultValue 兜底存量行——sys_menu 已存在,CodeFirst 增量加列时旧行取默认值 0,不报非空约束错。</summary>
        [SugarColumn(ColumnDescription = "状态(0启用1停用)", DefaultValue = "0")]
        public int Status { get; set; } = 0;

        /// <summary>是否缓存:0 不缓存(每次切回页签重新挂载/重取数),1 缓存(keep-alive 保留组件状态)。
        /// DefaultValue 兜底存量行——sys_menu 已存在,CodeFirst 增量加列时旧行取默认值 1(与加列前"一律缓存"行为一致)。</summary>
        [SugarColumn(ColumnDescription = "是否缓存(0否1是)", DefaultValue = "1")]
        public int IsKeepAlive { get; set; } = 1;
    }
}
