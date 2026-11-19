using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 实体基类:审计字段(创建/更新) + 软删标记。业务实体继承后只写业务字段,
    /// 主键/CreateTime/UpdateTime/用户字段由 SqlSugar AOP(DataExecuting)统一自动填充。
    /// </summary>
    public class EntityBase : EntityBaseId, IDeletedFilter
    {
        /// <summary>软删标记:为 true 的行被全局 QueryFilter 过滤(软删=更新本字段为 true,非物理删除)。</summary>
        [SugarColumn(ColumnName = "IsDelete", ColumnDescription = "是否删除")]
        public bool IsDelete { get; set; }

        /// <summary>删除时间(AOP 在 UpdateByObject 路径 IsDelete=true 时自动填)。</summary>
        [SugarColumn(ColumnName = "DeleteTime", ColumnDescription = "删除时间", IsNullable = true)]
        public DateTime? DeleteTime { get; set; }

        /// <summary>
        /// 创建时间(AOP 插入时自动填)。
        /// WHY IsOnlyIgnoreUpdate:创建期审计字段不允许被更新语句覆盖——
        /// 整对象更新生成的 UPDATE 若带上这些列,会把 CreateTime 刷成当前时间,审计失真。
        /// </summary>
        [SugarColumn(ColumnName = "CreateTime", ColumnDescription = "创建时间", IsOnlyIgnoreUpdate = true)]
        public DateTime? CreateTime { get; set; }

        /// <summary>
        /// 创建用户Id(当前登录用户 Claim;无用户时为空——后台/测试场景;更新时忽略)。
        /// [OwnerUser] 数据权限标记(task_013):仅本人(4)范围行级过滤读本属性——
        /// 标记在基类统一生效,EntityBaseData/EntityTenantBaseData 继承即参与;存量实体(不继承数据权限基类)零影响。
        /// </summary>
        [OwnerUser]
        [SugarColumn(ColumnName = "CreateUserId", ColumnDescription = "创建用户Id", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        public long? CreateUserId { get; set; }

        /// <summary>创建用户(用户名 Claim;无用户时为空;更新时忽略)。</summary>
        [SugarColumn(ColumnName = "CreateUserName", ColumnDescription = "创建用户", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        public string? CreateUserName { get; set; }

        /// <summary>更新时间(AOP 每次更新自动填)。</summary>
        [SugarColumn(ColumnName = "UpdateTime", ColumnDescription = "更新时间", IsNullable = true)]
        public DateTime? UpdateTime { get; set; }

        /// <summary>更新用户Id(当前登录用户 Claim;无用户时为空)。</summary>
        [SugarColumn(ColumnName = "UpdateUserId", ColumnDescription = "更新用户Id", IsNullable = true)]
        public long? UpdateUserId { get; set; }

        /// <summary>更新用户(用户名 Claim;无用户时为空)。</summary>
        [SugarColumn(ColumnName = "UpdateUserName", ColumnDescription = "更新用户", IsNullable = true)]
        public string? UpdateUserName { get; set; }
    }
}
