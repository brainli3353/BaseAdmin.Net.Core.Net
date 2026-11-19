using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Notice.Dto;
using AdminCore.Core.Services.Print;
using AdminCore.Core.Services.Print.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 AF 回归用例(测试阶段抓出的三个真缺陷的修复验证;均为"原判据下判漏了"的自纠,非新增功能面):
/// ① **P1** 升级公告载体不可达——`SysNoticeService.ToType` 曾把 3 硬编码回落为 Notice ⇒ 服务层建不出/改不出
///    <see cref="NoticeTypeEnum.Upgrade"/>;修复=按枚举定义判定(`System.Enum.IsDefined`)。
/// ② **P2** 打印模板软删后同名重建——查重受全局软删过滤器影响 ⇒ 应用层放行而 INSERT 撞库层唯一索引(500);
///    修复=查重/副本命名 `ClearFilter&lt;IDeletedFilter&gt;()`,给出可读业务异常。
/// ③ **P2** 唯一索引含 `TenantId` ⇒ 跨租户同名模板互不冲突(此前只有 Name 会跨租户互斥且落 500)。
/// </summary>
public class BatchAFRegressionFixTest
{
    private readonly ITestOutputHelper _output;

    public BatchAFRegressionFixTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;
    private const string ProbePrefix = "afrf_";

    // ── ① 升级公告:服务层能写入 Upgrade 类型(P1) ──
    [Fact]
    public async Task 修复_P1_通知服务可写入Upgrade类型()
    {
        using var scope = new Scope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var noticeService = sp.GetRequiredService<SysNoticeService>();
        var noticeRepo = sp.GetRequiredService<SqlSugarRepository<SysNotice>>();

        var title = $"{ProbePrefix}升级公告载体{DateTime.Now:HHmmss}";
        var id = await noticeService.AddAsync(new AddNoticeInput(title, "批 AF P1 验证正文", (int)NoticeTypeEnum.Upgrade));
        try
        {
            var row = await noticeRepo.GetFirstAsync(n => n.Id == id);
            Assert.NotNull(row);
            Assert.Equal(NoticeTypeEnum.Upgrade, row!.Type); // 修复前:被 ToType 静默改写成 Notice(1)
            _output.WriteLine($"P1 判定:服务层写入后 Type={row.Type}(期望 Upgrade=3)");

            // 更新路径同口径:改回 Announcement 再改回 Upgrade,均不被静默改写
            await noticeService.UpdateAsync(new UpdateNoticeInput(id, title, "改后正文", (int)NoticeTypeEnum.Announcement));
            row = await noticeRepo.GetFirstAsync(n => n.Id == id);
            Assert.Equal(NoticeTypeEnum.Announcement, row!.Type);

            await noticeService.UpdateAsync(new UpdateNoticeInput(id, title, "再改回", (int)NoticeTypeEnum.Upgrade));
            row = await noticeRepo.GetFirstAsync(n => n.Id == id);
            Assert.Equal(NoticeTypeEnum.Upgrade, row!.Type);

            // 非法值仍回落 Notice(容错口径不回归)
            await noticeService.UpdateAsync(new UpdateNoticeInput(id, title, "非法值", 99));
            row = await noticeRepo.GetFirstAsync(n => n.Id == id);
            Assert.Equal(NoticeTypeEnum.Notice, row!.Type);
        }
        finally
        {
            await noticeRepo.DeletePhysicalByAsync(n => n.Id == id);
        }
    }

    // ── ② 软删后同名重建给业务异常(P2) ──
    [Fact]
    public async Task 修复_P2_软删后同名重建_业务异常而非库层500()
    {
        using var scope = new Scope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var printService = sp.GetRequiredService<SysPrintService>();
        var printRepo = sp.GetRequiredService<SqlSugarRepository<SysPrint>>();

        var name = $"{ProbePrefix}软删同名{DateTime.Now:HHmmss}";
        var id = await printService.AddAsync(new AddSysPrintInput(name, "{\"v\":1}", 1, null, null, null));
        try
        {
            await printService.DeleteAsync(id); // 软删

            // 修复前:此处抛 PostgresException(唯一索引冲突)→ HTTP 500;修复后:AppException(业务码)
            var ex = await Assert.ThrowsAsync<AppException>(() =>
                printService.AddAsync(new AddSysPrintInput(name, "{\"v\":2}", 1, null, null, null)));
            Assert.Equal(ErrorCodes.PrintNameExists, ex.ErrorCode); // 批 AR-4 口径
            _output.WriteLine($"P2 判定:软删后同名重建 → AppException「{ex.Message}」(而非 PostgresException)");
        }
        finally
        {
            // 软删行仍占唯一索引 ⇒ 必须物理清(含本用例期间可能产生的行)
            await printRepo.DeletePhysicalByAsync(p => p.Name == name);
        }
    }

    // ── ③ 跨租户同名允许(P2:唯一键含 TenantId) ──
    [Fact]
    public async Task 修复_P2_跨租户同名模板_互不冲突()
    {
        using var scope = new Scope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var printRepo = sp.GetRequiredService<SqlSugarRepository<SysPrint>>();

        var name = $"{ProbePrefix}跨租户同名{DateTime.Now:HHmmss}";
        const long tenantA = 9900000000000001L;
        const long tenantB = 9900000000000002L;
        try
        {
            // 直接插实体并显式给 TenantId(AOP 只填 null 租户,显式值不覆盖)
            await printRepo.InsertAsync(new SysPrint { Name = name, Template = "{\"t\":\"A\"}", TenantId = tenantA });
            await printRepo.InsertAsync(new SysPrint { Name = name, Template = "{\"t\":\"B\"}", TenantId = tenantB });

            // 跨租户查询需清租户过滤(实体是 EntityTenant;本用例无 Claim,租户过滤未挂,仍显式 IgnoreTenant 更稳)
            var rows = await printRepo.Context.Queryable<SysPrint>()
                .ClearFilter<ITenantIdFilter>()
                .Where(p => p.Name == name)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.TenantId == tenantA);
            Assert.Contains(rows, r => r.TenantId == tenantB);
            _output.WriteLine($"P2 判定:同租户名唯一键生效前提下,跨租户同名共 {rows.Count} 行(修复前第二行会撞唯一索引)");
        }
        finally
        {
            await printRepo.DeletePhysicalByAsync(p => p.Name == name);
        }
    }

    // ── 容器(最小可用:通知域需 SysConfigService;打印域只需仓储) ──
    private sealed class Scope : IDisposable
    {
        private readonly ServiceProvider? _provider;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        public Scope()
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
            services.AddHttpContextAccessor();
            services.AddSqlSugar(config);
            services.AddCache(config);
            services.AddSignalR(); // 通知服务 → SysOnlineUserService → IHubContext
            services.AddAutoServices(typeof(SysNoticeService).Assembly); // 含 SysNoticeService 与 SysPrintService

            _provider = services.BuildServiceProvider();
            Provider = _provider;
            PgReachable = true;
        }

        public void Dispose() => _provider?.Dispose();

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