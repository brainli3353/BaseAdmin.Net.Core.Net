// 批 UX 后端筛选参数补全测试(真实 PG,与 BatchBODictTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
// 覆盖 6 个新增筛选参数,每个参数 1 条用例 = 带参只回匹配 + 不带参行为不变:
// ① PageRoleInput.Keyword(Name/Code 模糊 OR) ② PageDictTypeInput.Keyword(Name/Code)
// ③ PageDictDataInput.Keyword(Label/Value) ④ PageSysLogExInput.LogLevel(string 精确)
// ⑤ PageSysLogLoginInput.LoginType(string 精确) ⑥ PageTenantInput.Status(int,0/1 精确)
// 探针纪律(批 BM D2 机制):IAsyncLifetime 探针登记兜底——用例失败也不漏行。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Dict;
using AdminCore.Core.Services.Dict.Dto;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
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

public class BatchUXBackendFilterTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例失败后 DisposeAsync 仍执行(清理幂等)
    private readonly List<long> _roleIds = new();
    private readonly List<long> _dictTypeIds = new();
    private readonly List<long> _dictDataIds = new();
    private readonly List<long> _logExIds = new();
    private readonly List<long> _logLoginIds = new();
    private readonly List<long> _tenantIds = new();

    // 批 UX 固定探针 Id 段(日志/直接 Insertable 用;幂等重建 + 清理)
    private const long LogExErrorId = 99_000_000_051_01;
    private const long LogExWarnId = 99_000_000_051_02;
    private const long LogLoginLoginId = 99_000_000_051_11;
    private const long LogLoginLogoutId = 99_000_000_051_12;

    public BatchUXBackendFilterTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_roleIds.Count == 0 && _dictTypeIds.Count == 0 && _dictDataIds.Count == 0
            && _logExIds.Count == 0 && _logLoginIds.Count == 0 && _tenantIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        await CleanupAsync(dbScope.Db, _roleIds, _dictTypeIds, _dictDataIds, _logExIds, _logLoginIds, _tenantIds);
    }

    // 开发库连接串(与 BatchBODictTest/BatchCTenantTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 角色 Keyword:Name/Code 模糊 OR
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 角色_Keyword_Name或Code模糊()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var roleService = diScope.ServiceProvider.GetRequiredService<SysRoleService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var kw = $"kw{p}";

        // 甲:仅 Name 命中;乙:仅 Code 命中;丙:两者都不命中
        var idByName = await roleService.AddAsync(new AddRoleInput($"角色{kw}", $"nameonly{p}", "甲", null));
        var idByCode = await roleService.AddAsync(new AddRoleInput($"无关{p}", $"code{kw}", "乙", null));
        var idNone = await roleService.AddAsync(new AddRoleInput($"无关x{p}", $"zz{p}x", "丙", null));
        _roleIds.AddRange(new[] { idByName, idByCode, idNone });

        var filtered = await roleService.PageAsync(new PageRoleInput(1, 50, Keyword: kw));
        Assert.Contains(filtered.List, r => r.Id == idByName);
        Assert.Contains(filtered.List, r => r.Id == idByCode);
        Assert.DoesNotContain(filtered.List, r => r.Id == idNone);

        // 不带参行为不变:全部可见
        var baseline = await roleService.PageAsync(new PageRoleInput(1, 50));
        Assert.Contains(baseline.List, r => r.Id == idByName);
        Assert.Contains(baseline.List, r => r.Id == idByCode);
        Assert.Contains(baseline.List, r => r.Id == idNone);
    }

    // ─────────────────────────────────────────────────────────────
    // ② 字典类型 Keyword:Name/Code 模糊 OR
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 字典类型_Keyword_Name或Code模糊()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var typeService = diScope.ServiceProvider.GetRequiredService<SysDictTypeService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var kw = $"kw{p}";

        var idByName = await typeService.AddAsync(new AddDictTypeInput($"类型{kw}", $"ux.typeonly{p}", false, "甲"));
        var idByCode = await typeService.AddAsync(new AddDictTypeInput($"无关{p}", $"ux.{kw}", false, "乙"));
        var idNone = await typeService.AddAsync(new AddDictTypeInput($"无关x{p}", $"ux.zz{p}x", false, "丙"));
        _dictTypeIds.AddRange(new[] { idByName, idByCode, idNone });

        var filtered = await typeService.PageAsync(new PageDictTypeInput(1, 50, Keyword: kw));
        Assert.Contains(filtered.List, t => t.Id == idByName);
        Assert.Contains(filtered.List, t => t.Id == idByCode);
        Assert.DoesNotContain(filtered.List, t => t.Id == idNone);

        var baseline = await typeService.PageAsync(new PageDictTypeInput(1, 50));
        Assert.Contains(baseline.List, t => t.Id == idByName);
        Assert.Contains(baseline.List, t => t.Id == idByCode);
        Assert.Contains(baseline.List, t => t.Id == idNone);
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 字典值 Keyword:Label/Value 模糊 OR
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 字典值_Keyword_Label或Value模糊()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var typeService = diScope.ServiceProvider.GetRequiredService<SysDictTypeService>();
        var dataService = diScope.ServiceProvider.GetRequiredService<SysDictDataService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var kw = $"kw{p}";

        var typeId = await typeService.AddAsync(new AddDictTypeInput($"ux值类型{p}", $"ux.data{p}", false, null));
        _dictTypeIds.Add(typeId);

        // 甲:仅 Label 命中;乙:仅 Value 命中;丙:都不命中
        var idByLabel = await dataService.AddAsync(new AddDictDataInput(typeId, "a", "v-a", $"标签{kw}"));
        var idByValue = await dataService.AddAsync(new AddDictDataInput(typeId, "b", $"val{kw}", "无关标"));
        var idNone = await dataService.AddAsync(new AddDictDataInput(typeId, "c", "v-c", $"无关x{p}"));
        _dictDataIds.AddRange(new[] { idByLabel, idByValue, idNone });

        var filtered = await dataService.PageAsync(new PageDictDataInput(1, 50, typeId, Keyword: kw));
        Assert.Contains(filtered.List, d => d.Id == idByLabel);
        Assert.Contains(filtered.List, d => d.Id == idByValue);
        Assert.DoesNotContain(filtered.List, d => d.Id == idNone);

        var baseline = await dataService.PageAsync(new PageDictDataInput(1, 50, typeId));
        Assert.Contains(baseline.List, d => d.Id == idByLabel);
        Assert.Contains(baseline.List, d => d.Id == idByValue);
        Assert.Contains(baseline.List, d => d.Id == idNone);
    }

    // ─────────────────────────────────────────────────────────────
    // ④ 异常日志 LogLevel:实体列 string 精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 异常日志_LogLevel_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysLogExService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var account = $"ux{p}ex";

        // 幂等重建(固定探针 Id;失败轮次残留先清)
        await dbScope.Db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_log_ex WHERE id IN (@a, @b)",
            new SugarParameter("@a", LogExErrorId), new SugarParameter("@b", LogExWarnId));
        await dbScope.Db.Insertable(new SysLogEx
        {
            Id = LogExErrorId, Account = account, ControllerName = "UxProbe", HttpMethod = "GET", LogLevel = "Error", ThreadId = 42
        }).ExecuteCommandAsync();
        await dbScope.Db.Insertable(new SysLogEx
        {
            Id = LogExWarnId, Account = account, ControllerName = "UxProbe", HttpMethod = "GET", LogLevel = "Warning", ThreadId = 42
        }).ExecuteCommandAsync();
        _logExIds.AddRange(new[] { LogExErrorId, LogExWarnId });

        var filtered = await svc.PageAsync(new PageSysLogExInput(account, null, null, null, null, 1, 50, LogLevel: "Error"));
        Assert.Contains(filtered.List, l => l.Id == LogExErrorId);
        Assert.DoesNotContain(filtered.List, l => l.Id == LogExWarnId);

        var baseline = await svc.PageAsync(new PageSysLogExInput(account, null, null, null, null, 1, 50));
        Assert.Contains(baseline.List, l => l.Id == LogExErrorId);
        Assert.Contains(baseline.List, l => l.Id == LogExWarnId);
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ 登录日志 LoginType:实体列 string 精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录日志_LoginType_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysLogLoginService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var account = $"ux{p}lg";

        await dbScope.Db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_log_login WHERE id IN (@a, @b)",
            new SugarParameter("@a", LogLoginLoginId), new SugarParameter("@b", LogLoginLogoutId));
        await dbScope.Db.Insertable(new SysLogLogin
        {
            Id = LogLoginLoginId, TenantId = SqlSugarConst.DefaultTenantId, Account = account,
            Status = true, Message = "探针登录", LoginType = "login"
        }).ExecuteCommandAsync();
        await dbScope.Db.Insertable(new SysLogLogin
        {
            Id = LogLoginLogoutId, TenantId = SqlSugarConst.DefaultTenantId, Account = account,
            Status = true, Message = "探针登出", LoginType = "logout"
        }).ExecuteCommandAsync();
        _logLoginIds.AddRange(new[] { LogLoginLoginId, LogLoginLogoutId });

        var filtered = await svc.PageAsync(new PageSysLogLoginInput(1, 50, account, LoginType: "login"));
        Assert.Contains(filtered.List, l => l.Id == LogLoginLoginId);
        Assert.DoesNotContain(filtered.List, l => l.Id == LogLoginLogoutId);

        var baseline = await svc.PageAsync(new PageSysLogLoginInput(1, 50, account));
        Assert.Contains(baseline.List, l => l.Id == LogLoginLoginId);
        Assert.Contains(baseline.List, l => l.Id == LogLoginLogoutId);
    }

    // ─────────────────────────────────────────────────────────────
    // ⑥ 租户 Status:实体列 int(0=启用 1=停用)精确匹配
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 租户_Status_精确匹配()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var tenantService = diScope.ServiceProvider.GetRequiredService<SysTenantService>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var name = $"ux{p}t";

        var idEnabled = await tenantService.AddAsync(new AddTenantInput($"{name}a", null, null, null, null));
        var idDisabled = await tenantService.AddAsync(new AddTenantInput($"{name}b", null, null, null, null));
        _tenantIds.AddRange(new[] { idEnabled, idDisabled });
        await tenantService.SetStatusAsync(new SetTenantStatusInput(idDisabled, 1));

        var filtered = await tenantService.PageAsync(new PageTenantInput(name, Status: 1));
        Assert.Contains(filtered.List, t => t.Id == idDisabled);
        Assert.DoesNotContain(filtered.List, t => t.Id == idEnabled);

        var baseline = await tenantService.PageAsync(new PageTenantInput(name));
        Assert.Contains(baseline.List, t => t.Id == idEnabled);
        Assert.Contains(baseline.List, t => t.Id == idDisabled);
    }

    // ─────────────────────────────────────────────────────────────
    // 清理(裸 SQL 物理删,与 BatchBODictTest 同口径)
    // ─────────────────────────────────────────────────────────────
    private static async Task CleanupAsync(ISqlSugarClient db,
        IEnumerable<long> roleIds, IEnumerable<long> dictTypeIds, IEnumerable<long> dictDataIds,
        IEnumerable<long> logExIds, IEnumerable<long> logLoginIds, IEnumerable<long> tenantIds)
    {
        foreach (var id in dictDataIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_dict_data WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in dictTypeIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_dict_type WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in roleIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_role WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in logExIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_log_ex WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in logLoginIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_log_login WHERE id = @id", new SugarParameter("@id", id));
        }
        foreach (var id in tenantIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_tenant WHERE id = @id", new SugarParameter("@id", id));
        }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
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
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysRoleService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchBODictTest 口径)。</summary>
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