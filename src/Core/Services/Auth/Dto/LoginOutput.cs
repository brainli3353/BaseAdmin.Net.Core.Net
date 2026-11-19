namespace AdminCore.Core.Services.Auth.Dto
{
    /// <summary>
    /// 一阶段登录成功响应:challengeId(二阶段凭据)+ bindRequired(是否未绑定);
    /// 未绑定时附带 otpauthUri(扫码内容)与 secret(手动录入兜底)。
    /// </summary>
    public sealed record LoginOutput(string ChallengeId, bool BindRequired, string? OtpauthUri, string? Secret);
}
