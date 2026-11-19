namespace AdminCore.Core.Services.User.Dto
{
    /// <summary>
    /// 修改本人密码请求 {oldPassword, newPassword}(批 A 1.11;JSON camelCase)。
    /// 口径:旧密码须校验通过;新密码经强度(开关)/历史重复(开关)校验;改密成功触发
    /// TokenVersion++ 强制下线闭环(旧 Token 即刻作废)。
    /// </summary>
    public sealed record UpdatePasswordInput(string? OldPassword, string? NewPassword);
}
