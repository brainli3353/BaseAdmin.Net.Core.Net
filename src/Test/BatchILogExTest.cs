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
using AdminCore.Core.SeedData;
using AdminCore.Web.Core.Auth;
using AdminCore.Web.Core.Middlewares;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using System.Security.Claims;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 I(5.1 异常档 SysLogEx + sys_oplog 开关 / 5.2 文件日志通道)测试。
    /// 覆盖:未处理异常经 ExceptionMiddleware 发 ex_log 事件(FakePublisher 捕获实例,字段逐断言)+
    /// 订阅者直接驱动落库(批 G 口径,隔离总线时序;真实总线全链由 e2e 覆盖)/
    /// AppException 不发事件(反向)/异常档 Page 不载大字段+过滤+Detail 全字段/
    /// sys_oplog 开关关→OpLogFilter 不发事件、开→发(真实 filter 链+FakePublisher)/
    /// SysConfigSeedData 种子口径(纯函数)/FileLoggerProvider 按天切分+级别过滤+格式(文件系统)。
    /// 探针:sys_log_ex/sys_config 行 dsh_i_ 前缀,finally 清理。
    /// </summary>
    public class BatchILogExTest
    {
        private const string ExAccount = "dsh_i_ex_probe";
        private const string OpAccount = "dsh_i_oplog";
        private const long SysOpLogId = 1300000000141L;

        // ── 5.1 异常档 ──────────────────────────────────────────────

        [Fact]
        public async Task 未处理异常_中间件发事件且订阅者落库()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            try
            {
                scope.FakeAccessor.HttpContext = UserContext(ExAccount);

                var ctx = new DefaultHttpContext();
                ctx.Request.Method = "POST";
                ctx.Request.Path = "/api/dsh/probe";
                ctx.Request.QueryString = new QueryString("?a=1&b=2");
                ctx.User = scope.FakeAccessor.HttpContext!.User;

                RequestDelegate next = _ => throw new DivideByZeroException("除零测试");
                var mw = NewMiddleware(next, scope, publisher);
                await mw.InvokeAsync(ctx);

                // 响应统一出口:HTTP 200 + 通用文案(不泄堆栈)
                Assert.Equal(200, ctx.Response.StatusCode);

                // 事件被发布且仅一次
                var exEvents = publisher.Calls.Where(c => c.Name == "ex_log").ToList();
                Assert.Single(exEvents);
                var log = Assert.IsType<SysLogEx>(exEvents[0].Payload);

                // 字段口径:异常全文三段 + TraceId 非空 + RequestParam 含查询串 + ReturnResult 为统一响应 JSON + 身份
                Assert.Contains("异常信息：除零测试", log.Exception);
                Assert.Contains("异常来源：", log.Exception);
                Assert.Contains("堆栈信息：", log.Exception);
                Assert.Contains("?a=1&b=2", log.RequestParam);
                Assert.Contains("系统繁忙", log.ReturnResult);
                Assert.False(string.IsNullOrWhiteSpace(log.TraceId));
                Assert.Equal(ExAccount, log.Account);
                Assert.Equal("Error", log.LogLevel);
                Assert.Equal("POST", log.HttpMethod);

                // 订阅者直接驱动落库(批 G 口径):落库后全字段可查
                var subscriber = scope.Service<ExLogSubscriber>();
                await subscriber.HandleExLogAsync(log);
                var row = (await scope.Db.Queryable<SysLogEx>().FirstAsync(u => u.Id == log.Id))!;
                Assert.Contains("除零测试", row.Exception);
                Assert.Equal(ExAccount, row.Account);
            }
            finally
            {
                await CleanupExAsync(scope, ExAccount);
            }
        }

        [Fact]
        public async Task 业务异常AppException_不发异常档事件()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var publisher = new FakePublisher();
            scope.FakeAccessor.HttpContext = UserContext(ExAccount);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/dsh/appex";
            ctx.User = scope.FakeAccessor.HttpContext!.User;

            RequestDelegate next = _ => throw new AppException("业务异常探针", code: 1001);
            var mw = NewMiddleware(next, scope, publisher);
            await mw.InvokeAsync(ctx);

            Assert.Equal(200, ctx.Response.StatusCode); // 统一出口
            Assert.Empty(publisher.Calls); // AppException 不发任何日志事件(5.17/5.1 口径)
        }

        [Fact]
        public async Task 异常档_Page不载大字段与过滤_Detail全字段()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var svc = scope.Service<SysLogExService>();
            long probeId = 0;
            try
            {
                // 探针行(带三大字段)
                probeId = 99_000_000_003_01;
                var dup = await scope.Db.Queryable<SysLogEx>().FirstAsync(u => u.Id == probeId);
                if (dup is not null)
                {
                    await scope.Db.Deleteable<SysLogEx>().Where(u => u.Id == probeId).ExecuteCommandAsync();
                }

                await scope.Db.Insertable(new SysLogEx
                {
                    Id = probeId,
                    ControllerName = "DshProbe",
                    ActionName = "Crash",
                    HttpMethod = "POST",
                    RequestUrl = "/api/dsh/crash?x=1",
                    RemoteIp = "127.0.0.1",
                    RequestParam = "x=1",
                    ReturnResult = "{\"code\":500}",
                    TraceId = "trace-probe-001",
                    ThreadId = 42,
                    Account = ExAccount,
                    RealName = "探针丁",
                    Exception = "异常信息：探针 \n堆栈信息：at X",
                    LogLevel = "Error"
                }).ExecuteCommandAsync();

                // Page:Account 精确过滤命中;record 无大字段属性=编译期保证不载
                var page = await svc.PageAsync(new PageSysLogExInput(ExAccount, null, null, null, null, 1, 10));
                Assert.True(page.Total >= 1);
                var hit = page.List.Single(u => u.Id == probeId);
                Assert.Equal("DshProbe", hit.ControllerName);
                Assert.Equal("trace-probe-001", hit.TraceId);
                Assert.Equal(42, hit.ThreadId);

                // ControllerName 过滤(不含 → 排除)
                var miss = await svc.PageAsync(new PageSysLogExInput(ExAccount, "NoMatchCtrl", null, null, null, 1, 10));
                Assert.DoesNotContain(miss.List, u => u.Id == probeId);

                // Detail:全字段(含三大字段)
                var detail = await svc.GetDetailAsync(probeId);
                Assert.Equal("x=1", detail.RequestParam);
                Assert.Equal("{\"code\":500}", detail.ReturnResult);
                Assert.Contains("探针", detail.Exception);

                // Detail 不存在 → AppException
                await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(-1));
            }
            finally
            {
                await scope.Db.Deleteable<SysLogEx>().Where(u => u.Id == probeId).ExecuteCommandAsync();
                await CleanupExAsync(scope, ExAccount);
            }
        }

        [Fact]
        public async Task oplog开关_关不发事件开发事件()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var existed = await scope.Db.Queryable<SysConfig>().AnyAsync(u => u.Code == SysConfigService.SysOpLog);
            var publisher = new FakePublisher();

            try
            {
                scope.FakeAccessor.HttpContext = UserContext(OpAccount);
                var filter = new OpLogFilter(NullLogger<OpLogFilter>.Instance, publisher,
                    scope.Service<UserManager>(), scope.Service<SysConfigService>());

                // 关:不发事件
                await EnsureOpLogConfigAsync(scope, "False");
                await RunOpLogFilterAsync(filter, OpLogHttpContext(OpAccount));
                Assert.Empty(publisher.Calls);

                // 开:发事件(op_log 一条)
                publisher.Clear();
                await EnsureOpLogConfigAsync(scope, "True");
                await RunOpLogFilterAsync(filter, OpLogHttpContext(OpAccount));
                var calls = publisher.Calls.ToList();
                Assert.Single(calls);
                Assert.Equal("op_log", calls[0].Name);
                var op = Assert.IsType<SysLogOp>(calls[0].Payload);
                Assert.Equal(OpAccount, op.Account);
                Assert.True(op.Success);
            }
            finally
            {
                // 恢复:原存在回 True;原不存在删行 + 失效缓存
                if (existed)
                {
                    await EnsureOpLogConfigAsync(scope, "True");
                }
                else
                {
                    await scope.Db.Deleteable<SysConfig>().Where(u => u.Code == SysConfigService.SysOpLog).ExecuteCommandAsync();
                    await scope.Cache.RemoveAsync($"{CacheConst.KeyConfig}{SysConfigService.SysOpLog}");
                }
            }
        }

        // ── 种子口径(纯函数)─────────────────────────────────────

        [Fact]
        public void SysConfigSeedData_sys_oplog种子口径()
        {
            var data = new SysConfigSeedData().HasData().ToList();
            // 批 AF:种子从 1 行扩为 2 行(sys_oplog + sys_upgrade),故改为"含目标行"断言(不弱化为 Single)
            Assert.Contains(data, r => r.Code == "sys_oplog");
            Assert.Contains(data, r => r.Code == "sys_upgrade");
            var row = data[0];
            Assert.Equal(SysOpLogId, row.Id);
            Assert.Equal("sys_oplog", row.Code);
            Assert.Equal("True", row.Value);
            Assert.True(row.SysFlag);
        }

        // ── 5.2 文件日志通道(文件系统,无 DB)─────────────────────

        [Fact]
        public void FileLogger_按天切分与级别过滤与格式()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dsh_i_logs_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var options = new FileLoggerOptions { Path = dir, MinimumLevel = LogLevel.Information };
                using var provider = new FileLoggerProvider(options);
                var logger = provider.CreateLogger("DshProbe.Cat");

                Assert.False(logger.IsEnabled(LogLevel.Debug));    // 低于 MinimumLevel
                Assert.True(logger.IsEnabled(LogLevel.Information));

                logger.LogInformation("批 I 文件日志探针 {N}", 1);
                logger.LogDebug("不应出现");
                logger.LogError(new Exception("boom"), "异常探针");

                var file = Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                Assert.True(File.Exists(file), "当天文件未创建");

                var lines = File.ReadAllLines(file);
                Assert.Equal(3, lines.Length); // Debug 被过滤;Error 消息行 + 异常体行 = 2
                Assert.Contains("批 I 文件日志探针 1", lines[0]);
                Assert.Contains("Information", lines[0]);
                Assert.Contains("DshProbe.Cat", lines[0]);
                Assert.Contains("异常探针", lines[1]);
                Assert.Contains("boom", lines[2]); // 异常体独立行

                // 文件被外部删除(logrotate 场景)→ 再写自动重建
                File.Delete(file);
                logger.LogInformation("重建探针");
                Assert.True(File.Exists(file), "被删后未重建");
                Assert.Contains("重建探针", File.ReadAllText(file));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        // ── 辅助 ──────────────────────────────────────────────────

        /// <summary>构造中间件(root provider=测试容器,异常分支逐请求 scope 解析 UserManager)。</summary>
        private static ExceptionMiddleware NewMiddleware(RequestDelegate next, DbScope scope, FakePublisher publisher)
            => new(next, NullLogger<ExceptionMiddleware>.Instance, publisher, scope.Provider);

        private static async Task CleanupExAsync(DbScope scope, string account)
            => await scope.Db.Deleteable<SysLogEx>().Where(u => u.Account == account).ExecuteCommandAsync();

        /// <summary>sys_oplog 配置行确保存在并置值(经服务写路径失效缓存;不存在先 Add)。</summary>
        private static async Task EnsureOpLogConfigAsync(DbScope scope, string value)
        {
            var svc = scope.Service<SysConfigService>();
            // ㉖ 平台级配置写闸（`SysConfigService.EnsurePlatformAdmin`）：本夹具写平台开关
            // `sys_oplog`，非超管会被 Forbidden 拦下 ⇒ 仅写这一段临时挂超管身份，
            // 结束后还原调用方原上下文（OpLogFilter 的 UserManager 仍按原身份判定）。
            var previous = scope.FakeAccessor.HttpContext;
            scope.FakeAccessor.HttpContext = TestActorAccessor.SuperAdmin().HttpContext;
            try
            {
                var row = await scope.Db.Queryable<SysConfig>().FirstAsync(u => u.Code == SysConfigService.SysOpLog);
                if (row is null)
                {
                    await svc.AddAsync(new AddConfigInput("记录操作日志", SysConfigService.SysOpLog, value, "Default", true, "批 I 探针"));
                }
                else
                {
                    await svc.UpdateAsync(new UpdateConfigInput(row.Id, null, value, null, null, null));
                }
            }
            finally
            {
                scope.FakeAccessor.HttpContext = previous;
            }
        }

        private static HttpContext OpLogHttpContext(string account)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/dsh/oplog";
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, "990000000004"),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.RealName, "探针戊"),
                new Claim(JwtClaimNames.AccountType, "777"),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return ctx;
        }

        private static async Task RunOpLogFilterAsync(OpLogFilter filter, HttpContext httpContext)
        {
            var descriptor = new ControllerActionDescriptor();
            var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
            var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object());
            var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
            await filter.OnActionExecutionAsync(executing, () => Task.FromResult(executed));
        }

        private static HttpContext UserContext(string account)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, "990000000005"),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.RealName, "探针己"),
                new Claim(JwtClaimNames.AccountType, "777"),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
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

        /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计/租户填充读它)。</summary>
        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public AdminCore.Core.Cache.ICacheService Cache { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

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
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysLogEx).Assembly);
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
