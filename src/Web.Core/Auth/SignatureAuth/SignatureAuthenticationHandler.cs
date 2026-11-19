using AdminCore.Core.Cache;
using AdminCore.Core;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using AdminCore.Core.Services.OpenAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminCore.Web.Core.Auth.SignatureAuth
{
    /// <summary>
    /// 开放接口签名认证 Handler(批 E 1.6):HMAC-SHA256 四头(accessKey/timestamp/nonce/sign)验签。
    /// </summary>
    /// <remarks>
    /// 移植参照 AdminNetPro SignatureAuthenticationHandler(8 文件事件体系简化为单 Handler——
    /// 参照事件回调服务 Furion「Core 框架 + 业务实现」分层,本仓 Web.Core→Core 单向依赖可直接注入
    /// SysOpenAccessService,无分层解耦需求;完整流程/字面量/行号见 docs/研究/e1_refer_research.md §2)。
    /// 验签链:四头齐 → 时间戳窗口(5min,秒/毫秒自判)→ 身份取数(缓存 + 绑定用户实时行)→
    /// HMAC-SHA256 恒定时间验签(OpenAccessSignHelper)→ nonce 防重放(IncrementAsync 原子自增,
    /// 参照 check-then-set 有竞态,目标改原子口径)→ ClaimsPrincipal 与 JWT 同构(绑定用户身份)。
    /// 加固/差异披露:① 验签 FixedTimeEquals(参照 !=);② 无头请求 Fail 而非 NoResult(参照同口径——
    /// 多 scheme Or 语义下任一 scheme 有头即认证,无头全部 Fail → 401,与参照行为一致);
    /// ③ 业务依赖经 Context.RequestServices 请求作用域解析(Handler 构造签名固定,先例 JwtBearerEvents
    /// OnTokenValidated 内解析 JwtAuthHandler 同模式);④ Challenge 覆写 401 ApiResult 体(参照靠 Furion
    /// 统一响应出口,目标无——细分失败消息经 Items[AuthenticateFailMsgKey] 透传)。
    /// </remarks>
    public sealed class SignatureAuthenticationHandler : AuthenticationHandler<SignatureAuthenticationOptions>
    {
        public SignatureAuthenticationHandler(
            IOptionsMonitor<SignatureAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var accessKey = Request.Headers["accessKey"].FirstOrDefault();
            var timestampStr = Request.Headers["timestamp"].FirstOrDefault();
            var nonce = Request.Headers["nonce"].FirstOrDefault();
            var sign = Request.Headers["sign"].FirstOrDefault();

            if (string.IsNullOrEmpty(accessKey))
            {
                return await FailAsync("accessKey 不能为空");
            }
            if (string.IsNullOrEmpty(timestampStr))
            {
                return await FailAsync("timestamp 不能为空");
            }
            if (string.IsNullOrEmpty(nonce))
            {
                return await FailAsync("nonce 不能为空");
            }
            if (string.IsNullOrEmpty(sign))
            {
                return await FailAsync("sign 不能为空");
            }
            if (!OpenAccessSignHelper.InWindow(timestampStr, Options.AllowedDateDrift))
            {
                return await FailAsync(
                    !long.TryParse(timestampStr, out _)
                        ? "timestamp 值不合法"
                        : "timestamp 值已超过允许的偏差范围");
            }

            var services = Context.RequestServices;
            var openAccessService = services.GetRequiredService<SysOpenAccessService>();
            var identity = await openAccessService.GetIdentityAsync(accessKey);
            if (identity is null)
            {
                return await FailAsync("accessKey 无效");
            }
            if (!identity.BindUserValid)
            {
                return await FailAsync("绑定用户不存在或已停用");
            }

            if (!OpenAccessSignHelper.Verify(
                    identity.AccessSecret, sign, Request.Method, Request.Path.Value ?? string.Empty,
                    accessKey, timestampStr, nonce))
            {
                return await FailAsync("sign 无效的签名");
            }

            // nonce 防重放:原子自增(首个 1 通过,>1 重放拒绝);TTL = 2× 窗口(参照口径)
            var cache = services.GetRequiredService<ICacheService>();
            var nonceKey = CacheConst.KeyOpenAccessNonce + accessKey + "|" + timestampStr + "|" + nonce;
            var counter = await cache.IncrementAsync(nonceKey, Options.AllowedDateDrift.Multiply(2));
            if (counter > 1)
            {
                return await FailAsync("重复的请求");
            }

            // Claims 与 JWT 同构(参照 §2.5 口径;目标消费链:UserId/Account → AOP 审计,TenantId → 租户行级过滤,
            // TokenVersion/AccountType/OrgId → 机构数据范围与超管判定;OrgName/OrgType 目标实体缺字段省略,R2/R3)
            var identityClaims = new ClaimsIdentity(SignatureAuthenticationDefaults.AuthenticationScheme);
            identityClaims.AddClaims(BuildClaims(identity));
            return AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identityClaims), Scheme.Name));
        }

        /// <summary>401 出口:ApiResult 体 + 细分失败消息透传(参照 AdminNETResultProvider 出口的替代)。</summary>
        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var failMsg = Context.Items.TryGetValue(SignatureAuthenticationDefaults.AuthenticateFailMsgKey, out var msg)
                ? msg as string
                : null;
            await Context.Response.WriteAsJsonAsync(
                ApiResult.Fail(string.IsNullOrEmpty(failMsg) ? "开放接口签名认证失败" : failMsg), JsonOptions);
        }

        private async Task<AuthenticateResult> FailAsync(string message)
        {
            Context.Items[SignatureAuthenticationDefaults.AuthenticateFailMsgKey] = message;
            return AuthenticateResult.Fail(message);
        }

        private static IEnumerable<Claim> BuildClaims(
            AdminCore.Core.Services.OpenAccess.Dto.OpenAccessIdentity identity)
        {
            yield return new Claim(JwtClaimNames.UserId, identity.BindUserId.ToString());
            yield return new Claim(JwtClaimNames.TenantId, identity.BindTenantId.ToString());
            if (!string.IsNullOrEmpty(identity.Account))
            {
                yield return new Claim(JwtClaimNames.Account, identity.Account);
            }
            if (!string.IsNullOrEmpty(identity.RealName))
            {
                yield return new Claim(JwtClaimNames.RealName, identity.RealName);
            }
            yield return new Claim(JwtClaimNames.AccountType, identity.AccountType.ToString());
            yield return new Claim(JwtClaimNames.TokenVersion, identity.TokenVersion.ToString());
            if (identity.OrgId is long orgId)
            {
                yield return new Claim(JwtClaimNames.OrgId, orgId.ToString());
            }
        }

        /// <summary>统一响应序列化口径(与 PermissionMiddleware/异常中间件同构:camelCase + 中文不转义)。</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
