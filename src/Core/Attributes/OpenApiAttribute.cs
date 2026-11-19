using System;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 开放接口端点标记(批 E 6.10):标注在开放接口 Action 上,声明该端点以开放接口身份认证
    /// (Signature/ApiKey scheme,而非 JWT)——须与 [Authorize(AuthenticationSchemes = "...")] 共标。
    /// </summary>
    /// <remarks>
    /// 判定链(PermissionMiddleware 开放接口分支,批 E):[OpenApi] 端点先触发 [Authorize] 声明的
    /// scheme 认证(UseAuthentication 只跑默认 JwtBearer scheme,自定义 scheme 若等到 UseAuthorization
    /// 才触发,租户/机构行级过滤已错过设定时机)→ 认证成功继续租户/机构过滤 + 放行;
    /// 认证失败 → 401(细分消息经 Items[SignatureAuthenticateFailMsg] 透传)。
    /// [OpenApi] 无 [Authorize(AuthenticationSchemes)] → 401(防裸开放接口配置错误)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class OpenApiAttribute : Attribute
    {
    }
}
