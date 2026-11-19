// AdminCore.Test — 批 AZ:操作日志补参照口径(RequestParam 请求参数 / ReturnResult 返回结果)。
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Logging;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Test.Infrastructure;
using AdminCore.Web.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using System.IO;
using System.Security.Claims;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 AZ(操作日志参照口径补全:请求参数/返回结果)测试。
    /// 覆盖:OpLogFilter 内存采集(方法参数 JSON + 返回对象 JSON,零 EnableBuffering)/
    /// 鉴权端点(Auth|Login、Auth|LoginOtp)参数豁免不落库/异常动作 ReturnResult 为空仍发事件/
    /// 不可序列化参数(Stream 等)占位替代/
    /// 操作日志 Page 投影排除两大字段 + GetDetailAsync 全字段(含两大字段)/不存在 → AppException。
    /// 探针:sys_log_op/sys_config 行 dsh_az_ 前缀,finally 清理(真实 PG)。
    /// </summary>
    public class BatchAZOpLogDetailTest
    {
        private const string OpAccount = "dsh_az_op_probe";
        private const long ProbeOpId = 99_000_000_004_01L;
        private const string SysOpLogCode = "sys_oplog";

        // ── 1. 参数/返回内存采集(零 EnableBuffering)─────────────────

        [Fact]
        public async Task 采集_方法参数与返回对象_落入两大字段()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            var existed = await scope.Db.Queryable<SysConfig>().AnyAsync(u => u.Code == SysOpLogCode);
            try
            {
                await EnsureOpLogConfigAsync(scope, "True");
                scope.FakeAccessor.HttpContext = UserContext(OpAccount);

                var filter = NewFilter(scope, publisher);
                var httpCtx = NewHttpContext(OpAccount, controller: "SysUser", action: "Page");
                // 方法参数(含 body 载体)与返回对象(统一响应载体)
                var args = new Dictionary<string, object?>
                {
                    ["input"] = new PageSysLogOpInput(OpAccount, null, null, null, null, 1, 10)
                };
                var result = new ObjectResult(new { code = 200, msg = "ok", data = new { total = 7, id = 42 } });
                await RunFilterAsync(filter, httpCtx, args, result);

                var calls = publisher.Calls.ToList();
                Assert.Single(calls);
                Assert.Equal("op_log", calls[0].Name);
                var op = Assert.IsType<SysLogOp>(calls[0].Payload);

                // 请求参数:方法参数 JSON(camelCase 口径),含入参字段
                Assert.NotNull(op.RequestParam);
                Assert.Contains("dsh_az_op_probe", op.RequestParam);
                Assert.Contains("account", op.RequestParam);

                // 返回结果:返回对象 JSON,含业务码与 data 载荷
                Assert.NotNull(op.ReturnResult);
                Assert.Contains("\"msg\":\"ok\"", op.ReturnResult);
                Assert.Contains("\"total\":7", op.ReturnResult);
                Assert.True(op.Success);
            }
            finally
            {
                await RestoreOpLogConfigAsync(scope, existed);
            }
        }

        // ── 2. 鉴权端点参数豁免(凭据纪律)──────────────────────────

        [Theory]
        [InlineData("Login")]
        [InlineData("LoginOtp")]
        public async Task 采集_鉴权登录端点_请求参数豁免不落库(string action)
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            var existed = await scope.Db.Queryable<SysConfig>().AnyAsync(u => u.Code == SysOpLogCode);
            try
            {
                await EnsureOpLogConfigAsync(scope, "True");
                scope.FakeAccessor.HttpContext = UserContext(OpAccount);

                var filter = NewFilter(scope, publisher);
                var httpCtx = NewHttpContext(OpAccount, controller: "Auth", action: action);
                // 登录入参含密码/动态码——必须豁免,不得入审计库
                var args = new Dictionary<string, object?>
                {
                    ["input"] = new { account = "admin", password = "P@ssw0rd", otp = "123456" }
                };
                var result = new ObjectResult(new { code = 200, msg = "ok", data = new { token = "xxx" } });
                await RunFilterAsync(filter, httpCtx, args, result);

                var calls = publisher.Calls.ToList();
                Assert.Single(calls);
                var op = Assert.IsType<SysLogOp>(calls[0].Payload);

                // 豁免:请求参数为 null,密码/动态码绝不落库
                Assert.Null(op.RequestParam);
                Assert.False(op.RequestParam is string s && s.Contains("P@ssw0rd", StringComparison.Ordinal));
                // 返回对象非敏感,照常采集
                Assert.NotNull(op.ReturnResult);
            }
            finally
            {
                await RestoreOpLogConfigAsync(scope, existed);
            }
        }

        // ── 3. 异常动作:仍发事件,ReturnResult 为空 ──────────────────

        [Fact]
        public async Task 采集_动作异常_仍发事件且返回结果为空()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            var existed = await scope.Db.Queryable<SysConfig>().AnyAsync(u => u.Code == SysOpLogCode);
            try
            {
                await EnsureOpLogConfigAsync(scope, "True");
                scope.FakeAccessor.HttpContext = UserContext(OpAccount);

                var filter = NewFilter(scope, publisher);
                var httpCtx = NewHttpContext(OpAccount, controller: "SysUser", action: "Boom");
                var args = new Dictionary<string, object?> { ["id"] = 1 };
                // 动作抛异常:过滤器应记录后原样上抛,ReturnResult 无值可采
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RunFilterAsync(filter, httpCtx, args, null, throwInAction: new InvalidOperationException("boom")));

                var calls = publisher.Calls.ToList();
                Assert.Single(calls);
                var op = Assert.IsType<SysLogOp>(calls[0].Payload);
                Assert.False(op.Success);
                Assert.Null(op.ReturnResult); // 无正常返回对象
                Assert.NotNull(op.RequestParam);
            }
            finally
            {
                await RestoreOpLogConfigAsync(scope, existed);
            }
        }

        // ── 4. 不可序列化参数占位替代 ─────────────────────────────

        [Fact]
        public async Task 采集_不可序列化参数_Stream占位不落原文()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            var existed = await scope.Db.Queryable<SysConfig>().AnyAsync(u => u.Code == SysOpLogCode);
            try
            {
                await EnsureOpLogConfigAsync(scope, "True");
                scope.FakeAccessor.HttpContext = UserContext(OpAccount);

                var filter = NewFilter(scope, publisher);
                var httpCtx = NewHttpContext(OpAccount, controller: "SysFile", action: "Upload");
                var args = new Dictionary<string, object?>
                {
                    ["stream"] = new MemoryStream(new byte[] { 1, 2, 3 }),
                    ["name"] = "a.txt"
                };
                var result = new ObjectResult(new { code = 200, msg = "ok" });
                await RunFilterAsync(filter, httpCtx, args, result);

                var op = Assert.IsType<SysLogOp>(Assert.Single(publisher.Calls).Payload);
                Assert.NotNull(op.RequestParam);
                Assert.Contains("[stream]", op.RequestParam);
                Assert.Contains("a.txt", op.RequestParam);
            }
            finally
            {
                await RestoreOpLogConfigAsync(scope, existed);
            }
        }

        // ── 5. Page 排除大字段 + Detail 全字段(真实 PG)────────────

        [Fact]
        public async Task 操作日志_Page不载大字段_Detail全字段含两大字段()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var svc = scope.Service<SysLogOpService>();
            try
            {
                // 探针行(带两大字段)
                var dup = await scope.Db.Queryable<SysLogOp>().FirstAsync(u => u.Id == ProbeOpId);
                if (dup is not null)
                {
                    await scope.Db.Deleteable<SysLogOp>().Where(u => u.Id == ProbeOpId).ExecuteCommandAsync();
                }

                await scope.Db.Insertable(new SysLogOp
                {
                    Id = ProbeOpId,
                    ControllerName = "DshProbe",
                    ActionName = "Echo",
                    HttpMethod = "POST",
                    RemoteIp = "127.0.0.1",
                    ElapsedMs = 12,
                    Account = OpAccount,
                    Success = true,
                    RequestParam = "{\"input\":{\"name\":\"探针\"}}",
                    ReturnResult = "{\"code\":200,\"msg\":\"ok\"}"
                }).ExecuteCommandAsync();

                // Page:Account 精确过滤命中;record SysLogOpOutput 无两大字段属性=编译期保证不载
                var page = await svc.PageAsync(new PageSysLogOpInput(OpAccount, null, null, null, null, 1, 10));
                Assert.True(page.Total >= 1);
                var hit = page.List.Single(u => u.Id == ProbeOpId);
                Assert.Equal("DshProbe", hit.ControllerName);
                Assert.Equal(12, hit.ElapsedMs);

                // Detail:全字段(含两大字段)
                var detail = await svc.GetDetailAsync(ProbeOpId);
                Assert.Equal("{\"input\":{\"name\":\"探针\"}}", detail.RequestParam);
                Assert.Equal("{\"code\":200,\"msg\":\"ok\"}", detail.ReturnResult);
                Assert.True(detail.Success);

                // Detail 不存在 → AppException
                await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(-1));
            }
            finally
            {
                await scope.Db.Deleteable<SysLogOp>().Where(u => u.Id == ProbeOpId).ExecuteCommandAsync();
                await scope.Db.Deleteable<SysLogOp>().Where(u => u.Account == OpAccount).ExecuteCommandAsync();
            }
        }

        // ── 辅助 ──────────────────────────────────────────────────

        private static OpLogFilter NewFilter(DbScope scope, FakePublisher publisher)
            => new(NullLogger<OpLogFilter>.Instance, publisher, scope.Service<UserManager>(), scope.Service<SysConfigService>());

        private static HttpContext NewHttpContext(string account, string controller, string action)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "GET";
            ctx.Request.Path = $"/api/{controller}/{action}";
            ctx.Request.RouteValues["controller"] = controller;
            ctx.Request.RouteValues["action"] = action;
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, "990000000006"),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.RealName, "探针庚"),
                new Claim(JwtClaimNames.AccountType, "777"),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return ctx;
        }

        private static async Task RunFilterAsync(OpLogFilter filter, HttpContext httpContext,
            IDictionary<string, object?> args, IActionResult? result, Exception? throwInAction = null)
        {
            var actionContext = new ActionContext(httpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor());
            var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object());
            executed.Result = result;
            var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), (IDictionary<string, object?>)args, new object());

            ActionExecutionDelegate next = () =>
            {
                if (throwInAction is not null)
                {
                    throw throwInAction;
                }
                return Task.FromResult(executed);
            };

            await filter.OnActionExecutionAsync(executing, next);
        }

        private static HttpContext UserContext(string account)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, "990000000007"),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.RealName, "探针辛"),
                new Claim(JwtClaimNames.AccountType, "777"),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
        }

        /// <summary>sys_oplog 配置行确保存在并置值(经服务写路径失效缓存;不存在先 Add)。</summary>
        private static async Task EnsureOpLogConfigAsync(DbScope scope, string value)
        {
            var svc = scope.Service<SysConfigService>();
            var row = await scope.Db.Queryable<SysConfig>().FirstAsync(u => u.Code == SysOpLogCode);

            // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):仅"写 sys_oplog"这一段临时挂超管,
            // 结束后还原原上下文——OpLogFilter 采集/审计仍以探针操作者(UserContext)身份执行,被测语义不变。
            var accessor = scope.FakeAccessor;
            var previous = accessor.HttpContext;
            accessor.SetSuperAdmin();
            try
            {
                if (row is null)
                {
                    await svc.AddAsync(new AddConfigInput("记录操作日志", SysOpLogCode, value, "Default", true, "批 AZ 探针"));
                }
                else
                {
                    await svc.UpdateAsync(new UpdateConfigInput(row.Id, null, value, null, null, null));
                }
            }
            finally
            {
                accessor.HttpContext = previous;
            }
        }

        private static async Task RestoreOpLogConfigAsync(DbScope scope, bool existed)
        {
            if (existed)
            {
                await EnsureOpLogConfigAsync(scope, "True");
            }
            else
            {
                await scope.Db.Deleteable<SysConfig>().Where(u => u.Code == SysOpLogCode).ExecuteCommandAsync();
                await scope.Cache.RemoveAsync($"{CacheConst.KeyConfig}{SysOpLogCode}");
            }
        }

        /// <summary>事件发布测试替身:记录 (事件名, 载荷),供字段级断言(隔离真实总线时序)。</summary>
        private sealed class FakePublisher : IEventPublisher
        {
            private readonly object _lock = new();
            private readonly List<(string Name, object? Payload)> _calls = [];

            public IReadOnlyList<(string Name, object? Payload)> Calls
            {
                get { lock (_lock) { return _calls.ToList(); } }
            }

            public void Clear() { lock (_lock) { _calls.Clear(); } }

            public Task PublishAsync<T>(string eventName, T payload)
            {
                lock (_lock) { _calls.Add((eventName, payload)); }
                return Task.CompletedTask;
            }
        }

        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public AdminCore.Core.Cache.ICacheService Cache { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            // ㉖ 唯一身份构造点:默认 null;用例内设探针操作者(UserContext),配置写段临时切超管
            public TestActorAccessor FakeAccessor { get; } = new();

            public T Service<T>() where T : class
                => _provider!.GetRequiredService<T>();

            public DbScope()
            {
                if (!IsPostgresReachable())
                {
                    return;
                }

                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = DbConnectionString,
                        ["Yit:WorkerId"] = "1"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysLogOp).Assembly);
                // ㉖ 放在 AddAutoServices/AddSqlSugar 之后注册:单服务解析取最后一次注册,确保 accessor 落到本夹具实例
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Cache = _provider.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
                Provider = _provider;
                TestSchemaBootstrap.Ensure(Db);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
            }

            private static string DbConnectionString =>
                TestPg.ConnectionString;

            private static bool IsPostgresReachable()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var result = client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2));
                    return result;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
