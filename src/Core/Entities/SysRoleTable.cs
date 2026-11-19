using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 角色字段权限(批 AH 2.9 表级字段权限):"角色 × (表, 列)"白名单——授权行存在即表示该角色**可见**该列。
    /// </summary>
    /// <remarks>
    /// 与参照仓差异（本批裁决）：
    /// ① 参照 `SysRoleTable` 只到"表+列"字符串、**后端无任何强制点**（全仓无查询侧消费、前端 `setUserTables()` 零调用点＝端到端死代码）
    ///    ⇒ 本仓补齐强制点：`ColumnScopeFilter`（响应出口按白名单裁剪，见 Web.Core/Filters）；
    /// ② 参照把 Id 设为雪花但表**无唯一索引**（并发可插入重复授权行）⇒ 本仓加 (RoleId,TableName,ColumnName) 唯一索引；
    /// ③ 语义边界（必须知悉）：本仓强制点在**响应出口**（DTO 字段按白名单置空），不是 SQL 层列裁剪——
    ///    敏感列仍在服务端内存中、只是不外出；若将来需要"库层就不取该列"，属另一个课题（登记，不在本批）。
    /// 继承 EntityBase（非 EntityTenant）:与 SysRoleMenu/SysRoleApi/SysRoleOrg 同口径——角色授权是平台级配置,
    /// 其行级隔离由"角色本身属租户"承担（角色表已受租户过滤），授权行再加一层过滤会让超管跨租户配置失效。
    /// ⇒ **t846 批 1 复评（2026-10-26）后维持不纳管**：除上述契约外另有一条硬理由——本表唯一索引 i_sys_role_table
    /// 若纳入 TenantId，而该列实体不映射、插入恒为 NULL，PG 中 **NULL 互不相等 ⇒ 唯一约束静默失效**（同键可重复插）。
    /// **勿再"顺手"给本表加 TenantId**（专项件已把本表移出批 1 射程）。
    /// </remarks>
    [SugarTable("sys_role_table", "角色字段权限")]
    [IncreTable]
    [SugarIndex("i_sys_role_table", nameof(RoleId), OrderByType.Asc, nameof(TableName), OrderByType.Asc, nameof(ColumnName), OrderByType.Asc, true)]
    public class SysRoleTable : EntityBase
    {
        /// <summary>角色 Id(关联 sys_role.Id)。</summary>
        [SugarColumn(ColumnDescription = "角色Id")]
        public long RoleId { get; set; }

        /// <summary>表名(库表名,如 sys_user——与实体 [SugarTable] 声明一致)。</summary>
        [SugarColumn(ColumnDescription = "表名", Length = 64)]
        public string TableName { get; set; } = string.Empty;

        /// <summary>列名(库列名;本仓多数与属性名同形,显式 [SugarColumn(ColumnName)] 时以声明为准)。</summary>
        [SugarColumn(ColumnDescription = "列名", Length = 64)]
        public string ColumnName { get; set; } = string.Empty;
    }
}