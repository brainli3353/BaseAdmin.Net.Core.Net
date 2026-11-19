using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统机构(组织树:用户归属 OrgId 的数据权限枢纽 + 机构管理闭环)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段(口径同 SysMenu/SysUser)。
    /// </summary>
    /// <remarks>
    /// 树形:Pid 指向父机构,0=顶级(根节点由种子落「总部」);组树在服务层内存递归(管理端数据量级小,
    /// 单次全量查询足够,与 SysMenuService.TreeAsync 同口径)。
    /// Code 唯一性由服务层校验(建库不配唯一约束,与项目实体口径一致——字典模块同机制)。
    /// </remarks>
    [SugarTable("sys_org", "系统机构")]
    // WHY 索引:高频查询列——SysOrgService.Delete 子机构校验(有子节点拒删)＋树形层级按 Pid 关联
    [SugarIndex("i_sys_org_pid", nameof(Pid), OrderByType.Asc)]
    [IncreTable]
    [SysTable]
    public class SysOrg : EntityTenant
    {
        /// <summary>父机构 Id(0=顶级;树形结构)。</summary>
        [SugarColumn(ColumnDescription = "父机构Id")]
        public long Pid { get; set; } = 0;

        /// <summary>机构名称(如 总部;ORM 读回无空值保证,按项目实体惯例用可空引用类型)。</summary>
        [SugarColumn(ColumnDescription = "机构名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>
        /// 机构编码(唯一,业务标识;服务层校验唯一性)。
        /// IsNullable=true:编码可缺省(契约 Code 唯一但未强制必填);NOT NULL 列会把两个缺省编码
        /// 都落成空串 → 误触发唯一性冲突,必须可空(SysMenu.Icon/SysUser.OrgId 同口径)。
        /// </summary>
        [SugarColumn(ColumnDescription = "机构编码", Length = 64, IsNullable = true)]
        public string? Code { get; set; }

        /// <summary>
        /// 排序号(同级升序展示)。
        /// DefaultValue="0":增量迁移必备——共享开发库的 sys_org 由 task_013 旧实体先建(带存量行、无本列),
        /// 缺省时 CodeFirst 生成 ADD COLUMN "Sort" int4 NOT NULL(无默认值)→ PG 23502 拒(非空表);
        /// 带默认值则存量行自动填 0(与 C# 侧缺省值一致)。
        /// </summary>
        [SugarColumn(ColumnDescription = "排序号", DefaultValue = "0")]
        public int Sort { get; set; } = 0;

        /// <summary>
        /// 备注(用途说明)。IsNullable=true:同 Code——缺省不落空串,可空列直接存 NULL。
        /// </summary>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>
        /// 状态:0 启用,1 禁用(与 SysUser.Status 同口径)。
        /// DefaultValue="0":同 Sort——存量表增量迁移时存量行自动填 0(启用),语义一致(见 Sort WHY)。
        /// </summary>
        [SugarColumn(ColumnDescription = "状态(0启用1禁用)", DefaultValue = "0")]
        public int Status { get; set; } = 0;
    }
}
