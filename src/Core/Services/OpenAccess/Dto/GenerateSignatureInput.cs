namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 签名预览入参 {accessKey, accessSecret, method, url, timestamp?, nonce?}(JSON camelCase,管理端调试用)。
    /// Timestamp=0 时服务端填当前秒级时间戳;Nonce 缺省服务端生成 Guid(N)。
    /// 口径与 SignatureAuthenticationHandler 验签完全一致(共用 OpenAccessSignHelper)。
    /// </summary>
    public sealed record GenerateSignatureInput(string AccessKey, string AccessSecret, string Method, string Url, long Timestamp = 0, string? Nonce = null);
}
