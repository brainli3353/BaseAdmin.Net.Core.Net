using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统角色(RBAC 角色表:Name/Code/Remark/内置标记 SysFlag)。继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// SysFlag=true 标记内置角色(如系统管理员):内置角色禁止删除、禁止修改角色权限集(契约 §7);
    /// Code 唯一性由服务层校验(建库不配唯一约束,与项目实体口径一致)。
    /// </remarks>
    [SugarTable("sys_role", "系统角色")]
    [IncreTable]
    [SysTable]
    public class SysRole : EntityTenant
    {
        /// <summary>角色名称(展示名;ORM 读回无空值保证,按项目实体惯例用可空引用类型)。</summary>
        [SugarColumn(ColumnDescription = "角色名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>角色编码(唯一,业务标识;服务层校验唯一性)。</summary>
        [SugarColumn(ColumnDescription = "角色编码", Length = 64)]
        public string? Code { get; set; }

        /// <summary>备注(用途说明;可选,可空——与 SysOrg/SysPos/SysTenant 同口径)。</summary>
        /// <remarks>WHY IsNullable=true:SqlSugar 对 string 引用类型默认 NOT NULL,漏标则 CodeFirst 建表/同步漂移为
        /// NOT NULL,而契约 remark 可选(null 合法)→ insert null 23502(批 V 部署全量验证抓出)。</remarks>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>内置标记:true=内置角色(禁止删除、禁止修改权限集);默认 false 普通角色。</summary>
        [SugarColumn(ColumnDescription = "内置标记")]
        public bool SysFlag { get; set; } = false;

        /// <summary>
        /// 数据范围(task_013 数据权限,DataScopeEnum 数值存储):1 全部 / 2 本部门及以下 / 3 本部门 / 4 仅本人 / 5 自定义。
        /// 默认 4 仅本人(存量角色增量列 DefaultValue=4——最小权限起步);多角色按枚举值最小收敛(范围最大)。
        /// 变更经 SysRoleService.UpdateAsync(DataScope 入参非 null 且变更 → 该角色全部用户数据权限缓存即时失效)。
        /// </summary>
        [SugarColumn(ColumnDescription = "数据范围", DefaultValue = "4")]
        public long DataScope { get; set; } = 4;
    }
}
