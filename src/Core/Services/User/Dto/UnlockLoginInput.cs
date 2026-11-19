namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 解锁登录请求 {account}(批 A 2.1;JSON camelCase)。
    /// 口径:清空该账号的密码错误计数 + OTP 失败计数缓存(30min 锁定窗口即刻解除),
    /// 用于管理员为被锁定账号人工解锁;不触发 TokenVersion++(解锁不改变已登录会话)。
    /// </summary>
    public sealed record UnlockLoginInput(string? Account);
}
