using SqlSugar;

namespace AdminCore.Core.SeedData
{
    /// <summary>
    /// 增量种子契约:被 [IncreSeed] 标记的类实现本接口,启动时框架扫描并按类名顺序执行(在建表 InitTables 之后)。
    /// </summary>
    /// <remarks>
    /// WHY 约定无参构造而非 DI 注入:种子在宿主启动期一次性执行,依赖面收敛为 ISqlSugarClient + 静态工具,
    /// 走 Activator 保持机制最简;后续种子如需注入更多服务,再升级 ActivatorUtilities(不提前设计)。
    /// </remarks>
    public interface ISqlSugarSeed
    {
        /// <summary>执行种子(必须幂等:数据已存在则跳过,不重复插入、不抛错)。</summary>
        /// <param name="db">SqlSugar 客户端(AOP 建表/审计/软删过滤器已挂好,种子插入同样享受自动填充)。</param>
        Task SeedAsync(ISqlSugarClient db);
    }
}
