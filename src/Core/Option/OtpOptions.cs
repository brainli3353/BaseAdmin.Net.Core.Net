namespace AdminCore.Core.Option;

/// <summary>
/// OTP 配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Otp</c>。
/// 消费方经 <c>IOptions&lt;OtpOptions&gt;</c>。
/// </summary>
public sealed class OtpOptions
{
    /// <summary>TOTP 账号前缀(OTP 账号 = {Issuer}:{Account})。</summary>
    public string? Issuer { get; set; }
}
