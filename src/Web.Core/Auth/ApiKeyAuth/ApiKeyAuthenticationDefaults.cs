namespace AdminCore.Web.Core.Auth.ApiKeyAuth
{
    /// <summary>
    /// 开放接口 API Key 认证 scheme 常量(批 E 1.7)。
    /// </summary>
    public static class ApiKeyAuthenticationDefaults
    {
        /// <summary>scheme 名(参照 AdminNetPro 同字面量;[Authorize(AuthenticationSchemes=...)] 引用)。</summary>
        public const string AuthenticationScheme = "ApiKey";
    }
}
