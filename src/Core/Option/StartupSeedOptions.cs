namespace AdminCore.Core.Option;

/// <summary>
/// 命令式启动种子选项(G32,节名=类名约定):配置节 <c>StartupSeed</c>。
/// 消费方 <see cref="AdminCore.Core.Services.Seed.StartupSeedRunner"/> 经 <c>IOptionsMonitor</c> 读取。
/// </summary>
public sealed class StartupSeedOptions
{
    /// <summary>
    /// 总开关,缺省 true。false ⇒ 不执行任何命令式种子,但**响亮告警列出未执行清单**
    /// (G32 核心口径:静默面变噪音面——"没跑"必须可从日志判定)。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 失败策略,缺省 false = fail-fast(首颗失败即抛断,后续种子不跑);
    /// true = 记录失败后继续执行剩余种子(汇总 Failed 计数)。
    /// </summary>
    public bool ContinueOnFailure { get; set; }
}