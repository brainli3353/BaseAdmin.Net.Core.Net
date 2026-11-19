namespace AdminCore.Core.Services.Auth.Dto
{
    /// <summary>
    /// 二阶段 OTP 换取请求 {challengeId, code}。
    /// challengeId 为雪花 Id——JSON 字段用字符串传输(JS number 安全位宽 53 位,雪花 Id 超出,防前端精度丢失)。
    /// </summary>
    public sealed record LoginOtpInput(string? ChallengeId, string? Code);
}
