using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 租户查询放行扩展(租户基座,对齐 baseadmin IgnoreTenant):
    /// 跨租户场景(管理后台跨租户操作、数据迁移、统计报表)用 IgnoreTenant&lt;T&gt;() 清除当前查询的租户过滤。
    /// </summary>
    /// <remarks>
    /// WHY 用查询级 ClearFilter 而非 QueryFilter.Clear:后者改动共享 Scope 的过滤状态
    /// (基座期单租户良性,并发多请求下会串扰其他请求);查询级 ClearFilter 只作用于当前查询实例,无共享状态。
    /// 租户过滤的按请求装配/清除在 PermissionMiddleware,两者互不干涉。
    /// </remarks>
    public static class TenantQueryExtension
    {
        /// <summary>
        /// 跨租户查询放行:返回清除租户过滤的查询实例(软删全局过滤器不受影响,自动保留)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端。</param>
        public static ISugarQueryable<T> IgnoreTenant<T>(this ISqlSugarClient db) where T : class, new()
        {
            return db.Queryable<T>().ClearFilter<ITenantIdFilter>();
        }
    }
}
