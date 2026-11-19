namespace AdminCore.Core.Option;

/// <summary>
/// 数据库设置选项(批 Q 6.8,节名=类名约定):配置节 <c>DbSettings</c>。
/// 消费方经 <c>IOptions&lt;DbSettingsOptions&gt;</c>;既有散读点(AddSqlSugar)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class DbSettingsOptions
{
    /// <summary>CodeFirst 建表模式:缺省 true=增量(只处理 [IncreTable] 实体)。</summary>
    public bool EnableIncreTable { get; set; } = true;

    /// <summary>慢 SQL 检测阈值(秒);&lt;=0 关闭(批 J 5.5)。</summary>
    public int SlowSqlSeconds { get; set; }

    /// <summary>库表差异日志开关(批 X 5.4):true 时每次写操作经 OnDiffLogEvent 落 sys_log_diff。</summary>
    public bool EnableDiffLog { get; set; }
}
