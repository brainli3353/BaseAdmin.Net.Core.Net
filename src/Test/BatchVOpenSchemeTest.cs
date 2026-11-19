using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 V(部署全量验证抓出的开放接口多 scheme 认证回归)测试:
/// [Authorize(AuthenticationSchemes = "A,B,C")] 端点上 PermissionMiddleware 须逐单 scheme 尝试认证
/// (.NET 10 的 AuthenticateAsync 只接受单 scheme,整体字符串传入抛 InvalidOperationException)。
/// 语义 = 按声明顺序尝试,首个成功即停;全失败 → 401 + 细分消息透传。
/// </summary>
public class BatchVOpenSchemeTest
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private sealed class FakeAuthService : IAuthenticationService
    {
        private readonly HashSet<string> _succeedSchemes;
        private readonly string? _failMsg;
        public List<string> AttemptedSchemes { get; } = new();

        public FakeAuthService(HashSet<string> succeedSchemes, string? failMsg = null)
        {
            _succeedSchemes = succeedSchemes;
            _failMsg = failMsg;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme)
        {
            AttemptedSchemes.Add(scheme);
            if (_succeedSchemes.Contains(scheme))
            {
                var identity = new ClaimsIdentity(new[]
                {
                    new Claim("AccountType", "999"),
                    new Claim("UserId", "1"),
                }, "Fake");
                var principal = new ClaimsPrincipal(identity);
                // 与生产 AuthenticationService 同行为:认证成功即设置 context.User(中间件后续判定依赖)
                context.User = principal;
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(), scheme)));
            }

            if (_failMsg is not null)
            {
                context.Items[AdminCore.Web.Core.Auth.SignatureAuth.SignatureAuthenticationDefaults.AuthenticateFailMsgKey] = _failMsg;
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal? principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class RecordingNext
    {
        public bool Called;
        public Task Invoke(HttpContext ctx) { Called = true; return Task.CompletedTask; }
    }

    private sealed class FixedHttpContextAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        private readonly HttpContext _context;
        public FixedHttpContextAccessor(HttpContext context) => _context = context;
        public HttpContext? HttpContext { get => _context; set { } }
    }

    private static (ServiceProvider provider, ISqlSugarClient db) BuildProvider(HttpContext context)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "t13v_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "t13v-test",
                ["Jwt:Audience"] = "t13v-test-api",
                ["Cryptogram:Key"] = "dDEzbl90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM=",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar(config);
        // SetTenantFilter 经请求作用域解析 UserManager(超管判定);固定 accessor 指向本测试 context
        services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new FixedHttpContextAccessor(context));
        services.AddSingleton<AdminCore.Core.Security.UserManager>(
            sp => new AdminCore.Core.Security.UserManager(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ISqlSugarClient>());
    }

    private static (HttpContext context, RecordingNext next, FakeAuthService auth) BuildContext(
        string schemes, FakeAuthService auth)
    {
        var next = new RecordingNext();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(auth)
                .BuildServiceProvider()
        };
        context.Response.Body = new System.IO.MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = "/api/openDemo/hello";
        context.Request.QueryString = new QueryString();

        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new object[]
            {
                new AdminCore.Core.Attributes.OpenApiAttribute(),
                new AuthorizeAttribute { AuthenticationSchemes = schemes },
            }),
            "test");
        context.SetEndpoint(endpoint);
        return (context, next, auth);
    }

    /// <summary>多 scheme 声明:首个 scheme 即成功 → 只尝试一个(且是单 scheme 名,绝不带逗号)。</summary>
    [Fact]
    public async Task T1_FirstSchemeWins_SingleSchemeOnly()
    {
        var auth = new FakeAuthService(new HashSet<string> { "SigA" });
        var (context, next, _) = BuildContext("SigA,SigB,SigC", auth);
        var (provider, db) = BuildProvider(context);

        var mw = new AdminCore.Web.Core.Auth.PermissionMiddleware(next.Invoke, db, provider.GetRequiredService<IServiceScopeFactory>());
        await mw.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(new[] { "SigA" }, auth.AttemptedSchemes);
        Assert.DoesNotContain(',', string.Join("|", auth.AttemptedSchemes));
        await provider.DisposeAsync();
    }

    /// <summary>首个失败、第二个成功 → 按声明顺序尝试两个后停(首个成功即停,不试第三个)。</summary>
    [Fact]
    public async Task T2_SecondSucceeds_TriesInOrder()
    {
        var auth = new FakeAuthService(new HashSet<string> { "SigB" });
        var (context, next, _) = BuildContext("SigA,SigB,SigC", auth);
        var (provider, db) = BuildProvider(context);

        var mw = new AdminCore.Web.Core.Auth.PermissionMiddleware(next.Invoke, db, provider.GetRequiredService<IServiceScopeFactory>());
        await mw.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(new[] { "SigA", "SigB" }, auth.AttemptedSchemes);
        await provider.DisposeAsync();
    }

    /// <summary>全部 scheme 失败 → 401,细分消息透传(Items failMsg),next 不执行。</summary>
    [Fact]
    public async Task T3_AllFail_401WithFailMsg()
    {
        var auth = new FakeAuthService(new HashSet<string>(), failMsg: "签名校验失败:签名不匹配");
        var (context, next, _) = BuildContext("SigA,SigB", auth);
        var (provider, db) = BuildProvider(context);

        var mw = new AdminCore.Web.Core.Auth.PermissionMiddleware(next.Invoke, db, provider.GetRequiredService<IServiceScopeFactory>());
        await mw.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(401, context.Response.StatusCode);
        var ms = (System.IO.MemoryStream)context.Response.Body;
        ms.Position = 0;
        var text = new System.Text.UTF8Encoding(false).GetString(ms.ToArray());
        Assert.Contains("签名校验失败:签名不匹配", text);
        Assert.Equal(new[] { "SigA", "SigB" }, auth.AttemptedSchemes);
        await provider.DisposeAsync();
    }
}
