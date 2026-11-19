using System.Diagnostics;
using AdminCore.Core.Attributes;
using AdminCore.Core.Option;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminCore.Core.Services.Seed;

/// <summary>
/// 命令式启动种子执行器(G32):resolve 全部 <see cref="IStartupSeed"/>,按 <see cref="IStartupSeed.Order"/>
/// 升序**串行**执行;每颗种子留 <c>[seed] {Name} ok|FAIL {ms}</c> 日志,结束留汇总行。
/// </summary>
/// <remarks>
/// 时序:由 <c>Program.cs</c> 在建表 + 声明式种子完成之后调用——声明式 <c>[IncreSeed]</c> 先跑(见
/// <see cref="IStartupSeed"/> 注释),本执行器只负责命令式种子。
///
/// 失败口径:缺省 **fail-fast**(首颗失败即上抛,后续种子不跑);<c>StartupSeedOptions.ContinueOnFailure=true</c>
/// 时记录后继续。两条路径都先落 <see cref="GetStatus"/> 快照与汇总日志——"跑没跑/跑到哪"必须可判。
///
/// 空转可观测:<c>StartupSeedOptions.Enabled=false</c> ⇒ 零执行,但 <c>LogWarning</c> **列出未执行清单**
/// (静默面变噪音面)。注册数与执行数之差即"跳过"。
/// </remarks>
[Service(Lifetime = ServiceLifetime.Singleton)]
public sealed class StartupSeedRunner
{
    private readonly IOptionsMonitor<StartupSeedOptions> _options;
    private readonly ILogger<StartupSeedRunner> _logger;
    private volatile StartupSeedStatus _status = StartupSeedStatus.Empty;

    /// <summary>构造:选项经 <c>IOptionsMonitor</c>(热更新友好)、日志用于逐颗 + 汇总留痕。</summary>
    public StartupSeedRunner(IOptionsMonitor<StartupSeedOptions> options, ILogger<StartupSeedRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>最近一次 <see cref="RunAllAsync"/> 的内存快照(供诊断/探针消费);未执行过时各计数为 0。</summary>
    public StartupSeedStatus GetStatus() => _status;

    /// <summary>
    /// 执行全部命令式种子(按 Order 升序串行)。
    /// </summary>
    /// <param name="serviceProvider">解析种子实例的作用域(种子缺省 Scoped,须传作用域而非根容器)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RunAllAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        var seeds = serviceProvider.GetServices<IStartupSeed>().OrderBy(s => s.Order).ToArray();
        var names = seeds.Select(s => s.GetType().Name).ToArray();
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            // 开关关闭:零执行,但"哪些没跑"必须响亮可查(G32 核心:静默面变噪音面)
            _status = new StartupSeedStatus(seeds.Length, Executed: 0, Failed: 0, SkippedByToggle: seeds.Length, names);
            _logger.LogWarning(
                "[seed] 开关 StartupSeed:Enabled=false ⇒ 命令式种子全部未执行(注册 {Registered} 颗):{Names}",
                seeds.Length, names.Length == 0 ? "(无)" : string.Join(", ", names));
            return;
        }

        var executed = 0;
        var failed = 0;

        foreach (var seed in seeds)
        {
            var name = seed.GetType().Name;
            var sw = Stopwatch.StartNew();

            try
            {
                await seed.RunAsync(serviceProvider, cancellationToken);
                sw.Stop();
                executed++;
                _logger.LogInformation("[seed] {Name} ok {Ms}ms", name, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;

                if (!options.ContinueOnFailure)
                {
                    // fail-fast:落快照 + 汇总后上抛,后续种子不再执行
                    _status = new StartupSeedStatus(seeds.Length, executed, failed, SkippedByToggle: seeds.Length - executed - failed, names);
                    _logger.LogError(ex, "[seed] {Name} FAIL {Ms}ms ⇒ fail-fast 中断(其后 {Remaining} 颗未执行)", name, sw.ElapsedMilliseconds, seeds.Length - executed - failed);
                    LogSummary(seeds.Length, executed, failed, seeds.Length - executed - failed);
                    throw;
                }

                _logger.LogWarning(ex, "[seed] {Name} FAIL {Ms}ms ⇒ ContinueOnFailure=true,记录后继续", name, sw.ElapsedMilliseconds);
            }
        }

        _status = new StartupSeedStatus(seeds.Length, executed, failed, SkippedByToggle: seeds.Length - executed - failed, names);
        LogSummary(seeds.Length, executed, failed, seeds.Length - executed - failed);
    }

    private void LogSummary(int registered, int executed, int failed, int skipped)
        => _logger.LogInformation("[seed] 注册 {Registered}/执行 {Executed}/失败 {Failed}/跳过 {Skipped}", registered, executed, failed, skipped);
}

/// <summary>
/// 命令式种子执行内存快照(G32):Registered=注册总数;Executed=成功执行数;Failed=失败数;
/// SkippedByToggle=未执行数(开关关闭时=全部注册数;fail-fast 中断时=其后未跑的剩余数);Names=按执行序的类名。
/// </summary>
public sealed record StartupSeedStatus(int Registered, int Executed, int Failed, int SkippedByToggle, IReadOnlyList<string> Names)
{
    /// <summary>零快照(尚未执行过任何一轮)。</summary>
    public static readonly StartupSeedStatus Empty = new(0, 0, 0, 0, []);
}