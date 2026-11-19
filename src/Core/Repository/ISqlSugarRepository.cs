using System.Linq.Expressions;
using AdminCore.Core.Models;
using SqlSugar;

namespace AdminCore.Core.Repository
{
    /// <summary>
    /// 通用仓储接口:CRUD + 分页。业务代码依赖本接口而非具体实现(可替换/可 mock)。
    /// </summary>
    /// <typeparam name="T">实体类型(带无参构造的 class)。</typeparam>
    public interface ISqlSugarRepository<T> where T : class, new()
    {
        /// <summary>插入单条(雪花主键/创建审计由 AOP 自动填充)。</summary>
        Task<bool> InsertAsync(T model);

        /// <summary>按主键整对象更新(更新审计由 AOP 自动填充;创建期审计列被 IsOnlyIgnoreUpdate 排除)。</summary>
        Task<bool> UpdateAsync(T model);

        /// <summary>
        /// 并发安全更新(批 K 3.1,参照 UpdateWithVerAsync 移植):实体带名为 <paramref name="verFieldName"/>
        /// (缺省 "Ver")的 int/int? 属性时执行"版本匹配校验＋自增"单语句原子更新
        /// (UPDATE ... SET Ver = Ver + 1 WHERE 主键 AND Ver = 读回版本),否则回落普通整对象更新。
        /// </summary>
        /// <param name="input">要更新的实体,必须含主键与当前(读回的)Ver 值;Ver 列走 SQL 表达式自增,不依赖其最终值。</param>
        /// <returns>true=更新成功;false=并发冲突(版本已变)或记录不存在(不抛异常,冲突是正常业务分支)。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 null。</exception>
        /// <exception cref="ArgumentException">Ver 为 int? 类型且值为 null(无法进行版本校验)。</exception>
        Task<bool> UpdateWithVerAsync(T input, string verFieldName = "Ver");

        /// <summary>
        /// 按主键删除。软删口径(task_003 定):实现 IDeletedFilter 的实体更新 IsDelete 为 true(物理行保留),
        /// 否则回退物理删除。
        /// </summary>
        Task<bool> DeleteByIdAsync(object id);

        /// <summary>
        /// 条件物理删除(绕过软删语义,返回受影响行数)。
        /// WHY 独立方法:RBAC 关联表(SysUserRole/SysRoleMenu)清理走物理删——中间行无审计价值,
        /// 软删残留会让"授权全量替换"留下不可见脏行;与 DeleteByIdAsync 的软删口径显式区分,防误用。
        /// </summary>
        Task<int> DeletePhysicalByAsync(Expression<Func<T, bool>> expression);

        /// <summary>查询第一条符合条件的记录(无则 null;全局软删过滤器自动生效)。</summary>
        Task<T> GetFirstAsync(Expression<Func<T, bool>> expression);

        /// <summary>是否存在符合条件的记录(全局软删过滤器自动生效)。</summary>
        Task<bool> IsAnyAsync(Expression<Func<T, bool>> expression);

        /// <summary>可查询对象(高级查询/统计入口;全局软删过滤器自动生效)。</summary>
        ISugarQueryable<T> AsQueryable();

        /// <summary>分页查询:返回总条数 + 当前页列表。</summary>
        /// <param name="pageIndex">页码,从 1 开始。</param>
        /// <param name="pageSize">每页条数。</param>
        Task<PageResult<T>> ToPagedListAsync(int pageIndex, int pageSize);

        /// <summary>
        /// 分表查询入口(批 AC 3.1):返回按分表策略展开的查询对象(跨子表 UNION 由 SqlSugar 生成)。
        /// 仅对贴 <c>[SplitTable]</c> 的实体有效(非分表实体调用抛异常);全局软删过滤器自动生效。
        /// </summary>
        ISugarQueryable<T> AsSplitQueryable();

        /// <summary>
        /// 分表插入(批 AC 3.1):按实体时间字段路由到对应子表(子表不存在自动创建);审计字段/主键仍由 AOP 填充。
        /// </summary>
        Task<int> InsertSplitAsync(T entity);
    }
}
