namespace AdminCore.Core.Option;

/// <summary>
/// 雪花 Id 配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Yit</c>。
/// 消费方经 <c>IOptions&lt;YitOptions&gt;</c>;既有散读点(AddSqlSugar)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class YitOptions
{
    /// <summary>雪花 Id WorkerId(1-63)。</summary>
    public int WorkerId { get; set; }
}
