namespace AdminCore.Core.Services.Seed;

/// <summary>
/// 命令式启动种子契约(G32):用于"需要代码逻辑"的启动期种子——数据补写、迁移、按条件计算等
/// 无法用声明式数据种子表达的场景。
/// </summary>
/// <remarks>
/// 与声明式 <c>[IncreSeed]</c>(<c>ISqlSugarEntitySeedData&lt;T&gt;</c>)的时序关系:
/// **后者先跑**——declarative 种子在 <c>StartupSchemaInitializer</c> 的集群锁段内
/// (HealColumnCase → InitTables → InitSeeds → InitSeedData)完成;imperative 种子在其后,
/// 由 <see cref="StartupSeedRunner"/> 按 <see cref="Order"/> 串行执行。
/// 实现类贴 <c>[Service]</c> 即由 <c>AddAutoServices</c> 自动注册(唯一公共接口 = 本接口 ⇒ 接口→实现)。
/// </remarks>
public interface IStartupSeed
{
    /// <summary>执行顺序(升序串行);同值次序由 <c>IEnumerable</c> 解析顺序决定,实现方应给互异值。</summary>
    int Order { get; }

    /// <summary>
    /// 执行本种子。抛异常即视为失败:缺省 fail-fast 中断后续种子,
    /// <c>StartupSeedOptions.ContinueOnFailure=true</c> 时记录后继续。
    /// </summary>
    /// <param name="serviceProvider">启动期作用域(<c>Program.cs</c> 传入);实现经此解析仓储/服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RunAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default);
}