// 批 UX 后端件 B1a·Status 筛选参数测试(真实 PG,与 BatchUXBackendFilterTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
// 覆盖 4 个新增状态筛选参数,每个参数 1 条用例 = 带参只回匹配 + 不带参行为不变:
// ① PageUserInput.Status(int,0 正常/1 停用) ② PageOrgInput.Status(int,0 启用/1 禁用)
// ③ PagePosInput.Status(int,0 正常/1 停用)  ④ PageSysJobInput.Enabled(bool)
// 探针纪律:IAsyncLifetime 探针登记兜底——用例失败也不漏行;固定 Id 段 99_000_000_052_xx(批 UX-B1a 专用)。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.Job;
using AdminCore.Core.Services.Job.Dto;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.Services.Pos.Dto;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchUXStatusFilterTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例失败后 DisposeAsync 仍执行(清理幂等)
    private readonly List<long> _userIds = new();
    private readonly List<long> _orgIds = new();
    private readonly List<long> _posIds = new();
    private readonly List<long> _jobIds = new();

    // 固定探针 Id 段(99_000_000_052_xx = 批 UX-B1a);幂等重建 + 清理
    private const long UserEnabledId = 99_000_000_052_01;
    private const long UserDisabledId = 99_000_000_052_02;
    private const long OrgEnabledId = 99_000_000_052_11;
    private const long OrgDisabledId = 99_000_000_052_12;
    private const long PosEnabledId = 99_000_000_052_21;
    private const long PosDisabledId = 99_000_000_052_22;
    private const long JobEnabledId = 99_000_000_052_31;
    private const long JobDisabledId = 99_000_000_052_32;

    private const int NormalAccountType = 777;

    public BatchUXStatusFilterTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_userIds.Count == 0 && _orgIds.Count == 0 && _posIds.Count == 0 && _jobIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        await CleanupAsync(dbScope.Db, _userIds, _orgIds, _posIds, _jobIds);
    }

    // 开发库连接串(与 BatchUXBackendFilterTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 用户 Status:int(0 正常/1 停用)精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用户_Status_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"uxs{p}";

        await ResetUsersAsync(dbScope.Db, prefix);
        _userIds.AddRange(new[] { UserEnabledId, UserDisabledId });

        var filtered = await svc.PageAsync(new PageUserInput(prefix, PageSize: 200, Status: 1));
        Assert.Contains(filtered.List, u => u.Id == UserDisabledId);
        Assert.DoesNotContain(filtered.List, u => u.Id == UserEnabledId);

        // 不带参行为不变:两种状态都回
        var baseline = await svc.PageAsync(new PageUserInput(prefix, PageSize: 200));
        Assert.Contains(baseline.List, u => u.Id == UserEnabledId);
        Assert.Contains(baseline.List, u => u.Id == UserDisabledId);
    }

    // ─────────────────────────────────────────────────────────────
    // ② 机构 Status:int(0 启用/1 禁用)精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 机构_Status_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysOrgService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"uxs{p}";

        await ResetOrgsAsync(dbScope.Db, prefix);
        _orgIds.AddRange(new[] { OrgEnabledId, OrgDisabledId });

        var filtered = await svc.PageAsync(new PageOrgInput(Name: prefix, Code: null, PageSize: 200, Status: 1));
        Assert.Contains(filtered.List, o => o.Id == OrgDisabledId);
        Assert.DoesNotContain(filtered.List, o => o.Id == OrgEnabledId);

        var baseline = await svc.PageAsync(new PageOrgInput(Name: prefix, Code: null, PageSize: 200));
        Assert.Contains(baseline.List, o => o.Id == OrgEnabledId);
        Assert.Contains(baseline.List, o => o.Id == OrgDisabledId);
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 职位 Status:int(0 正常/1 停用)精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 职位_Status_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPosService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"uxs{p}";

        await ResetPosAsync(dbScope.Db, prefix);
        _posIds.AddRange(new[] { PosEnabledId, PosDisabledId });

        var filtered = await svc.PageAsync(new PagePosInput(PageSize: 200, Name: prefix, Status: 1));
        Assert.Contains(filtered.List, x => x.Id == PosDisabledId);
        Assert.DoesNotContain(filtered.List, x => x.Id == PosEnabledId);

        var baseline = await svc.PageAsync(new PagePosInput(PageSize: 200, Name: prefix));
        Assert.Contains(baseline.List, x => x.Id == PosEnabledId);
        Assert.Contains(baseline.List, x => x.Id == PosDisabledId);
    }

    // ─────────────────────────────────────────────────────────────
    // ④ 定时任务 Enabled:bool 精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 定时任务_Enabled_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysJobService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"uxs{p}";

        await ResetJobsAsync(dbScope.Db, prefix);
        _jobIds.AddRange(new[] { JobEnabledId, JobDisabledId });

        var filtered = await svc.PageAsync(new PageSysJobInput(prefix, PageSize: 200, Enabled: false));
        Assert.Contains(filtered.List, j => j.Id == JobDisabledId);
        Assert.DoesNotContain(filtered.List, j => j.Id == JobEnabledId);

        var baseline = await svc.PageAsync(new PageSysJobInput(prefix, PageSize: 200));
        Assert.Contains(baseline.List, j => j.Id == JobEnabledId);
        Assert.Contains(baseline.List, j => j.Id == JobDisabledId);
    }

    // ─────────────────────────────────────────────────────────────
    // 探针(固定 Id;先按 Id 清残留再插,幂等)
    // ─────────────────────────────────────────────────────────────
    private static async Task ResetUsersAsync(ISqlSugarClient db, string prefix)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_user WHERE id IN (@a, @b)",
            new SugarParameter("@a", UserEnabledId), new SugarParameter("@b", UserDisabledId));
        await db.Insertable(new SysUser
        {
            Id = UserEnabledId, Account = $"{prefix}a", Password = "probe-dummy-never-login",
            RealName = "UX状态探针A", Status = 0, AccountType = NormalAccountType, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
        await db.Insertable(new SysUser
        {
            Id = UserDisabledId, Account = $"{prefix}b", Password = "probe-dummy-never-login",
            RealName = "UX状态探针B", Status = 1, AccountType = NormalAccountType, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
    }

    private static async Task ResetOrgsAsync(ISqlSugarClient db, string prefix)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_org WHERE id IN (@a, @b)",
            new SugarParameter("@a", OrgEnabledId), new SugarParameter("@b", OrgDisabledId));
        await db.Insertable(new SysOrg
        {
            Id = OrgEnabledId, Pid = 0, Name = $"{prefix}a", Sort = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
        await db.Insertable(new SysOrg
        {
            Id = OrgDisabledId, Pid = 0, Name = $"{prefix}b", Sort = 1, Status = 1, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
    }

    private static async Task ResetPosAsync(ISqlSugarClient db, string prefix)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_pos WHERE id IN (@a, @b)",
            new SugarParameter("@a", PosEnabledId), new SugarParameter("@b", PosDisabledId));
        await db.Insertable(new SysPos
        {
            Id = PosEnabledId, Name = $"{prefix}a", Code = $"{prefix}a", Sort = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
        await db.Insertable(new SysPos
        {
            Id = PosDisabledId, Name = $"{prefix}b", Code = $"{prefix}b", Sort = 1, Status = 1, TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
    }

    private static async Task ResetJobsAsync(ISqlSugarClient db, string prefix)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_job_detail WHERE id IN (@a, @b)",
            new SugarParameter("@a", JobEnabledId), new SugarParameter("@b", JobDisabledId));
        await db.Insertable(new SysJobDetail
        {
            Id = JobEnabledId, JobName = $"{prefix}a", Interval = 3600, Enabled = true,
            Remark = "UX状态探针启用", TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
        await db.Insertable(new SysJobDetail
        {
            Id = JobDisabledId, JobName = $"{prefix}b", Interval = 3600, Enabled = false,
            Remark = "UX状态探针禁用", TenantId = SqlSugarConst.DefaultTenantId
        }).ExecuteCommandAsync();
    }

    // ─────────────────────────────────────────────────────────────
    // 清理(裸 SQL 物理删,与 BatchUXBackendFilterTest 同口径)
    // ─────────────────────────────────────────────────────────────
    private static async Task CleanupAsync(ISqlSugarClient db,
        IEnumerable<long> userIds, IEnumerable<long> orgIds, IEnumerable<long> posIds, IEnumerable<long> jobIds)
    {
        foreach (var id in userIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_user WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in orgIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_org WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in posIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_pos WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in jobIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_job_detail WHERE id = @id", new SugarParameter("@id", id));
        }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集),
    /// 框架表增量建(InitTables 扫 Core 程序集,幂等)。PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
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
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor()); // 无 Claims ⇒ 未登录口径(机构范围不过滤)
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config); // 无 ConnectionStrings:Redis → 内存实现
            services.AddSignalR(); // SysUserService → SysOnlineUserService → IHubContext(无连接时推送 no-op)
            services.AddAutoServices(typeof(SysUserService).Assembly);

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