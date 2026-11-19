using global::SqlSugar;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 机构数据权限查询放行扩展(task_013 契约 4,对齐 TenantQueryExtension.IgnoreTenant 先例):
    /// 跨机构场景(数据迁移、统计报表、超管手工跨查)用 IgnoreOrgData&lt;T&gt;() 清除当前查询的机构过滤。
    /// </summary>
    /// <remarks>
    /// WHY 用查询级 ClearFilter(typeof(T)) 而非 QueryFilter.Clear:后者改动共享 Scope 的过滤状态
    /// (基座期单请求作用域良性,并发多请求下会串扰其他请求);查询级只作用于当前查询实例,无共享状态。
    /// 匹配语义(5.1.4.217 反编译实测):ClearFilter(Type[]) 按 item.type 排除——精确排除实体 T 的机构过滤
    /// (Type 重载注册的 TableFilterItem&lt;object&gt;.type=具体实体类型),接口级软删/租户过滤(item.type=接口)
    /// 不受影响,自动保留。与 PermissionMiddleware 的按请求装配/清除互不干涉。
    /// </remarks>
    public static class OrgQueryExtension
    {
        /// <summary>
        /// 跨机构查询放行:返回清除机构数据范围过滤的查询实例(软删全局过滤器不受影响,自动保留)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端。</param>
        /// <typeparam name="T">实体类型(继承 EntityBaseData/EntityTenantBaseData 且已注册机构过滤)。</typeparam>
        public static ISugarQueryable<T> IgnoreOrgData<T>(this ISqlSugarClient db) where T : class, new()
        {
            return db.Queryable<T>().ClearFilter(typeof(T));
        }
    }
}
