using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AdminCore.Web.Core.Auth.ApiKeyAuth;
using AdminCore.Web.Core.Auth.SignatureAuth;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 开放接口示例端点(批 E 6.10;外部系统对接模板):
    /// hello = 纯开放接口认证(Signature/ApiKey);helloOrJwt = 开放接口或 JWT 双通道(Bearer/Signature/ApiKey)。
    /// 请求身份为绑定用户(ClaimsPrincipal 与 JWT 同构)——AOP 审计/租户行级过滤/机构数据范围对开放接口同口径生效。
    /// 下游业务开放接口照此标注 [OpenApi] + [Authorize(AuthenticationSchemes = ...)]。
    /// </summary>
    [ApiController]
    [Route("api/openDemo")]
    public class OpenApiDemoController : Controller
    {
        private readonly UserManager _userManager;

        public OpenApiDemoController(UserManager userManager)
        {
            _userManager = userManager;
        }

        /// <summary>
        /// 签名示例:四头 accessKey/timestamp/nonce/sign(HMAC-SHA256,sign 口径 OpenAccessSignHelper);
        /// 或 API Key 示例:单头 X-API-KEY = AccessSecret。
        /// </summary>
        [OpenApi]
        [Authorize(AuthenticationSchemes = SignatureAuthenticationDefaults.AuthenticationScheme + "," + ApiKeyAuthenticationDefaults.AuthenticationScheme)]
        [HttpGet("hello")]
        public IActionResult Hello()
        {
            return Ok(ApiResult<CallerIdentity>.Success(new CallerIdentity(
                _userManager.UserId, _userManager.Account, _userManager.RealName, _userManager.TenantId)));
        }

        /// <summary>开放接口或 JWT 双通道示例:内部系统可用既有 JWT 访问,外部系统走签名/Key。</summary>
        [OpenApi]
        [Authorize(AuthenticationSchemes = "Bearer," + SignatureAuthenticationDefaults.AuthenticationScheme + "," + ApiKeyAuthenticationDefaults.AuthenticationScheme)]
        [HttpGet("helloOrJwt")]
        public IActionResult HelloOrJwt()
        {
            return Ok(ApiResult<CallerIdentity>.Success(new CallerIdentity(
                _userManager.UserId, _userManager.Account, _userManager.RealName, _userManager.TenantId)));
        }
    }

    /// <summary>调用方身份(开放接口 = 绑定用户身份)。</summary>
    public sealed record CallerIdentity(long? UserId, string? Account, string? RealName, long? TenantId);
}
