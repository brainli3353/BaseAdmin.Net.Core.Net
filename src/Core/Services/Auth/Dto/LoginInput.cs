namespace AdminCore.Core.Services.Auth.Dto
{
    /// <summary>一阶段登录请求 {account, password}(JSON 传输为 camelCase)。</summary>
    public sealed record LoginInput(string? Account, string? Password);
}
