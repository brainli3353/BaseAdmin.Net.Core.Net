using Microsoft.AspNetCore.Authentication;

namespace AdminCore.Web.Core.Auth.SignatureAuth
{
    /// <summary>
    /// 开放接口签名认证 scheme 选项(批 E 1.6)。
    /// </summary>
    public class SignatureAuthenticationOptions : AuthenticationSchemeOptions
    {
        /// <summary>
        /// 请求时间戳允许的偏差窗口(参照默认 5min):|请求时间-当前时间| ≤ 本值通过;
        /// nonce 防重放键 TTL 取 2× 本值(略大于窗口,保证窗口内重放必被拦)。
        /// </summary>
        public TimeSpan AllowedDateDrift { get; set; } = TimeSpan.FromMinutes(5);
    }
}
