using AdminCore.Core.Exceptions;
using SqlSugar;

namespace AdminCore.Core.Utils.DynamicQuery
{
    /// <summary>
    /// 动态查询扩展(批 AG 3.11):把"排序/关键字搜索"的字段名**经实体元数据白名单校验**后施加到查询上。
    /// </summary>
    /// <remarks>
    /// 用法(业务端点接收 <see cref="DynamicQueryInput"/> 后一行接入):
    /// <code>
    /// var list = await repo.AsQueryable()
    ///     .Where(u =&gt; u.Status == 0)
    ///     .ApplyDynamic(input.Dynamic, input.Keyword)   // ← 字段名白名单校验 + 参数化关键字
    ///     .ToPageListAsync(page, size, total);
    /// </code>
    /// 安全口径(三条,与 DynamicQueryInput remarks 对应):
    /// ① 排序字段经 <c>EntityMaintenance.GetEntityInfo&lt;T&gt;().Columns</c> 与属性名双重白名单比对,未命中即抛
    ///    <see cref="AppException"/>(文案可检索:含"不在白名单");
    /// ② 排序方向由 bool 决定(OrderByType 枚举),无字符串拼接;
    /// ③ 关键字经 SqlSugar <c>OrderBy/Where</c> 的**表达式/参数化**路径传入,不拼 SQL 文本。
    /// 能力先行(约定 #8):框架提供该扩展点,业务按需接入;演示见 <c>DemoController.dynamicQuery</c>。
    /// </remarks>
    public static class DynamicQueryExtensions
    {
        /// <summary>
        /// 施加动态排序与关键字搜索:
        /// <paramref name="input"/>.OrderBy 非空 → 按该列排序(方向由 Desc 决定);
        /// <paramref name="input"/>.SearchBy 非空且 <paramref name="keyword"/> 非空 → 对所列字段做 OR 模糊匹配(参数化);
        /// SearchBy 非空但 keyword 为空 → 不施加搜索(不报错,仅排序);
        /// keyword 非空但 SearchBy 为空 → 抛业务异常(避免"以为搜索了其实没搜")。
        /// </summary>
        public static ISugarQueryable<T> ApplyDynamic<T>(this ISugarQueryable<T> query, DynamicQueryInput? input, string? keyword = null)
            where T : class, new()
        {
            if (input is null)
            {
                return query;
            }

            var columns = ResolveColumnWhitelist<T>();

            // ① 排序(白名单校验 + 方向枚举化)
            if (!string.IsNullOrWhiteSpace(input.OrderBy))
            {
                var orderBy = input.OrderBy.Trim();
                if (!columns.TryGetValue(orderBy, out var orderProp))
                {
                    throw AppException.With(ErrorCodes.DynamicQueryOrderByNotAllowed, "field", orderBy, 400);
                }

                // WHY 构造表达式树而非 SqlSugar 的字符串排序重载:白名单校验后的列名仍**不拼进 SQL 文本**——
                // 表达式树经 ORM 参数化/转义路径生成 "ORDER BY "列" DESC",杜绝任何字符串拼接面
                var orderParam = System.Linq.Expressions.Expression.Parameter(typeof(T), "it");
                var orderMember = System.Linq.Expressions.Expression.Property(orderParam, orderProp);
                var orderLambda = System.Linq.Expressions.Expression.Lambda<Func<T, object>>(
                    System.Linq.Expressions.Expression.Convert(orderMember, typeof(object)), orderParam);
                query = query.OrderBy(orderLambda, input.Desc ? OrderByType.Desc : OrderByType.Asc);
            }

            // ② 关键字搜索(字段白名单 + 参数化值)
            var searchBy = (input.SearchBy ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kw = keyword?.Trim();

            if (searchBy.Length > 0 && string.IsNullOrEmpty(kw))
            {
                return query; // 只给了字段没给关键字:不施加(静默,不报错)
            }

            if (searchBy.Length == 0)
            {
                if (!string.IsNullOrEmpty(kw))
                {
                    throw AppException.Of(ErrorCodes.DynamicQuerySearchByRequired, args: null, code: 400);
                }

                return query;
            }

            var searchProps = new List<string>();
            foreach (var field in searchBy)
            {
                if (!columns.TryGetValue(field, out var prop))
                {
                    throw AppException.With(ErrorCodes.DynamicQueryFieldNotAllowed, "field", field, 400);
                }

                searchProps.Add(prop);
            }

            // SqlSugar:多字段 OR 模糊 —— 用表达式树按属性名动态构造(it => it.X.Contains(kw) || it.Y.Contains(kw))
            var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T), "it");
            System.Linq.Expressions.Expression? body = null;
            foreach (var propName in searchProps)
            {
                var prop = typeof(T).GetProperty(propName);
                if (prop is null || prop.PropertyType != typeof(string))
                {
                    continue; // 非字符串列不参与 LIKE(白名单校验已通过,类型不符按跳过处理)
                }

                var member = System.Linq.Expressions.Expression.Property(parameter, prop);
                var notNull = System.Linq.Expressions.Expression.NotEqual(
                    member, System.Linq.Expressions.Expression.Constant(null, typeof(string)));
                var contains = System.Linq.Expressions.Expression.Call(
                    member,
                    typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                    System.Linq.Expressions.Expression.Constant(kw, typeof(string)));
                var clause = System.Linq.Expressions.Expression.AndAlso(notNull, contains);
                body = body is null ? clause : System.Linq.Expressions.Expression.OrElse(body, clause);
            }

