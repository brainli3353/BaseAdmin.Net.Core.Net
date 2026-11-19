using System.Linq.Expressions;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using global::SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 机构数据范围 ORM 过滤器(task_013 数据权限,读侧装配点):对实现 IOrgIdFilter 的
    /// [SugarTable] 具体实体(排除 [IgnoreTable]/[LogTable])按请求追加行级过滤——
    /// · 仅本人(4)→ [OwnerUser] 属性(继承的 CreateUserId) == 当前用户 Id;
    /// · 本部门(3)/本部门及以下(2)→ [OwnerOrg] 属性落入用户可见机构集(Contains;空集 = fail-closed,查无行);
    /// · 全部(1)→ 不装配(超管/全部范围等价,超管豁免在 PermissionMiddleware);
    /// · 自定义(5)→ 机构集由 GetUserOrgScopeAsync 从 sys_role_org 读取(批 B B2;无授权行 = 空集 fail-closed)。
    /// 同一实体多个 [OwnerOrg]/[OwnerUser] 属性以 OR 联结(表达式树 Expression.OrElse);无对应标记属性的类型跳过。
    /// 装配点:PermissionMiddleware 每请求 SetOrgEntityFilter(超管/无用户豁免)+ finally ClearOrgFilter(与租户过滤同口径)。
    /// 过滤器为按请求内存装配(无持久缓存——契约 5 键中的 db:{dbConfigId}:orgList:{userId} 键本仓不存在,偏差见验收报告)。
    /// 基座期口径:QueryFilter 为单例 SqlSugarScope 全局状态 → 并发请求存在串扰窗口(与租户过滤同口径,已文档化)。
    /// WHY 反射装配(非逐实体注册):业务实体继承基类即自动获得数据权限,零业务侵入(契约 4);
    /// 实体集经 Init 显式注册(装配与开发解耦):Program.cs 传全部实体所在程序集,测试传测试程序集。
    /// </summary>
    public static class SqlSugarFilter
    {
        private static readonly object Gate = new();
        private static List<Entry>? _entries;
        private static bool _initialized;

        private sealed class Entry
        {
            public Entry(Type type, List<PropertyInfo> orgProps, List<PropertyInfo> userProps)
            {
                Type = type;
                OrgProps = orgProps;
                UserProps = userProps;
            }

            public Type Type { get; }
            public List<PropertyInfo> OrgProps { get; }
            public List<PropertyInfo> UserProps { get; }
        }

        /// <summary>
        /// 注册参与数据权限的实体集(幂等,可多次调用,末次生效):扫描指定程序集,
        /// 收集 [SugarTable] 具体类且实现 IOrgIdFilter(D1.3 S-G 纵深:门槛由"继承 EntityBaseData/EntityTenantBaseData"
        /// 收敛为接口判定——这是本机制的声明式语义,凡实现 IOrgIdFilter 的业务实体(含无租户 EntityBaseOrg 系)一律纳管,
        /// 避免新基类漏配;排除 [IgnoreTable](只读外部表)与 [LogTable](日志库——次库不参与主库过滤器装配))。
        /// </summary>
        public static void Init(params Assembly[] assemblies)
        {
            lock (Gate)
            {
                var entries = new List<Entry>();
                var seen = new HashSet<Type>();
                foreach (var assembly in assemblies)
                {
                    foreach (var type in SafeGetTypes(assembly))
                    {
                        if (!type.IsClass || type.IsAbstract)
                        {
                            continue;
                        }

                        if (type.GetCustomAttribute<SugarTable>(inherit: false) is null)
                        {
                            continue;
                        }

                        if (type.GetCustomAttribute<IgnoreTableAttribute>() is not null
                            || type.GetCustomAttribute<LogTableAttribute>() is not null)
                        {
                            continue;
                        }

                        if (!typeof(IOrgIdFilter).IsAssignableFrom(type))
                        {
                            continue;
                        }

                        if (seen.Contains(type))
                        {
                            continue;
                        }

                        seen.Add(type);
                        entries.Add(new Entry(
                            type,
                            FindMarkerProps(type, typeof(OwnerOrgAttribute)),
                            FindMarkerProps(type, typeof(OwnerUserAttribute))));
                    }
                }

                _entries = entries;
                _initialized = true;
            }
        }

        /// <summary>
        /// 设定当前请求的机构数据范围过滤(契约 4)。返回是否设定(为 true 时调用方须 finally ClearOrgFilter,防状态泄漏到下一请求)。
        /// 全部范围(1)直接返回 false(不装配);无实体/无标记属性 → false。
        /// </summary>
        public static bool SetOrgEntityFilter(ISqlSugarClient db, long userId, DataScopeEnum maxScope, IReadOnlyList<long> orgIds)
        {
            if (maxScope == DataScopeEnum.All)
            {
                return false;
            }

            var set = false;
            foreach (var entry in GetEntries())
            {
                var condition = BuildCondition(entry, userId, maxScope, orgIds);
                if (condition is null)
                {
                    continue; // 无对应标记属性 → 该类型不参与此维度(跳过)
                }

                // 非泛型 Type 重载(5.1.4.217 QueryFilterProvider.AddTableFilter(Type, Expression, FilterJoinPosition),探针实测可用):
                // FilterJoinPosition 是 QueryFilterProvider 的嵌套枚举(探针实测:SqlSugar.QueryFilterProvider+FilterJoinPosition);
                // 条件内已 OR 联结多 [OwnerOrg]/[OwnerUser] 属性;On 位置与租户过滤/软删过滤同默认
                db.QueryFilter.AddTableFilter(entry.Type, condition, QueryFilterProvider.FilterJoinPosition.On);
                set = true;
            }

            return set;
        }

        /// <summary>
        /// 清除机构数据范围过滤(finally 调用):只清本机制添加的具体类型注册,
        /// 接口级过滤器(IDeletedFilter 软删/ITenantIdFilter 租户)不受影响(探针实测:Clear(Type[]) 按注册类型精确匹配)。
        /// </summary>
        public static void ClearOrgFilter(ISqlSugarClient db)
        {
            var entries = GetEntries();
            if (entries.Count == 0)
            {
                return;
            }

            db.QueryFilter.Clear(entries.Select(e => e.Type).ToArray());
        }

        /// <summary>
        /// 单用户数据权限缓存失效(task_013 契约 5,变更即失效,不靠 TTL 兜底):
        /// KeyUserOrg(机构集)+ KeyRoleMaxDataScope(最大范围)+ KeyUserApiPerm(按钮权限,契约 KeyUserApi 在本仓键名)。
        /// 偏差说明:契约 5 键清单另含 KeyUserRole(本仓无角色列表缓存)与 db:{dbConfigId}:orgList:{userId}
        /// (本仓过滤器为按请求内存装配,无持久缓存)——两键本仓不存在,不删,详见验收报告。
        /// </summary>
        public static async Task DeleteUserOrgCacheAsync(ICacheService cache, long userId)
        {
            await cache.RemoveAsync(CacheConst.KeyUserOrg + userId);
            await cache.RemoveAsync(CacheConst.KeyRoleMaxDataScope + userId);
            await cache.RemoveAsync(CacheConst.KeyUserApiPerm + userId);
        }

        /// <summary>实体集:未显式 Init 时兜底扫 Core 程序集(框架自保;业务程序集须显式 Init,见类注释)。</summary>
        private static List<Entry> GetEntries()
        {
            if (_initialized)
            {
                return _entries!;
            }

            lock (Gate)
            {
                if (!_initialized)
                {
                    Init(typeof(SqlSugarSetup).Assembly);
                }

                return _entries!;
            }
        }

        /// <summary>
        /// 构建单实体类型的行级条件:仅本人 → [OwnerUser] == userId;机构范围 → [OwnerOrg] Contains(orgIds)
        /// (orgIds 空 → Contains(空集) → 无行,fail-closed)。多属性 OR 联结;无属性 → null(跳过)。
        /// WHY 必须返回带命名参数的 LambdaExpression(反编译 5.1.4.217 探针实测):
        /// QueryBuilder.AppendTableFilter 取 exp 强转 LambdaExpression 并读 Parameters[0].Name 作列前缀——
        /// 裸 body(非 lambda)→ 强转 null → NRE;未命名参数 → Name=null → 前缀映射失败。
        /// 且 Clear&lt;T&gt;() 无法清除 Type 重载注册的 TableFilterItem&lt;object&gt;(只匹配泛型项),
        /// 故清理必须用 Clear(Type[])(ClearOrgFilter 已按此实现)。
        /// </summary>
        private static Expression? BuildCondition(Entry entry, long userId, DataScopeEnum maxScope, IReadOnlyList<long> orgIds)
        {
            var param = Expression.Parameter(entry.Type, "it");
            Expression? condition = null;

            if (maxScope == DataScopeEnum.Self)
            {
                foreach (var prop in entry.UserProps)
                {
                    var equal = Expression.Equal(
                        Expression.Property(param, prop),
                        Expression.Constant(userId, typeof(long?)));
                    condition = condition is null ? equal : Expression.OrElse(condition, equal);
                }

                return condition is null ? null : Expression.Lambda(condition, param);
            }

            foreach (var prop in entry.OrgProps)
            {
                var list = orgIds.Select(v => (long?)v).ToList();
                var contains = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    new[] { typeof(long?) },
                    Expression.Constant(list),
                    Expression.Property(param, prop));
                condition = condition is null ? contains : Expression.OrElse(condition, contains);
            }

            return condition is null ? null : Expression.Lambda(condition, param);
        }

        /// <summary>收集实体类型上的标记属性(含基类继承:GetProperties 默认 inherited=true)。</summary>
        private static List<PropertyInfo> FindMarkerProps(Type type, Type attributeType)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute(attributeType) is not null)
                .ToList();
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null)!;
            }
        }
    }
}
