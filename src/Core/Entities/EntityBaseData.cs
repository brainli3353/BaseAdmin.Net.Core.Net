using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 机构数据范围过滤标记接口(task_013 数据权限):实现本接口的实体,其 CreateOrgId 参与机构范围行级过滤
    /// (读侧装配点 Web.Core/Auth/PermissionMiddleware + Core/SqlSugar/SqlSugarFilter;写侧 AOP 自动填,见 SqlSugarSetup)。
    /// </summary>
    public interface IOrgIdFilter
    {
        /// <summary>创建机构Id(插入时 AOP 自动填:当前登录用户所属机构;无用户上下文 → 空 = 存量数据语义)。</summary>
        long? CreateOrgId { get; set; }
    }

    /// <summary>
    /// 机构数据权限实体基类(task_013):需要机构维度数据权限的业务实体继承本类(而非 EntityBase),
    /// 自动获得:创建机构 CreateOrgId(AOP 插入自动填)+ 读侧机构范围过滤(零业务侵入,继承即生效)。
    /// 读侧语义(详见 SqlSugarFilter):仅本人 → [OwnerUser] CreateUserId;本部门/本部门及以下 → CreateOrgId 落入可见机构集;
    /// CreateOrgId 为空的存量行在机构范围下不可见(fail-closed,存量数据行为已文档化)。
    /// </summary>
    public class EntityBaseData : EntityBase, IOrgIdFilter
    {
        /// <summary>
        /// 创建机构Id(行级机构归属)。
        /// WHY IsOnlyIgnoreUpdate:机构归属是创建期属性,更新语句不允许改归属(迁移机构走显式路径,同 TenantId 口径)。
        /// </summary>
        [SugarColumn(ColumnName = "CreateOrgId", ColumnDescription = "创建机构Id", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        [OwnerOrg]
        public long? CreateOrgId { get; set; }
    }

    /// <summary>
    /// 无租户机构数据权限实体基类(D1.3 S-G 纵深):不归属租户但需要机构范围数据权限的业务实体继承本类
    /// (而非 EntityBase),如 sys_file(实体注释明示文件不归属租户,保持无租户语义,但机构范围行级过滤仍需生效)。
    /// 与 EntityBaseData 的差别:不挂租户基座(无 TenantId),行级只参与机构维度过滤;
    /// 读侧装配口径:实现 IOrgIdFilter 即被 SqlSugarFilter 纳管(继承即生效,同基类口径)。
    /// </summary>
    public class EntityBaseOrg : EntityBase, IOrgIdFilter
    {
        /// <summary>
        /// 创建机构Id(行级机构归属)。
        /// WHY IsOnlyIgnoreUpdate:机构归属是创建期属性,更新语句不允许改归属(迁移机构走显式路径,同 TenantId 口径)。
        /// </summary>
        [SugarColumn(ColumnName = "CreateOrgId", ColumnDescription = "创建机构Id", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        [OwnerOrg]
        public long? CreateOrgId { get; set; }
    }

    /// <summary>
    /// 机构数据权限实体基类 + 更新机构(task_013):在 EntityBaseData 之上增 UpdateOrgId(AOP 更新自动填),
    /// 行级「最后更新机构」同样受机构范围约束(多 [OwnerOrg] 属性 OR 联结)。
    /// </summary>
    public class EntityBaseUpdateData : EntityBaseData
    {
        /// <summary>更新机构Id(更新时 AOP 自动填:当前登录用户所属机构)。</summary>
        [SugarColumn(ColumnName = "UpdateOrgId", ColumnDescription = "更新机构Id", IsNullable = true)]
        [OwnerOrg]
        public long? UpdateOrgId { get; set; }
    }

    /// <summary>
    /// 租户 + 机构数据权限实体基类(task_013):同时需要租户与机构数据权限的业务实体继承本类(而非 EntityTenant),
    /// 自动获得 TenantId(租户基座)+ CreateOrgId(机构数据权限)两套行级过滤。
    /// </summary>
    public class EntityTenantBaseData : EntityTenant, IOrgIdFilter
    {
        /// <summary>
        /// 创建机构Id(行级机构归属)。
        /// WHY IsOnlyIgnoreUpdate:机构归属是创建期属性,更新语句不允许改归属(迁移机构走显式路径,同 TenantId 口径)。
        /// </summary>
        [SugarColumn(ColumnName = "CreateOrgId", ColumnDescription = "创建机构Id", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        [OwnerOrg]
        public long? CreateOrgId { get; set; }
    }
}
