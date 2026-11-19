namespace AdminCore.Web.Core.Auth.SignatureAuth
{
    /// <summary>
    /// 开放接口签名认证 scheme 常量(批 E 1.6)。
    /// </summary>
    public static class SignatureAuthenticationDefaults
    {
        /// <summary>scheme 名(参照 AdminNetPro 同字面量;[Authorize(AuthenticationSchemes=...)] 引用)。</summary>
        public const string AuthenticationScheme = "Signature";

        /// <summary>
        /// 认证失败细分消息的 HttpContext.Items 键(参照同字面量):
        /// Handler 验签失败时写入("accessKey 无效"/"sign 无效的签名"/"重复的请求"等),
        /// PermissionMiddleware 开放接口 401 分支与 Handler HandleChallengeAsync 读取透传,
        /// 排障时可见具体失败原因(参照靠 Furion AdminNETResultProvider 出口,目标仓无此机制)。
        /// </summary>
        public const string AuthenticateFailMsgKey = "SignatureAuthenticateFailMsg";
    }
}
