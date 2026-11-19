namespace AdminCore.Core.Option;

/// <summary>
/// 密码策略配置选项(批 Q 6.8,节名=类名约定):配置节 <c>SysPassword</c>。
/// 消费方经 <c>IOptions&lt;SysPasswordOptions&gt;</c>;既有散读点(SysUserService)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class SysPasswordOptions
{
    /// <summary>密码强度正则;配置空时消费点回退内置缺省正则(8-20 位含大小写+数字+特殊字符)。</summary>
    public string? StrengthExpression { get; set; }
}
