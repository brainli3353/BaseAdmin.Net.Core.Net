namespace AdminCore.Core.Option;

/// <summary>
/// 对称加密配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Cryptogram</c>。
/// 消费方经 <c>IOptions&lt;CryptogramOptions&gt;</c>;既有散读点(CryptogramHelper)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class CryptogramOptions
{
    /// <summary>加密密钥(Base64,解码后须恰 32 字节)。</summary>
    public string? Key { get; set; }
}
