using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Security;
using AdminCore.Web.Core.Auth;
using AdminCore.Web.Core.Auth.SignatureAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BV openAccess 管线级回归(D1):[OpenApi] 自定义 scheme 认证成功后必须回填 context.User。
    /// 根因(.NET 10.0.12 实锤,活体 5090 + 本地 5092 双复现):IAuthenticationService.AuthenticateAsync
    /// 成功后不回填 context.User(UseAuthentication 只代默认 scheme 回填)——[OpenApi] 端点在
    /// PermissionMiddleware 手动触发 Signature/ApiKey scheme,若不显式回填,IsAuthenticated 门禁恒 401,
    /// 开放接口签名/ApiKey 认证完全不可用(修复前活体:四头签名正确仍 401,principal Succeeded=true 而 User 匿名)。
    /// 批 E 头注披露的「Handler 级集成未覆盖」缝隙正是本缺陷藏身处——本件补管线级回归。
    /// 装配口径:stub IAuthenticationService(脚本化结果)+ 真实 scopeFactory(UserManager 纯 claim 门面)+
    /// 默认租户 principal(TenantDbRegistry.EnsureConnectionAsync 默认租户短路主库,全程零 DB)。
    /// </summary>
    public class BatchBVOpenAccessTest
    {
        private static DefaultHttpContext MakeContext(
            ServiceCollection services, params (AuthenticateResult Result, string? FailMessage)[] results)
        {
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddScoped<UserManager>();
            services.AddSingleton<IAuthenticationService>(new FakeAuthService(results));

            var sp = services.BuildServiceProvider();
            var context = new DefaultHttpContext
            {
                RequestServices = sp
            };
            // .NET 10 实锤:DefaultHttpContext.Response.Body 默认 NullStream(写入静默丢弃)——显式给 MemoryStream 才能断言响应体
            context.Response.Body = new MemoryStream();
            context.Request.Method = "GET";
            context.Request.Path = "/api/openDemo/hello";
            // .NET 10:Endpoint 构造无 RouteModel(已移除),第二参直接 metadata 集
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new object[] { new OpenApiAttribute(), new AuthorizeAttribute { AuthenticationSchemes = "Signature,ApiKey" } }),
                "bv-probe"));
            return context;
        }

        private static ClaimsPrincipal MakePrincipal(string accountType = "999", string authScheme = "Signature")
        {
            // 与 SignatureAuthenticationHandler.BuildClaims 同构:authType=scheme 名 + JwtClaimNames claim 集
            var identity = new ClaimsIdentity(authScheme);
            identity.AddClaims(new[]
            {
                new Claim(JwtClaimNames.UserId, "841915844415557"),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString()),
                new Claim(JwtClaimNames.Account, "admin"),
                new Claim(JwtClaimNames.AccountType, accountType),
            });
            return new ClaimsPrincipal(identity);
        }

        /// <summary>D1 回归核心:scheme 认证成功后 middleware 必须回填 context.User 并放行(next 被调)。</summary>
        [Fact]
        public async Task OpenApi端点_签名认证成功后回填User放行()
        {
            var context = MakeContext(new ServiceCollection(),
                (AuthenticateResult.Success(new AuthenticationTicket(MakePrincipal(), "Signature")), (string?)null));

            var nextCalled = false;
            var mw = new PermissionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, null!, context.RequestServices.GetRequiredService<IServiceScopeFactory>());
            await mw.InvokeAsync(context);

            Assert.True(nextCalled, "认证成功应放行(next 被调);恒 401 即 D1 复发");
            Assert.True(context.User.Identity?.IsAuthenticated == true, "回填后 Identity.IsAuthenticated 应为 true");
            Assert.Equal("admin", context.User.FindFirst(JwtClaimNames.Account)?.Value);
            Assert.Equal("Signature", context.User.Identity!.AuthenticationType);
        }

        /// <summary>多 scheme 顺序语义:首个 NoResult 不回填,第二个 Succeeded 回填其 principal。</summary>
        [Fact]
        public async Task OpenApi端点_多scheme顺序_首个未认证第二个成功()
        {
            // principal 内部 authType=scheme 名(与真实 handler 同构:ClaimsIdentity(scheme));ticket 的
            // authenticationScheme 参数不进 User(框架固有语义:回填的是 ticket.Principal 本身)
            var principal = MakePrincipal(authScheme: "ApiKey");
            var context = MakeContext(new ServiceCollection(),
                (AuthenticateResult.NoResult(), (string?)null),
                (AuthenticateResult.Success(new AuthenticationTicket(principal, "ApiKey")), (string?)null));

            var nextCalled = false;
            var mw = new PermissionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, null!, context.RequestServices.GetRequiredService<IServiceScopeFactory>());
            await mw.InvokeAsync(context);

            Assert.True(nextCalled);
            // 回填的应是第二个 scheme 的 principal(首个 NoResult 不回填)
            Assert.Equal("ApiKey", context.User.Identity!.AuthenticationType);
        }

        /// <summary>401 分支不变量:全 scheme Fail → 401 + 最后写入的细分消息透传(单一出口口径)+ next 不调。</summary>
        [Fact]
        public async Task OpenApi端点_全scheme失败_401透传最后细分消息()
        {
            var context = MakeContext(new ServiceCollection(),
                (AuthenticateResult.Fail("accessKey 无效"), "accessKey 无效"),
                (AuthenticateResult.Fail("API-KEY 不能为空"), "API-KEY 不能为空"));

            var nextCalled = false;
            var mw = new PermissionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, null!, context.RequestServices.GetRequiredService<IServiceScopeFactory>());
            await mw.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = new StreamReader(context.Response.Body).ReadToEnd();
            Assert.True(body.Contains("API-KEY 不能为空"), $"401 体应透传最后 scheme 的细分消息(单一出口口径),实际 body={body}");
        }

        /// <summary>缺 AuthenticationSchemes 的 [OpenApi] 端点 → 401(防裸开放接口,批 E 6.10 不变量)。</summary>
        [Fact]
        public async Task OpenApi端点_缺scheme声明_401()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddScoped<UserManager>();
            services.AddSingleton<IAuthenticationService>(new FakeAuthService((AuthenticateResult.NoResult(), (string?)null)));
            var sp = services.BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = sp };
            context.Response.Body = new MemoryStream();
            context.Request.Method = "GET";
            context.Request.Path = "/api/bare";
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new object[] { new OpenApiAttribute() }),
                "bv-bare"));

            var nextCalled = false;
            var mw = new PermissionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, null!, sp.GetRequiredService<IServiceScopeFactory>());
            await mw.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }

        /// <summary>IAuthenticationService stub:按脚本顺序出结果;Fail 时写细分消息到 Items(模拟真实 Handler FailAsync)。</summary>
        private sealed class FakeAuthService : IAuthenticationService
        {
            private readonly Queue<(AuthenticateResult, string?)> _results;
            public FakeAuthService(params (AuthenticateResult Result, string? FailMessage)[] results)
                => _results = new(results.Select(r => (r.Result, r.FailMessage)).ToArray());

            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            {
                var (r, msg) = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
                // 模拟真实 Handler FailAsync:失败细分消息写 Items(单一出口键,后写覆盖先写)
                if (msg is not null)
                {
                    context.Items[SignatureAuthenticationDefaults.AuthenticateFailMsgKey] = msg;
                }

                return Task.FromResult(r);
            }

            public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
                => Task.CompletedTask;

            public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
                => Task.CompletedTask;

            public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal user, AuthenticationProperties? properties)
                => Task.CompletedTask;

            public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
                => Task.CompletedTask;
        }
    }
}
