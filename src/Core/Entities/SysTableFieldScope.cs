using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 字段权限「表级开关」(批 AK/G13 表级开关)：显式声明哪些表进入字段权限管理。
    /// WHY 存在：批 AH 的受管判定是**隐式推导**——任一角色对某表有授权行 ⇒ 该表对**所有**非超管用户
    /// 进入 fail-closed 裁剪（其他角色/其他租户未授权列被置空），配置一行影响全局且无提示（爆炸半径）。
    /// 本批把"受管"改为**显式声明**：表在此 Enabled=true 才受管；未声明表零影响（全列放行）。
    /// 授权（GrantAsync）时对涉及的表自动置 Enabled=true（"配置即受管"，行为直觉）；可再手动关闭。
    /// 权威读口 = <see cref="Services.RoleTable.SysRoleTableService.GetManagedTablesAsync"/>（缓存键 sys_role_table:__managed）。
    /// </summary>
    [SugarTable("sys_table_field_scope")]
    [IncreTable]
    public class SysTableFieldScope : EntityBase
    {
        [SugarColumn(ColumnDescription = "表名(与授权树/裁剪同口径,如 sys_user)", Length = 128)]
        public string TableName { get; set; } = string.Empty;

        [SugarColumn(ColumnDescription = "是否受管(受管表对非超管用户按角色列白名单 fail-closed 裁剪)")]
        public bool Enabled { get; set; } = true;

        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }
    }
}
