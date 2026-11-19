using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 D 5.1/5.18 日志闭环测试(真实 PG,与 BatchCTenantTest 同源 DbScope 样板;沙箱不可达时诚实降级 Assert.Fail):
/// ① 登录一阶段失败 → sys_log_login 落 Status=false(账号/消息/类型);
/// ② 登录两阶段成功 → Status=true「登录成功」;
/// ③ 登出 → LoginType=logout「登出」;
/// ④ 操作日志查询端点(SysLogOpService.PageAsync):Account 精确/ControllerName 模糊/Success/时间范围过滤;
/// ⑤ 登录日志查询端点(SysLogLoginService.PageAsync):Account 过滤命中。
/// </summary>
public class BatchDLogTest
{
    private readonly ITestOutputHelper _output;

    public BatchDLogTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // ─────────────────────────────────────────────────────────────
    // ① 登录一阶段失败 → 登录日志(Status=false)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录失败_落登录日志()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var logRepo = sp.GetRequiredService<SqlSugarRepository<SysLogLogin>>();
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}lg", null, null, null, null));
        var user = await InsertLoginUserAsync(userRepo, $"u{p}lg", password, tenantId);

        // 一阶段错密码 → 失败
        var r = await authService.LoginAsync(user.Account, "WrongPass1!");
        Assert.False(r.Success);
        Assert.Equal(ErrorCodes.AuthLoginFailed, r.ErrorCode); // 批 AQ:Error 不再承载文案,改断言稳定错误码

        // 登录日志落库(Status=false + 账号 + 消息 + 类型)
        var log = await logRepo.GetFirstAsync(l => l.Account == user.Account && l.Message == "账号或密码错误");
        Assert.NotNull(log);
        Assert.False(log!.Status);
        Assert.Equal("login", log.LoginType);
        Assert.True(log.CreateTime >= DateTime.Now.AddMinutes(-1)); // 时间合理(审计字段 AOP 填)

        await CleanupLogsAsync(sp, log.Id);
        await CleanupAsync(sp, new[] { tenantId }, new[] { user.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ② 登录两阶段成功 → 登录日志(Status=true「登录成功」)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录成功_落登录日志()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var logRepo = sp.GetRequiredService<SqlSugarRepository<SysLogLogin>>();
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}ok", null, null, null, null));
        var user = await InsertLoginUserAsync(userRepo, $"u{p}ok", password, tenantId);

        // 一阶段 → 二阶段(与 BatchCTenantTest ⑥ 同流程)
        var r1 = await authService.LoginAsync(user.Account, password);
        Assert.True(r1.Success);
        var code = new Totp(Base32Encoding.ToBytes(r1.Data!.Secret!)).ComputeTotp();
        var r2 = await authService.LoginOtpAsync(r1.Data.ChallengeId, code);
        Assert.True(r2.Success);

        // 成功日志(Status=true + 「登录成功」)
        var log = await logRepo.GetFirstAsync(l => l.Account == user.Account && l.Message == "登录成功");
        Assert.NotNull(log);
        Assert.True(log!.Status);
        Assert.Equal("login", log.LoginType);

        await CleanupLogsAsync(sp, log.Id);
        await CleanupAsync(sp, new[] { tenantId }, new[] { user.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 登出 → 登录日志(LoginType=logout)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登出_落登录日志()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var logRepo = sp.GetRequiredService<SqlSugarRepository<SysLogLogin>>();
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}lg2", null, null, null, null));
        var user = await InsertLoginUserAsync(userRepo, $"u{p}lg2", password, tenantId);

        var r1 = await authService.LoginAsync(user.Account, password);
        Assert.True(r1.Success);
        var code = new Totp(Base32Encoding.ToBytes(r1.Data!.Secret!)).ComputeTotp();
        var r2 = await authService.LoginOtpAsync(r1.Data.ChallengeId, code);
        Assert.True(r2.Success);

        // 登出(服务自行剥 Bearer 前缀,测试直传 token 串)
        await authService.LogoutAsync($"Bearer {r2.Token}");

        var log = await logRepo.GetFirstAsync(l => l.Account == user.Account && l.LoginType == "logout");
        Assert.NotNull(log);
        Assert.True(log!.Status);
        Assert.Equal("登出", log.Message);

        await CleanupLogsAsync(sp, log.Id);
        await CleanupAsync(sp, new[] { tenantId }, new[] { user.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ④ 操作日志查询端点:多条件过滤
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 操作日志_分页查询_条件过滤()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var logOpService = sp.GetRequiredService<SysLogOpService>();
        var logOpRepo = sp.GetRequiredService<SqlSugarRepository<SysLogOp>>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var account = $"u{p}op";

        // 造三条:两成功(SysUser 控制器)、一失败(SysRole 控制器)
        var ids = new List<long>();
        foreach (var (ctrl, success) in new[] { ("SysUser", true), ("SysUser", false), ("SysRole", true) })
        {
            var row = new SysLogOp
            {
                ControllerName = ctrl,
                ActionName = "Page",
                HttpMethod = "GET",
                RemoteIp = "127.0.0.1",
                ElapsedMs = 1,
                Account = account,
                Success = success
            };
            await logOpRepo.InsertAsync(row);
            ids.Add(row.Id);
        }

        // 全量:Account 精确命中 3 条
        var page = await logOpService.PageAsync(new PageSysLogOpInput(account, null, null, null, null));
        Assert.Equal(3, page.Total);

        // ControllerName 模糊:SysUser → 2 条
        var page2 = await logOpService.PageAsync(new PageSysLogOpInput(account, "SysUser", null, null, null));
        Assert.Equal(2, page2.Total);

        // Success=false → 1 条
        var page3 = await logOpService.PageAsync(new PageSysLogOpInput(null, null, false, null, null));
        Assert.True(page3.Total >= 1); // 库内可能有历史失败日志,只断言含本条
        Assert.Contains(page3.List, l => l.Id == ids[1]);

        // 时间范围:未来起点 → 0 条
        var page4 = await logOpService.PageAsync(new PageSysLogOpInput(account, null, null, DateTime.Now.AddHours(1), null));
        Assert.Equal(0, page4.Total);

        await CleanupOpLogsAsync(sp, ids);
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ 登录日志查询端点:Account 过滤命中
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录日志_分页查询_账号过滤()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var logLoginService = sp.GetRequiredService<SysLogLoginService>();
        var logRepo = sp.GetRequiredService<SqlSugarRepository<SysLogLogin>>();
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}q", null, null, null, null));
        var user = await InsertLoginUserAsync(userRepo, $"u{p}q", password, tenantId);

        // 错密码 → 失败日志
        var r = await authService.LoginAsync(user.Account, "WrongPass1!");
        Assert.False(r.Success);

        // 查询端点:Account 精确命中本条(位置参数序:PageIndex,PageSize,Account,StartTime,EndTime,Status)
        var page = await logLoginService.PageAsync(new PageSysLogLoginInput(1, 10, user.Account));
        Assert.True(page.Total >= 1);
        Assert.Contains(page.List, l => l.Account == user.Account && l.Message == "账号或密码错误");

        // Status=false 过滤命中
        var page2 = await logLoginService.PageAsync(new PageSysLogLoginInput(1, 10, user.Account, null, null, false));
        Assert.True(page2.Total >= 1);

        var log = await logRepo.GetFirstAsync(l => l.Account == user.Account && l.Message == "账号或密码错误");
        await CleanupLogsAsync(sp, log!.Id);
        await CleanupAsync(sp, new[] { tenantId }, new[] { user.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助(与 BatchCTenantTest 同源样板)
    // ─────────────────────────────────────────────────────────────

    private static async Task<SysUser> InsertLoginUserAsync(SqlSugarRepository<SysUser> userRepo, string account, string password, long? tenantId)
    {
        var u = new SysUser
        {
            Account = account,
            Password = Pbkdf2Helper.HashPassword(password),
            RealName = "x",
            Status = 0,
            AccountType = 777,
            TenantId = tenantId
        };
        await userRepo.InsertAsync(u);
        return u;
    }

    private static async Task CleanupLogsAsync(IServiceProvider sp, long logId)
    {
        var logRepo = sp.GetRequiredService<SqlSugarRepository<SysLogLogin>>();
        await logRepo.DeletePhysicalByAsync(l => l.Id == logId);
    }

    private static async Task CleanupOpLogsAsync(IServiceProvider sp, List<long> ids)
    {
        var logOpRepo = sp.GetRequiredService<SqlSugarRepository<SysLogOp>>();
        if (ids.Count > 0)
        {
            await logOpRepo.DeletePhysicalByAsync(l => ids.Contains(l.Id));
        }
    }

    private static async Task CleanupAsync(IServiceProvider sp, IEnumerable<long> tenantIds, IEnumerable<long> userIds)
    {
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var otpRepo = sp.GetRequiredService<SqlSugarRepository<SysUserOtp>>();

        var tids = tenantIds.Where(id => id != SqlSugarConst.DefaultTenantId).ToList();
        if (tids.Count > 0)
        {
            await tenantRepo.DeletePhysicalByAsync(t => tids.Contains(t.Id));
        }

        var uids = userIds.ToList();
        if (uids.Count > 0)
        {
            // 两阶段登录(LoginOtpAsync)会为探针用户落 OTP 绑定行:先删绑定再删用户,否则孤儿行逐次累积(批 BG 实测 56 行存量)
            await otpRepo.DeletePhysicalByAsync(o => uids.Contains(o.UserId));
            await userRepo.DeletePhysicalByAsync(u => uids.Contains(u.Id));
        }
    }

    /// <summary>DB 测试客户端(与 BatchCTenantTest 同源样板;Jwt 段供两阶段登录签发 token)。</summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

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
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(AuthService).Assembly);
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
