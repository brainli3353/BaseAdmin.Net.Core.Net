using AdminCore.Core;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminCore.Web.Core.Auth.ApiKeyAuth
{
    /// <summary>
    /// 开放接口 API Key 认证 Handler(批 E 1.7):Header X-API-KEY 值 = sys_open_access.AccessSecret。
    /// </summary>
    /// <remarks>
    /// 移植参照 AdminNetPro ApiKeyAuthenticationHandler(流程见 docs/研究/e1_refer_research.md §3):
    /// 查身份(按 Secret)→ 绑定用户有效性 → ClaimsPrincipal 与 JWT 同构。
    /// 差异/加固披露:① 参照 TokenVersion 写死 "1"(改密/踢出后旧 Key 语义失真)→ 目标取绑定用户实时
    /// TokenVersion(经 GetIdentityBySecretAsync 实时查用户行,同签名 scheme 口径);
    /// ② 参照 .Includes(BindUser) 导航查询 → 目标无导航,服务层两表查询(直查不缓存——密钥查询为
    /// 低频兜底路径,缓存失效协调成本不划算);③ 失败消息透传键复用 Signature 的
    /// AuthenticateFailMsgKey(参照同口径,单一出口);④ 401 ApiResult 体同签名 Handler 口径。
    /// </remarks>
    public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<ApiKeyAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var apiKey = Request.Headers["X-API-KEY"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return await FailAsync("API-KEY 不能为空");
            }

            var openAccessService = Context.RequestServices.GetRequiredService<SysOpenAccessService>();
            var identity = await openAccessService.GetIdentityBySecretAsync(apiKey);
            if (identity is null)
            {
                return await FailAsync("API-KEY 无效");
            }
            if (!identity.BindUserValid)
            {
                return await FailAsync("绑定用户不存在或已停用");
            }

            var claims = new ClaimsIdentity(ApiKeyAuthenticationDefaults.AuthenticationScheme);
            claims.AddClaims(new[]
            {
                new Claim(JwtClaimNames.UserId, identity.BindUserId.ToString()),
                new Claim(JwtClaimNames.TenantId, identity.BindTenantId.ToString()),
            });
            if (!string.IsNullOrEmpty(identity.Account))
            {
                claims.AddClaim(new Claim(JwtClaimNames.Account, identity.Account));
            }
            if (!string.IsNullOrEmpty(identity.RealName))
            {
                claims.AddClaim(new Claim(JwtClaimNames.RealName, identity.RealName));
            }
            claims.AddClaim(new Claim(JwtClaimNames.AccountType, identity.AccountType.ToString()));
            claims.AddClaim(new Claim(JwtClaimNames.TokenVersion, identity.TokenVersion.ToString()));
            if (identity.OrgId is long orgId)
            {
                claims.AddClaim(new Claim(JwtClaimNames.OrgId, orgId.ToString()));
            }

            return AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(claims), Scheme.Name));
        }

        /// <summary>401 出口:ApiResult 体 + 细分失败消息透传(复用 Signature 的 Items 键,单一出口)。</summary>
        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var failMsg = Context.Items.TryGetValue(
                SignatureAuth.SignatureAuthenticationDefaults.AuthenticateFailMsgKey, out var msg)
                ? msg as string
                : null;
            await Context.Response.WriteAsJsonAsync(
                ApiResult.Fail(string.IsNullOrEmpty(failMsg) ? "开放接口 API Key 认证失败" : failMsg), JsonOptions);
        }

        private async Task<AuthenticateResult> FailAsync(string message)
        {
            Context.Items[SignatureAuth.SignatureAuthenticationDefaults.AuthenticateFailMsgKey] = message;
            return AuthenticateResult.Fail(message);
        }

        /// <summary>统一响应序列化口径(与 PermissionMiddleware/异常中间件同构:camelCase + 中文不转义)。</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
