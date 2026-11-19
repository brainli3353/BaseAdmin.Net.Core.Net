namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 管理员重置他人密码请求 {userId, newPassword}(批 A 1.11;JSON camelCase)。
    /// 口径:无需旧密码(管理员代操作);新密码经强度(开关)校验;重置成功触发 TokenVersion++
    /// 强制下线闭环 + 清空该账号密码错误计数缓存(解锁锁定窗口)。
    /// </summary>
    public sealed record ResetPwdInput(long UserId, string? NewPassword);
}
