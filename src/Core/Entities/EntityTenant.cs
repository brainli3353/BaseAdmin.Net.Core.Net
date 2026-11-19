using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 租户过滤标记接口(租户基座):实现本接口的实体,其查询被租户过滤器自动追加 TenantId 条件
    /// (过滤器按请求装配,见 Web.Core/Auth/PermissionMiddleware;无租户 Claim/超管 → 不过滤)。
    /// </summary>
    public interface ITenantIdFilter
    {
        /// <summary>租户Id(行级租户归属;插入时由 AOP 自动填:Claim 值,缺省 SqlSugarConst.DefaultTenantId)。</summary>
        long? TenantId { get; set; }
    }

    /// <summary>
    /// 租户实体基类(租户基座):业务实体一律继承本类(不是 EntityBase)——
    /// 存量数据天然带默认租户值(后补多租户时免回填迁移)。
    /// </summary>
    /// <remarks>
    /// 三层机制(对齐设计精华参考 §9):
    /// 1. 查询过滤:PermissionMiddleware 按请求 AddTableFilter&lt;ITenantIdFilter&gt;(当前租户),超管/无 Claim 跳过;
    /// 2. 写入自动填:Aop.DataExecuting 插入时 TenantId 为空 → Claim 值,缺省 DefaultTenantId;
    /// 3. 选库预留:[Tenant] 特性(仓储按特性选 ConfigId,租户库懒加载后补)。
    /// </remarks>
    public class EntityTenant : EntityBase, ITenantIdFilter
    {
        /// <summary>
        /// 租户Id(行级租户归属)。
        /// WHY IsOnlyIgnoreUpdate:租户归属是创建期属性,更新语句不允许改租户(跨租户迁移走显式迁移路径,迭代项)。
        /// </summary>
        [SugarColumn(ColumnName = "TenantId", ColumnDescription = "租户Id", IsOnlyIgnoreUpdate = true, IsNullable = true)]
        public virtual long? TenantId { get; set; }
    }
}
