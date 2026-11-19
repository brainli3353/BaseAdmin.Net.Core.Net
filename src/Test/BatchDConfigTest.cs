using System.Net;
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.SqlSugar;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
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
/// 批 D 4.1 配置参数 + 1.13 幂等测试(真实 PG,与 BatchCTenantTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
/// ① CRUD:新建(Code 唯一)/按 Code 读值(缓存)/部分更新(变更即失效)/软删(SysFlag 内置禁删);
/// ② 幂等:IdempotentFilter 手工构造——窗口内首次放行、第二次 429(含异常码),未标注端点直通。
/// </summary>
public class BatchDConfigTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public BatchDConfigTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // ─────────────────────────────────────────────────────────────
    // ① CRUD + 缓存闭环
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CRUD_读值缓存_部分更新_内置禁删()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var configService = sp.GetRequiredService<SysConfigService>();
        var configRepo = sp.GetRequiredService<SqlSugarRepository<SysConfig>>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var code = $"batchd.{p}";

        // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):本用例整方法断言配置 CRUD/缓存/内置禁删,
        // 全程以超管身份执行以穿过写闸(读值断言与身份无关);不把任何期望值改成 common.forbidden。
        dbScope.Accessor.SetSuperAdmin();

        // 新建
        var id = await configService.AddAsync(new AddConfigInput($"测试配置{p}", code, "v1", "batchd", false, "备注"));
        Assert.True(id > 0);

        // 按 Code 读值(首读回源并回填缓存)
        Assert.Equal("v1", await configService.GetConfigValueByCodeAsync(code));
        // 二次读值走缓存(缓存被回填)——直接改库绕过服务,读值应仍返旧值(缓存生效的直证)
        var row = await configRepo.GetFirstAsync(u => u.Code == code);
        row!.Value = "v-direct";
        await configRepo.UpdateAsync(row);
        Assert.Equal("v1", await configService.GetConfigValueByCodeAsync(code)); // 缓存直返,未回源

        // 部分更新(变更即失效):value → 读值立即新值
        await configService.UpdateAsync(new UpdateConfigInput(id, null, "v2", null, null, null));
        Assert.Equal("v2", await configService.GetConfigValueByCodeAsync(code));
        row = await configRepo.GetFirstAsync(u => u.Code == code);
        Assert.Equal("v2", row!.Value);
        Assert.Equal("batchd", row.GroupCode); // 未传字段未变
        Assert.Equal($"测试配置{p}", row.Name);

        // 空串 = 显式清值(合法操作)
        await configService.UpdateAsync(new UpdateConfigInput(id, null, "", null, null, null));
        Assert.Equal("", await configService.GetConfigValueByCodeAsync(code));

        // 不存在 Code → null(不抛)
        Assert.Null(await configService.GetConfigValueByCodeAsync($"batchd.{p}.none"));

        // 内置项禁删
        await configService.UpdateAsync(new UpdateConfigInput(id, null, null, null, true, null));
        await Assert.ThrowsAsync<AppException>(() => configService.DeleteAsync(id));
        row = await configRepo.GetFirstAsync(u => u.Id == id);
        Assert.NotNull(row); // 仍在(未删)

        // 业务项可删(软删)
        await configService.UpdateAsync(new UpdateConfigInput(id, null, null, null, false, null));
        await configService.DeleteAsync(id);
        Assert.Null(await configRepo.GetFirstAsync(u => u.Id == id));

        // Code 唯一:再建同 Code → 拒绝(软删行被软删过滤器排除,不冲突)
        var id2 = await configService.AddAsync(new AddConfigInput(null, code, "v9", null, false, null));
        await Assert.ThrowsAsync<AppException>(() => configService.AddAsync(new AddConfigInput(null, code, "x", null, false, null)));
        // 清理
        await CleanupAsync(sp, id2, id);
    }

    // ─────────────────────────────────────────────────────────────
    // ② 幂等:窗口内重复提交 429
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 幂等_窗口内重复提交_429()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例不可达时缓存自增无意义,本用例不依赖 PG,仅缓存");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var cache = sp.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
        var filter = new IdempotentFilter(cache);

        // 首次:放行(next 执行)
        var invoked = 0;
        await filter.OnActionExecutionAsync(NewIdempotentContext(), async () =>
        {
            invoked++;
            return new ActionExecutedContext(NewActionContext(), new List<IFilterMetadata>(), null!);
        });
        Assert.Equal(1, invoked);

        // 第二次(同身份+同端点,窗口 3s 内):429 AppException
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            filter.OnActionExecutionAsync(NewIdempotentContext(), async () =>
            {
                invoked++;
                return new ActionExecutedContext(NewActionContext(), new List<IFilterMetadata>(), null!);
            }));
        Assert.Equal(429, ex.Code);
        Assert.Contains("重复提交", ex.Message);
        Assert.Equal(1, invoked); // next 未再执行
    }

    [Fact]
    public async Task 幂等_未标注端点_直通()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var cache = diScope.ServiceProvider.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
        var filter = new IdempotentFilter(cache);
        var invoked = 0;

        for (var i = 0; i < 3; i++)
        {
            await filter.OnActionExecutionAsync(NewIdempotentContext(withAttr: false), async () =>
            {
                invoked++;
                return new ActionExecutedContext(NewActionContext(), new List<IFilterMetadata>(), null!);
            });
        }
        Assert.Equal(3, invoked); // 未标注 → 每次直通
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>构造带 [Idempotent] 标注的 ActionExecutingContext(身份=远端 IP,端点=RouteValues 固定值)。</summary>
    private static ActionExecutingContext NewIdempotentContext(bool withAttr = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback; // 固定身份(匿名 → IP)
        var descriptor = new ActionDescriptor { RouteValues = new Dictionary<string, string?> { ["controller"] = "SysConfig", ["action"] = "Add" } };
        if (withAttr)
        {
            // .NET 10:EndpointMetadata 为 IList<object>(无 Add,整体赋值)
            descriptor.EndpointMetadata = new List<object> { new IdempotentAttribute(seconds: 3) };
        }

        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor, new ModelStateDictionary()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), new TestController());
    }

    private static ActionContext NewActionContext()
        => new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary());

    private static async Task CleanupAsync(IServiceProvider sp, params long[] ids)
    {
        var db = sp.GetRequiredService<ISqlSugarClient>();
        if (ids.Length > 0)
        {
            await db.Deleteable<SysConfig>().Where(u => ids.Contains(u.Id)).ExecuteCommandAsync();
        }
    }

    // 探针配置 Code 前缀（`batchd.{guid8}`）：夹具兜底清理按它无条件识别，见 DisposeAsync
    private const string ProbeCodePrefix = "batchd.";

    /// <summary>夹具实现：无准备动作（⚠ 接口成员名是 InitializeAsync，写成 ImplAsync 会 CS0535）。</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// 夹具级兜底清理（会写库测试的清理句柄统一放夹具里）。
    /// **为什么必须有**：原清理 <see cref="CleanupAsync"/> 排在被测调用**之后** ⇒ 中途断言失败/抛异常即整段跳过，
    /// 探针行（sys_config 是产品表）就永久残留；且用例本身会**软删**探针行（业务项可删那步），
    /// 带软删过滤器时恰恰查不到这些行。⇒ 清理句柄放夹具里，用例体内不再依赖走到最后一行。
    /// 按**探针 Code 前缀**无条件清理（而非用入参 id）：既覆盖中断场景，也让以后新增用例自动受保护。
    /// </summary>
    public async Task DisposeAsync()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable) { return; }
        await CleanupResidueAsync(dbScope.Db);
    }

    private static async Task CleanupResidueAsync(ISqlSugarClient db)
    {
        // ⚠ 必须 ClearFilter()：探针行常被用例软删（删除用例正走软删路径），
        //   留着软删全局过滤器就恰恰查不到要清的那些行。
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

/// <summary>DB 测试客户端(与 BatchCTenantTest 同源样板)。</summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        // ㉖ 唯一身份构造点:默认未认证基线;CRUD 用例整方法挂超管以穿过配置写闸
        public TestActorAccessor Accessor { get; } = new();

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
            // ㉖ 放在 AddAutoServices 之后注册:单服务解析取最后一次注册,确保 accessor 落到本夹具实例
            services.AddSingleton<IHttpContextAccessor>(Accessor);
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
