// 批 S-G 纵深期·分页边界止血(单点常量 + 全仓调用点收口 + 生成器模板同步 + 真实 PG 证据):
// ① 零库:PageConst 常量值定盘(Max=200/Default=10);
// ② 真实 PG:SysJobService.PageJobLogAsync(sys_job_log,实测 8876 行)传 PageSize=100000,断言 List.Count <= MaxPageSize
//    (行数 > 200 时强断言 Count == 200——钳制真实生效,而非"恰好没拉爆");
// ③(已拆出) 源码扫描防回潮门 → 独立零库类 BatchSGPageGateTest(用户 2026-10-26 批准作为门控保留,
//    已入 CLAUDE.md 零库档预设);本类只保留 ①常量定盘 ②真实 PG 钳制读数。
// 真实 PG 装置照抄 BatchUXDateRangeTest DbScope(同源连接串;沙箱不可达 Assert.Fail 明示禁跳过)。
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Job;
using AdminCore.Core.Services.Job.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchSGPageSizeClampTest
{
    private readonly ITestOutputHelper _output;

    public BatchSGPageSizeClampTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 BatchUXDateRangeTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 零库:单点常量定盘
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void PageConst_常量定盘()
    {
        Assert.Equal(200, PageConst.MaxPageSize);
        Assert.Equal(10, PageConst.DefaultPageSize);
    }

    // ─────────────────────────────────────────────────────────────
    // ② 真实 PG:PageSize=100000 被钳到 ≤200(sys_job_log 实测 8876 行)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 任务日志_超大PageSize被钳制()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysJobService>();

        // 表基线行数:钳制强断言的分母依据(行数 > 200 ⇒ Count 必须恰为 200)
        var baseline = await dbScope.Db.Ado.GetIntAsync("SELECT COUNT(*) FROM sys_job_log");
        _output.WriteLine($"sys_job_log 基线行数 = {baseline}");

        var page = await svc.PageJobLogAsync(new PageJobLogInput(null, 1, 100000));
        _output.WriteLine($"PageSize=100000 → Total={page.Total}, List.Count={page.List.Count}");

        Assert.True(page.List.Count <= PageConst.MaxPageSize,
            $"钳制未生效:List.Count={page.List.Count} 超过上限 {PageConst.MaxPageSize}(基线行数 {baseline})");
        if (baseline > PageConst.MaxPageSize)
        {
            Assert.Equal(PageConst.MaxPageSize, page.List.Count);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG 的最小宿主(照抄 BatchUXDateRangeTest DbScope;服务经 AddAutoServices 自动发现)
    // ─────────────────────────────────────────────────────────────
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;

        public ServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                scope.Provider = new ServiceCollection().BuildServiceProvider();
                return scope;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR(); // AddAutoServices 全量注册连带依赖(SysNoticeService → IHubContext),测试无连接时推送 no-op
            services.AddAutoServices(typeof(SysJobService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchUXDateRangeTest 口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", 5432, timeout.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd;AOP 审计/租户填充读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