            if (body is null)
            {
                return query;
            }

            var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter);
            return query.Where(lambda);
        }

        /// <summary>
        /// 动态排序(带默认序,批 CI):恰好施加**一个** ORDER BY——<paramref name="orderBy"/> 为空 → 用
        /// <paramref name="defaultSort"/> 表达式按 <paramref name="defaultType"/> 排序(保持端点原有默认序);
        /// 非空 → 白名单校验(未命中抛 <see cref="AppException"/>)＋表达式树排序(方向由 <paramref name="desc"/> 决定,缺省降序)。
        /// </summary>
        /// <remarks>
        /// 用法:端点原 `.OrderByDescending(x => x.Id)` 改一行
        /// <code>.ApplyDynamicSort(input.OrderBy, input.Desc, x =&gt; x.Id, OrderByType.Desc)</code>
        /// (勿与原 OrderBy 并存——SqlSugar 多次 OrderBy 会叠加成多列 ORDER BY,默认序必须走本方法的回落分支)。
        /// 仅适用**单实体**查询;join 投影端点(如 OpenAccess 三表 join)不适用,维持固定默认序(批 CI 边界)。
        /// </remarks>
        public static ISugarQueryable<T> ApplyDynamicSort<T>(
            this ISugarQueryable<T> query,
            string? orderBy,
            bool? desc,
            System.Linq.Expressions.Expression<Func<T, object?>> defaultSort,
            OrderByType defaultType = OrderByType.Desc)
            where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(orderBy))
            {
                return query.OrderBy(defaultSort, defaultType);
            }

            return query.ApplyDynamic(new DynamicQueryInput(null, orderBy, desc ?? true));
        }

        /// <summary>
        /// 列白名单:实体元数据的列名 + 属性名(大小写不敏感) → 实际属性名(供 SqlSugar OrderBy 用)。
        /// WHY 取元数据而非只看反射:Kestrel 实体可能用 [SugarColumn(ColumnName=...)] 映射到不同列名,
        /// 业务方按"库列名"传参也应被接受(与参照仓按列名口径一致)。
        /// </summary>
        /// <summary>
        /// 动态排序(自定义默认序链形式,批 CI):<paramref name="orderBy"/> 为空时原样执行 <paramref name="defaultOrderChain"/>
        /// (任意多列/任意方向——保留端点原默认序,如 OrderNo asc + Id desc);非空时白名单校验 + 表达式树排序。
        /// 与单表达式形式同口径:调用方须**替换**原 OrderBy(不能并存);orderBy 为空 ⇒ 默认序链原样生效(行为不变)。
        /// 用法:<c>query.ApplyDynamicSort(input.OrderBy, input.Desc, q => q.OrderBy(x => x.OrderNo).OrderByDescending(x => x.Id))</c>。
        /// </summary>
        /// <param name="query">查询。</param>
        /// <param name="orderBy">排序字段(实体属性名/数据库列名,大小写不敏感);空 = 走默认序链。</param>
        /// <param name="desc">方向;null = 降序(与单表达式形式同口径)。</param>
        /// <param name="defaultOrderChain">orderBy 为空时的默认序链(原端点 OrderBy 原样搬入)。</param>
        /// <typeparam name="T">实体类型。</typeparam>
        /// <returns>追加排序后的查询。</returns>
        public static ISugarQueryable<T> ApplyDynamicSort<T>(
            this ISugarQueryable<T> query,
            string? orderBy,
            bool? desc,
            Func<ISugarQueryable<T>, ISugarQueryable<T>> defaultOrderChain) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(orderBy))
            {
                return defaultOrderChain(query);
            }
            return query.ApplyDynamic(new DynamicQueryInput(null, orderBy, desc ?? true));
        }

        private static Dictionary<string, string> ResolveColumnWhitelist<T>() where T : class, new()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                map[prop.Name] = prop.Name;
                var column = prop.GetCustomAttributes(typeof(SugarColumn), true)
                    .OfType<SugarColumn>()
                    .FirstOrDefault()?.ColumnName;
                if (!string.IsNullOrWhiteSpace(column))
                {
                    map[column!] = prop.Name;
                }
            }

            return map;
        }
    }
}