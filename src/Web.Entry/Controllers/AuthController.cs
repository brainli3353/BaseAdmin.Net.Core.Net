using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Auth.Dto;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Security;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 认证 API(task_007 两阶段登录):一阶段初验 / 二阶段 OTP 换取 / 当前用户信息 / 登出。
    /// 失败路径一律 401 + ApiResult 体(契约口径:HTTP 语义与业务码同时表达);成功 200 + ApiResult。
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;
        private readonly UserManager _userManager; // 批 D 1.14:当前用户身份经门面收敛
        private readonly IMenuPermService _menuPermService; // 前端 v-perm 按钮显隐同源于此(契约「一处配置两端生效」)
        private readonly ILocalizationService _localization; // 批 AQ:登录失败文案按请求语言渲染

        public AuthController(AuthService authService, UserManager userManager, IMenuPermService menuPermService, ILocalizationService localization)
        {
            _authService = authService;
            _userManager = userManager;
            _menuPermService = menuPermService;
            _localization = localization;
        }

        /// <summary>登录失败响应文案(批 AQ):有稳定错误码则按请求语言渲染;否则回退原有消息(兼容存量)。</summary>
        private string ErrorText(LoginResult result)
            => string.IsNullOrEmpty(result.ErrorCode) ? result.Error ?? string.Empty : _localization.Get(result.ErrorCode);

        /// <summary>POST /api/auth/login:body {account, password} → 成功返 {challengeId, bindRequired, otpauthUri?, secret?};失败 401。</summary>
        // [AllowAnonymous]:契约 §2 默认拦截下的显式放行——登录入口必须先于认证可达
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginInput input)
        {
            var result = await _authService.LoginAsync(input.Account, input.Password);
            if (!result.Success)
            {
                return Unauthorized(ApiResult.Fail(ErrorText(result), 1, result.ErrorCode));
            }

            // 批 BB:OTP 全局开关"关"＝密码直登,服务直接签发令牌(LoginResult.Token 非空、Data=null)——
            // 与二阶段同一响应形态(TokenResult:响应头 + data=token);开关"开"＝挑战分支(Data 非空,data=LoginOutput)
            if (!string.IsNullOrEmpty(result.Token))
            {
                return TokenResult(result.Token, result.PasswordExpired, result.RefreshToken);
            }
            return Ok(ApiResult<LoginOutput>.Success(result.Data!));
        }

        /// <summary>POST /api/auth/login/otp:body {challengeId, code} → 成功返 {token};失败 401。</summary>
        // [AllowAnonymous]:两阶段登录的二阶段——尚未持有 JWT,必须显式放行
        [AllowAnonymous]
        [HttpPost("login/otp")]
        public async Task<IActionResult> LoginOtp([FromBody] LoginOtpInput input)
        {
            var result = await _authService.LoginOtpAsync(input.ChallengeId, input.Code);
            if (!result.Success)
            {
                return Unauthorized(ApiResult.Fail(ErrorText(result), 1, result.ErrorCode));
            }

            // 批 BB:令牌响应头透传与"关"直登分支统一收口 TokenResult(data=token 形状不变,旧客户端无感)
            return TokenResult(result.Token!, result.PasswordExpired, result.RefreshToken);
        }

        /// <summary>
        /// 批 BB:令牌签发成功响应统一形态(二阶段 + 关直登两入口共用):
        /// 批 M 1.11 X-Password-Expired / 批 N 6.4 X-Refresh-Token 头透传 + data=token(形状不变,旧客户端无感)。
        /// </summary>
        private IActionResult TokenResult(string token, bool passwordExpired, string? refreshToken)
        {
            // 批 M 1.11:密码到期标志经响应头 X-Password-Expired 透传(data=token 形状不变,旧客户端无感);
            // 前端据此引导强制改密页,服务端不拒登(锁死风险,见 g7 §5)
            if (passwordExpired)
            {
                Response.Headers["X-Password-Expired"] = "true";
            }

            // 批 N 6.4:refresh token(长时效双 JWT,参照 SysAuthService 口径)经响应头 X-Refresh-Token 透传
            // (data=token 形状不变;access 过期后前端切换 refresh 调接口——同签名密钥天然接受,见 g8)
            if (!string.IsNullOrEmpty(refreshToken))
            {
                Response.Headers["X-Refresh-Token"] = refreshToken;
            }

            return Ok(ApiResult<string>.Success(token));
        }

        /// <summary>GET /api/auth/me:需有效 JWT → 返回当前用户 Claims(随 task_006 口径迁移至 ApiResult,与其他端点契约统一)。</summary>
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            // 批 D 1.14:身份经 UserManager 门面;userId 随门面改返数值(long?,对齐框架 Id 数值口径,原 string 为历史残留)
            // 前端适配扩展:accountType(999 超管凭证,前端 v-perm 据此穿透)+ permissions(按钮权限集,
            // 与 MenuPermFilter 同源同缓存 KeyUserApiPerm——「一处配置两端生效」:菜单授权变更经
            // InvalidateUser/Role/Menu 失效缓存后,本端点即时反映,无需前端自行拉菜单树反推)
            List<string> permissions = new();
            if (_userManager.UserId is { } userId && !_userManager.IsSuperAdmin)
            {
                permissions = (await _menuPermService.GetPermissionsAsync(userId)).ToList();
            }

            return Ok(ApiResult<object>.Success(new
            {
                userId = _userManager.UserId,
                account = _userManager.Account,
                realName = _userManager.RealName,
                accountType = _userManager.AccountType,
                permissions
            }));
        }

        /// <summary>POST /api/auth/logout:当前 token 写入黑名单(TTL=剩余有效期)→ 旧 token 此后被 JwtAuthHandler 拒绝。</summary>
        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            // 服务自行剥 Bearer 前缀(与 JwtAuthHandler 黑名单 key 口径一致)
            await _authService.LogoutAsync(Request.Headers.Authorization.ToString());
            return Ok(ApiResult.Success());
        }
    }
}
