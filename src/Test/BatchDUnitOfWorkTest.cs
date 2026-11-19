using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 D 3.2 事务闭环测试(真实 PG,与 BatchCTenantTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
/// 手工构造 UnitOfWorkFilter + ActionExecutingContext(EndpointMetadata 注入 [UnitOfWork]),
/// next 委托内执行真实 DB 操作,观察提交/回滚/直通/ExceptionHandled 四分支。
/// 用例:① 全成功 → Commit(两行均持久化);② 委托抛出 → Rollback + 重抛(两行均不持久化);
/// ③ 返回 Exception 未处理 → Rollback;④ 返回 Exception 已处理 → Commit(框架口径:ExceptionHandled 视为成功);
/// ⑤ 无标注端点 → 直通(next 照常执行)。
/// </summary>
public class BatchDUnitOfWorkTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public BatchDUnitOfWorkTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // ─────────────────────────────────────────────────────────────
    // ① 全成功 → Commit
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 全成功_两行均提交()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var db = diScope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var filter = new UnitOfWorkFilter(db);
        var p = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"uow{p}a";
        var codeB = $"uow{p}b";

        await filter.OnActionExecutionAsync(NewContext(withUnitOfWork: true), async () =>
        {
            await db.Insertable(new SysConfig { Code = codeA, Value = "a" }).ExecuteCommandAsync();
            await db.Insertable(new SysConfig { Code = codeB, Value = "b" }).ExecuteCommandAsync();
            return new ActionExecutedContext(dbScope.ActionContext, new List<IFilterMetadata>(), null!);
        });

        // 提交后两行均可查
        Assert.NotNull(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeA));
        Assert.NotNull(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeB));
        await CleanupAsync(db, codeA, codeB);
    }

    // ─────────────────────────────────────────────────────────────
    // ② 委托抛出 → Rollback + 重抛
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 委托抛出_整体回滚且异常上抛()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var db = diScope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var filter = new UnitOfWorkFilter(db);
        var p = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"uow{p}a";
        var codeB = $"uow{p}b";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await filter.OnActionExecutionAsync(NewContext(withUnitOfWork: true), async () =>
            {
                await db.Insertable(new SysConfig { Code = codeA, Value = "a" }).ExecuteCommandAsync();
                await db.Insertable(new SysConfig { Code = codeB, Value = "b" }).ExecuteCommandAsync();
                throw new InvalidOperationException("触发回滚");
            });
        });
        Assert.Equal("触发回滚", ex.Message);

        // 回滚后两行均不可见(半成品不留)
        Assert.Null(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeA));
        Assert.Null(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeB));
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 返回 Exception 未处理 → Rollback(MVC 管道口径:action 异常被捕获进 executed.Exception)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 异常未处理_整体回滚()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var db = diScope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var filter = new UnitOfWorkFilter(db);
        var p = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"uow{p}a";
        var codeB = $"uow{p}b";

        // 口径:未处理异常 → 过滤器回滚后原样上抛(生产:外层 OpLogFilter 捕获记失败日志再上抛
        //→ ExceptionMiddleware 统一转 ApiResult)
        ActionExecutedContext? executed = null;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await filter.OnActionExecutionAsync(NewContext(withUnitOfWork: true), async () =>
            {
                await db.Insertable(new SysConfig { Code = codeA, Value = "a" }).ExecuteCommandAsync();
                await db.Insertable(new SysConfig { Code = codeB, Value = "b" }).ExecuteCommandAsync();
                executed = NewExecuted(dbScope.ActionContext, handled: false);
                return executed;
            });
        });
        Assert.Equal("测试异常", ex.Message);
        Assert.NotNull(executed);
        Assert.NotNull(executed!.Exception);
        Assert.False(executed.ExceptionHandled);

        Assert.Null(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeA));
        Assert.Null(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeB));
    }

    // ─────────────────────────────────────────────────────────────
    // ④ 返回 Exception 已处理 → Commit(框架口径:ExceptionHandled 视为成功)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 异常已处理_按成功提交()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var db = diScope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var filter = new UnitOfWorkFilter(db);
        var p = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"uow{p}a";

        ActionExecutedContext? executed = null;
        await filter.OnActionExecutionAsync(NewContext(withUnitOfWork: true), async () =>
        {
            await db.Insertable(new SysConfig { Code = codeA, Value = "a" }).ExecuteCommandAsync();
            executed = NewExecuted(dbScope.ActionContext, handled: true);
            return executed;
        });
        Assert.NotNull(executed);
        Assert.NotNull(executed!.Exception);
        Assert.True(executed.ExceptionHandled);

        // 已处理异常 → Commit:行持久化
        Assert.NotNull(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeA));
        await CleanupAsync(db, codeA);
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ 无标注 → 直通
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 无标注_直通执行()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var db = diScope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var filter = new UnitOfWorkFilter(db);
        var p = Guid.NewGuid().ToString("N")[..8];
        var codeA = $"uow{p}a";
        var invoked = false;

        await filter.OnActionExecutionAsync(NewContext(withUnitOfWork: false), async () =>
        {
            await db.Insertable(new SysConfig { Code = codeA, Value = "a" }).ExecuteCommandAsync();
            invoked = true;
            return new ActionExecutedContext(dbScope.ActionContext, new List<IFilterMetadata>(), null!);
        });

        Assert.True(invoked);
        Assert.NotNull(await db.Queryable<SysConfig>().FirstAsync(u => u.Code == codeA));
        await CleanupAsync(db, codeA);
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>构造 ActionExecutingContext(EndpointMetadata 注入 [UnitOfWork];特性路由下由路由系统填充,测试手工注入同构)。</summary>
    private static ActionExecutingContext NewContext(bool withUnitOfWork, ActionContext? actionContext = null)
    {
        // .NET 10:EndpointMetadata 为 IList<object>(无 Add,整体赋值;特性路由下由路由系统填充同构)
        var descriptor = new ActionDescriptor();
        if (withUnitOfWork)
        {
            descriptor.EndpointMetadata = new List<object> { new UnitOfWorkAttribute() };
        }

        var ac = actionContext ?? new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor, new ModelStateDictionary());
        return new ActionExecutingContext(ac, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new TestController());
    }

    private static ActionExecutedContext NewExecuted(ActionContext ac, bool handled)
        => new ActionExecutedContext(ac, new List<IFilterMetadata>(), null!)
        {
            Exception = new InvalidOperationException("测试异常"),
            ExceptionHandled = handled
        };

    private static async Task CleanupAsync(ISqlSugarClient db, params string[] codes)
    {
        if (codes.Length > 0)
        {
            await db.Deleteable<SysConfig>().Where(u => codes.Contains(u.Code)).ExecuteCommandAsync();
        }
    }

    // 探针配置 Code 前缀（`uow{guid8}a|b`）：夹具兜底清理按它无条件识别，见 DisposeAsync
    private const string ProbeCodePrefix = "uow";

    /// <summary>夹具实现：无准备动作（⚠ 接口成员名是 InitializeAsync，写成 ImplAsync 会 CS0535）。</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// 夹具级兜底清理（会写库测试的清理句柄统一放夹具里）。
    /// **为什么必须有**：原清理只挂在"提交成功"那几条用例的**末尾**（且按入参 code 清）⇒ 中途断言失败/抛异常即整段跳过，
    /// 而 sys_config 是**产品表**，探针行（code=uow{guid8}a/b）会永久残留并污染后续唯一性/计数类用例。
    /// ⇒ 清理句柄放夹具里，用例体内不再依赖走到最后一行；按**探针 Code 前缀**无条件识别（不依赖用例回传 id）。
    /// WHY 复用 <see cref="SharedScope"/> 而不新建容器：本类注释已实证"前驱容器 Commit 后新建容器会污染
    /// 后续事务语义"，夹具清理只做普通 DELETE（无事务），在共享 scope 上执行既避开该污染又不需要新连接。
    /// </summary>
    public async Task DisposeAsync()
    {
        var dbScope = SharedScope.Value;
        if (!dbScope.PgReachable) { return; }
        await CleanupResidueAsync(dbScope.Db);
    }

    private static async Task CleanupResidueAsync(ISqlSugarClient db)
    {
        // ⚠ 必须 ClearFilter()：若同族用例把探针行软删过，带软删全局过滤器就查不到残留。
        var ids = await db.Queryable<SysConfig>()
            .ClearFilter()
            .Where(u => u.Code.StartsWith(ProbeCodePrefix))
            .Select(u => u.Id)
            .ToListAsync();
        if (ids.Count == 0) { return; }
        await db.Deleteable<SysConfig>().Where(u => ids.Contains(u.Id)).ExecuteCommandAsync();
    }

        /// <summary>测试用具体控制器(ActionExecutingContext 第 4 参要求 Controller 实例;Mvc.Controller 为抽象类)。</summary>
    private sealed class TestController : Microsoft.AspNetCore.Mvc.Controller { }

/// <summary>DB 测试客户端(与 BatchCTenantTest 同源样板:同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices)。</summary>
    /// <summary>
    /// 类级共享 DB scope(事务特性专用):SqlSugar 5.1.4.217 多连接 scope 的事务 Commit 路径会在
    /// 线程执行上下文(AsyncLocal)残留当前连接状态,污染其「之后新建容器」的事务语义(实证:前驱容器
    /// 走 Commit 后,次容器 [UnitOfWork] 场景 insert 自动提交、回滚失效;前驱纯回滚则不污染)。
    /// 生产单容器(首容器干净)不受影响;测试侧本类全部用例共享一个容器——xUnit 同类方法同线程串行,
    /// 该类执行线程专属本类,首容器事务语义必正确,且不再新建容器。
    /// </summary>
    private static readonly Lazy<DbScope> SharedScope = new(() => new DbScope());

    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        /// <summary>测试内共享的 ActionContext(同一 DefaultHttpContext,供 NewContext 复用)。</summary>
        public ActionContext ActionContext { get; } = new(
            new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary());

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
                    ["Yit:WorkerId"] = "1",
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(SysConfig).Assembly);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(DbHost, DbPort);
                return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
