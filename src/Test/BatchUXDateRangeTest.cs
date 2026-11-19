// 批 UX 后端件 B1d:时间范围筛选族(真实 PG,与 BatchUXBackendFilterTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
// 覆盖 5 域 DTO 新增 StartDate/EndDate + Service WhereIf 范围(均按各域 CreateTime,闭区间 >=Start <=End,照仓内既有时间筛选实读口径):
// ① PageTenantInput(SysTenantService) ② PageNoticeInput(SysNoticeService) ③ PageOpenAccessInput(SysOpenAccessService)
// ④ PageSysPrintInput(SysPrintService) ⑤ PageSerialInput(SysSerialService)。
// 每域 1 用例 = 窄窗只回界内:插两条界外(now-10d / now-8d)+ 一条界内(now-1h)探针,窄窗(now-2d ~ now+1h)恰回界内;
// 不带范围基线三条都在。探针纪律:finally 物理删(失败也不漏),DisposeAsync 再兜底一次。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Notice.Dto;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using AdminCore.Core.Services.Print;
using AdminCore.Core.Services.Print.Dto;
using AdminCore.Core.Services.Serial;
using AdminCore.Core.Services.Serial.Dto;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchUXDateRangeTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例 finally 已删,DisposeAsync 再兜底(幂等)
    private readonly List<long> _tenantIds = new();
    private readonly List<long> _noticeIds = new();
    private readonly List<long> _openAccessIds = new();
    private readonly List<long> _printIds = new();
    private readonly List<long> _serialIds = new();

    public BatchUXDateRangeTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_tenantIds.Count == 0 && _noticeIds.Count == 0 && _openAccessIds.Count == 0
            && _printIds.Count == 0 && _serialIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        await PhysicalDeleteAsync<SysTenant>(dbScope.Db, _tenantIds);
        await PhysicalDeleteAsync<SysNotice>(dbScope.Db, _noticeIds);
        await PhysicalDeleteAsync<SysOpenAccess>(dbScope.Db, _openAccessIds);
        await PhysicalDeleteAsync<SysPrint>(dbScope.Db, _printIds);
        await PhysicalDeleteAsync<SysSerial>(dbScope.Db, _serialIds);
    }

    // 开发库连接串(与 BatchUXBackendFilterTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 租户:CreateTime 窄窗只回界内
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 租户_时间范围只回界内()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysTenantService>();
        var marker = NewMarker();
        var now = DateTime.Now;
        var ids = new List<long>();

        try
        {
            var outA = new SysTenant { Name = $"{marker}A", CreateTime = now.AddDays(-10) };
            var outB = new SysTenant { Name = $"{marker}B", CreateTime = now.AddDays(-8) };
            var inside = new SysTenant { Name = $"{marker}C", CreateTime = now.AddHours(-1) };
            foreach (var e in new[] { outA, outB, inside })
            {
                await dbScope.Db.Insertable(e).ExecuteCommandAsync();
            }

            ids.AddRange(new[] { outA.Id, outB.Id, inside.Id });
            _tenantIds.AddRange(ids);

            var page = await svc.PageAsync(new PageTenantInput(marker, 1, 50, StartDate: now.AddDays(-2), EndDate: now.AddHours(1)));
            Assert.Equal(1, page.Total);
            Assert.Contains(page.List, t => t.Id == inside.Id);
            Assert.DoesNotContain(page.List, t => t.Id == outA.Id || t.Id == outB.Id);

            // 不带范围:三条都在(证明确实是范围在收敛,而非标记/名称筛选)
            var baseline = await svc.PageAsync(new PageTenantInput(marker, 1, 50));
            Assert.Equal(3, baseline.Total);
        }
        finally
        {
            await PhysicalDeleteAsync<SysTenant>(dbScope.Db, ids);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ② 通知公告:CreateTime 窄窗只回界内(草稿行同样可被时间范围命中)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 通知_时间范围只回界内()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysNoticeService>();
        var marker = NewMarker();
        var now = DateTime.Now;
        var ids = new List<long>();

        try
        {
            // 三条均为草稿(PublicTime=null):按 CreateTime 过滤仍能命中,正是选 CreateTime 而非 PublicTime 的原因
            var outA = NewNotice($"{marker}A", now.AddDays(-10));
            var outB = NewNotice($"{marker}B", now.AddDays(-8));
            var inside = NewNotice($"{marker}C", now.AddHours(-1));
            foreach (var e in new[] { outA, outB, inside })
            {
                await dbScope.Db.Insertable(e).ExecuteCommandAsync();
            }

            ids.AddRange(new[] { outA.Id, outB.Id, inside.Id });
            _noticeIds.AddRange(ids);

            var page = await svc.PageAsync(new PageNoticeInput(marker, 0, 1, 50, StartDate: now.AddDays(-2), EndDate: now.AddHours(1)));
            Assert.Equal(1, page.Total);
            Assert.Contains(page.List, n => n.Id == inside.Id);
            Assert.DoesNotContain(page.List, n => n.Id == outA.Id || n.Id == outB.Id);

            var baseline = await svc.PageAsync(new PageNoticeInput(marker, 0, 1, 50));
            Assert.Equal(3, baseline.Total);
        }
        finally
        {
            await PhysicalDeleteAsync<SysNotice>(dbScope.Db, ids);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 开放接口:CreateTime 窄窗只回界内
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开放接口_时间范围只回界内()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysOpenAccessService>();
        var marker = NewMarker();
        var now = DateTime.Now;
        var ids = new List<long>();

        try
        {
            var outA = NewOpenAccess($"{marker}A", now.AddDays(-10));
            var outB = NewOpenAccess($"{marker}B", now.AddDays(-8));
            var inside = NewOpenAccess($"{marker}C", now.AddHours(-1));
            foreach (var e in new[] { outA, outB, inside })
            {
                await dbScope.Db.Insertable(e).ExecuteCommandAsync();
            }

            ids.AddRange(new[] { outA.Id, outB.Id, inside.Id });
            _openAccessIds.AddRange(ids);

            var page = await svc.PageAsync(new PageOpenAccessInput(marker, 1, 50, StartDate: now.AddDays(-2), EndDate: now.AddHours(1)));
            Assert.Equal(1, page.Total);
            Assert.Contains(page.List, a => a.Id == inside.Id);
            Assert.DoesNotContain(page.List, a => a.Id == outA.Id || a.Id == outB.Id);

            var baseline = await svc.PageAsync(new PageOpenAccessInput(marker, 1, 50));
            Assert.Equal(3, baseline.Total);
        }
        finally
        {
            await PhysicalDeleteAsync<SysOpenAccess>(dbScope.Db, ids);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ④ 打印模板:CreateTime 窄窗只回界内
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 打印模板_时间范围只回界内()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPrintService>();
        var marker = NewMarker();
        var now = DateTime.Now;
        var ids = new List<long>();

        try
        {
            var outA = NewPrint($"{marker}A", now.AddDays(-10));
            var outB = NewPrint($"{marker}B", now.AddDays(-8));
            var inside = NewPrint($"{marker}C", now.AddHours(-1));
            foreach (var e in new[] { outA, outB, inside })
            {
                await dbScope.Db.Insertable(e).ExecuteCommandAsync();
            }

            ids.AddRange(new[] { outA.Id, outB.Id, inside.Id });
            _printIds.AddRange(ids);

            var page = await svc.PageAsync(new PageSysPrintInput(marker, 1, 50, StartDate: now.AddDays(-2), EndDate: now.AddHours(1)));
            Assert.Equal(1, page.Total);
            Assert.Contains(page.List, p => p.Id == inside.Id);
            Assert.DoesNotContain(page.List, p => p.Id == outA.Id || p.Id == outB.Id);

            var baseline = await svc.PageAsync(new PageSysPrintInput(marker, 1, 50));
            Assert.Equal(3, baseline.Total);
        }
        finally
        {
            await PhysicalDeleteAsync<SysPrint>(dbScope.Db, ids);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ 流水号:CreateTime 窄窗只回界内
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 流水号_时间范围只回界内()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysSerialService>();
        var marker = NewMarker();
        var now = DateTime.Now;
        var ids = new List<long>();

        try
        {
            var outA = NewSerial($"{marker}A", now.AddDays(-10));
            var outB = NewSerial($"{marker}B", now.AddDays(-8));
            var inside = NewSerial($"{marker}C", now.AddHours(-1));
            foreach (var e in new[] { outA, outB, inside })
            {
                await dbScope.Db.Insertable(e).ExecuteCommandAsync();
            }

            ids.AddRange(new[] { outA.Id, outB.Id, inside.Id });
            _serialIds.AddRange(ids);

            var page = await svc.PageAsync(new PageSerialInput(1, 50, Keyword: marker, StartDate: now.AddDays(-2), EndDate: now.AddHours(1)));
            Assert.Equal(1, page.Total);
            Assert.Contains(page.List, s => s.Id == inside.Id);
            Assert.DoesNotContain(page.List, s => s.Id == outA.Id || s.Id == outB.Id);

            var baseline = await svc.PageAsync(new PageSerialInput(1, 50, Keyword: marker));
            Assert.Equal(3, baseline.Total);
        }
        finally
        {
            await PhysicalDeleteAsync<SysSerial>(dbScope.Db, ids);
        }
    }

    private static string NewMarker() => $"uxb1d{Guid.NewGuid():N}"[..14];

    private static SysNotice NewNotice(string title, DateTime createTime) => new()
    {
        Title = title,
        Content = $"批 UX B1d 探针 {title}",
        Type = NoticeTypeEnum.Notice,
        Status = NoticeStatusEnum.Draft,
        CreateUserId = 0, // 无 HttpContext 时分页按 CreateUserId == 0 过滤(非超管路径)
        CreateTime = createTime
    };

    private static SysOpenAccess NewOpenAccess(string accessKey, DateTime createTime) => new()
    {
        AccessKey = accessKey,
        AccessSecret = $"secret-{accessKey}",
        BindTenantId = 0,
        BindUserId = 0,
        CreateTime = createTime
    };

    private static SysPrint NewPrint(string name, DateTime createTime) => new()
    {
        Name = name,
        Template = "{\"cols\":[]}",
        PrintType = PrintTypeEnum.Browser,
        Status = 1,
        OrderNo = 100,
        CreateTime = createTime
    };

    private static SysSerial NewSerial(string type, DateTime createTime) => new()
    {
        Type = type,
        Formater = "{SEQ}",
        ResetInterval = ResetIntervalEnum.Never,
        Seq = 1,
        Expy = createTime,
        Min = 1,
        Max = 1_000_000_000,
        OrderNo = 100,
        Status = 0,
        CreateTime = createTime
    };

    /// <summary>物理删除探针(走 Deleteable=物理 DELETE,非软删;幂等:ids 空/行已删均无副作用)。</summary>
    private static async Task PhysicalDeleteAsync<T>(ISqlSugarClient db, List<long> ids) where T : class, new()
    {
        if (ids.Count == 0)
        {
            return;
        }

        await db.Deleteable<T>().In(ids).ExecuteCommandAsync();
    }

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG 的最小宿主(照抄 BatchUXBackendFilterTest DbScope;五服务经 AddAutoServices 自动发现)
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
            services.AddSignalR(); // SysNoticeService → SysOnlineUserService → IHubContext(测试无连接时推送 no-op,同 BatchG/批 AF 装配)
            services.AddAutoServices(typeof(SysTenantService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchUXBackendFilterTest 口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
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