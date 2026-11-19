namespace AdminCore.Core.Services.Auth
{
    /// <summary>
    /// 登录挑战缓存载荷(sys_login_otp_challenge:{challengeId},TTL 300s,一次性消费):
    /// 一/二阶段握手的中间状态。SetupSecret 是本次挑战的校验密钥——未绑定用户为新生成密钥
    /// (首次校验通过即绑定),已绑定用户为库内密钥的解密值(仅校验、重绑不动)。
    /// FailTimes 为 OTP 失败计数(达 5 次挑战作废,防 OTP 暴力枚举)。
    /// </summary>
    public sealed record LoginOtpChallenge(long UserId, string Account, string SetupSecret, bool BindRequired, int FailTimes);
}
