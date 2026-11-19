using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 L(2.1 用户分页机构范围约束)测试(参照 SysUserService.Page 移植,裁决见 g6 §5):
/// 超管全量/OrgAndChild 子树/Org 仅本部门/Define 授权∪所属机构/Self 无角色未挂机构空集(fail-closed)/
/// 指定机构子树交集(含越权指定 → 交集为空)/超管指定机构子树全量。
/// 断言口径:探针账号"含/不含"(开发库存量用户不干扰,不断言总计数)。
/// 环境:真实 PG;探针(机构树 A→A1→A2+角色+用户+授权行)finally 清理。
/// </summary>
public class BatchLOrgScopeTest
{
    // 开发库连接串(与批 B 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private const int AdminAccountType = 999;
    private const int NormalAccountType = 777;

    private sealed class Fixture : IDisposable
    {
        public ISqlSugarClient Db { get; }
        public ServiceProvider Provider { get; }
        public FakeHttpContextAccessor FakeAccessor { get; } = new();

        public long OrgA { get; }
        public long OrgA1 { get; }
        public long OrgA2 { get; }

        public long RoleOrgAndChild { get; }
        public long RoleOrg { get; }
        public long RoleDefine { get; }

        public long UserAdmin { get; }
        public long UserA { get; }
        public long UserA1 { get; }
        public long UserA2 { get; }
        public long UserNone { get; }
        public long UserOC { get; }
        public long UserO { get; }
        public long UserD { get; }
        public long UserS { get; }

        public string? P { get; }

        public bool PgReachable { get; }

        public Fixture()
        {
            if (!IsPostgresReachable())
            {
                Provider = new ServiceCollection().BuildServiceProvider();
                Db = null!;
                return;
            }

            P = Guid.NewGuid().ToString("N");
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
            services.AddSignalR();
            services.AddAutoServices(typeof(SysUserService).Assembly);
            Provider = services.BuildServiceProvider();
            Db = Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;

            var diScope = Provider.CreateScope();
            var sp = diScope.ServiceProvider;
            var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
            var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
            var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
            var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
            var roleOrgRepo = sp.GetRequiredService<SqlSugarRepository<SysRoleOrg>>();

            // 机构树 A → A1 → A2
            OrgA = InsertOrg(orgRepo, $"t13l_a_{P}", 0);
            OrgA1 = InsertOrg(orgRepo, $"t13l_a1_{P}", OrgA);
            OrgA2 = InsertOrg(orgRepo, $"t13l_a2_{P}", OrgA1);

            // 角色:OrgAndChild(2)/Org(3)/Define(5)
            RoleOrgAndChild = InsertRole(roleRepo, $"t13l_roc_{P}", DataScopeEnum.OrgAndChild);
            RoleOrg = InsertRole(roleRepo, $"t13l_ro_{P}", DataScopeEnum.Org);
            RoleDefine = InsertRole(roleRepo, $"t13l_rd_{P}", DataScopeEnum.Define);
            roleOrgRepo.InsertAsync(new SysRoleOrg { RoleId = RoleDefine, OrgId = OrgA2, TenantId = SqlSugarConst.DefaultTenantId }).GetAwaiter().GetResult();

            // 用户
            UserAdmin = InsertUser(userRepo, $"t13l_admin_{P}", AdminAccountType, null);
            UserA = InsertUser(userRepo, $"t13l_ua_{P}", NormalAccountType, OrgA);
            UserA1 = InsertUser(userRepo, $"t13l_ua1_{P}", NormalAccountType, OrgA1);
            UserA2 = InsertUser(userRepo, $"t13l_ua2_{P}", NormalAccountType, OrgA2);
            UserNone = InsertUser(userRepo, $"t13l_unone_{P}", NormalAccountType, null);
            UserOC = InsertUser(userRepo, $"t13l_uoc_{P}", NormalAccountType, OrgA1);
            UserO = InsertUser(userRepo, $"t13l_uo_{P}", NormalAccountType, OrgA1);
            UserD = InsertUser(userRepo, $"t13l_ud_{P}", NormalAccountType, OrgA1);
            UserS = InsertUser(userRepo, $"t13l_us_{P}", NormalAccountType, null);

            BindUser(userRoleRepo, UserOC, RoleOrgAndChild);
            BindUser(userRoleRepo, UserO, RoleOrg);
            BindUser(userRoleRepo, UserD, RoleDefine);
            diScope.Dispose();
        }

        private static long InsertOrg(ISqlSugarRepository<SysOrg> repo, string name, long pid)
        {
            var org = new SysOrg { Name = name, Pid = pid, TenantId = SqlSugarConst.DefaultTenantId };
            repo.InsertAsync(org).GetAwaiter().GetResult();
            return org.Id;
        }

        private static long InsertRole(ISqlSugarRepository<SysRole> repo, string name, DataScopeEnum scope)
        {
            var role = new SysRole { Name = name, Code = name, DataScope = (int)scope, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
            repo.InsertAsync(role).GetAwaiter().GetResult();
            return role.Id;
        }

        private static long InsertUser(ISqlSugarRepository<SysUser> repo, string account, int accountType, long? orgId)
        {
            // 存量 Password 列 NOT NULL(CodeFirst string 默认,使用指南坑 12)——探针填哑哈希,不登录
            var user = new SysUser
            {
                Account = account,
                Password = "t13l_probe_never_login",
                RealName = account,
                AccountType = accountType,
                Status = 0,
                OrgId = orgId,
                TenantId = SqlSugarConst.DefaultTenantId
            };
            repo.InsertAsync(user).GetAwaiter().GetResult();
            return user.Id;
        }

        private static void BindUser(ISqlSugarRepository<SysUserRole> repo, long userId, long roleId)
        {
            repo.InsertAsync(new SysUserRole { UserId = userId, RoleId = roleId, TenantId = SqlSugarConst.DefaultTenantId }).GetAwaiter().GetResult();
        }

        /// <summary>设模拟登录身份(与生产 JwtClaimNames 同键,批 B 同口径)。</summary>
        public void SetUser(long userId, int accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, $"t13l_{userId}"),
                new Claim(JwtClaimNames.AccountType, accountType.ToString()),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            FakeAccessor.HttpContext = context;
        }

        /// <summary>分页取账号集(探针账号断言用;开发库存量用户不干扰"含/不含"断言)。</summary>
        public async Task<HashSet<string>> PageAccountsAsync(long? orgId = null)
        {
            var diScope = Provider.CreateScope();
            var userService = diScope.ServiceProvider.GetRequiredService<SysUserService>();
            var page = await userService.PageAsync(new PageUserInput(null, 1, 200, orgId));
            diScope.Dispose();
            return page.List.Select(u => u.Account!).ToHashSet();
        }

        public void Dispose()
        {
            if (!PgReachable)
            {
                Provider.Dispose();
                return;
            }

            var diScope = Provider.CreateScope();
            var sp = diScope.ServiceProvider;
            var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
            var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
            var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
            var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
            var roleOrgRepo = sp.GetRequiredService<SqlSugarRepository<SysRoleOrg>>();

            var userIds = new[] { UserAdmin, UserA, UserA1, UserA2, UserNone, UserOC, UserO, UserD, UserS };
            userRoleRepo.DeletePhysicalByAsync(u => userIds.Contains(u.UserId)).GetAwaiter().GetResult();
            userRepo.DeletePhysicalByAsync(u => userIds.Contains(u.Id)).GetAwaiter().GetResult();
            roleOrgRepo.DeletePhysicalByAsync(u => u.RoleId == RoleDefine).GetAwaiter().GetResult();
            roleRepo.DeletePhysicalByAsync(u => new[] { RoleOrgAndChild, RoleOrg, RoleDefine }.Contains(u.Id)).GetAwaiter().GetResult();
            orgRepo.DeletePhysicalByAsync(u => new[] { OrgA, OrgA1, OrgA2 }.Contains(u.Id)).GetAwaiter().GetResult();
            diScope.Dispose();
            Provider.Dispose();
        }
    }

    [Fact]
    public async Task 超管_全量可见()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserAdmin, (int)AdminAccountType);
        var accounts = await f.PageAccountsAsync();
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua_")), "应含 A 直属用户");
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua1_")), "应含 A1 用户");
        Assert.True(accounts.Any(a => a.StartsWith("t13l_unone_")), "应含未挂机构用户(超管无范围限制)");
    }

    [Fact]
    public async Task 超管_指定机构_子树全量()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserAdmin, (int)AdminAccountType);
        var accounts = await f.PageAccountsAsync(f.OrgA1);
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua1_")), "应含 A1 用户");
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua2_")), "应含 A2 用户(子树)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_ua_") && !a.StartsWith("t13l_ua1_")), "不含 A 直属用户");
    }

    [Fact]
    public async Task OrgAndChild范围_子树可见不含上级()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserOC, NormalAccountType);
        var accounts = await f.PageAccountsAsync();
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua1_")), "应含 A1 用户(本部门)");
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua2_")), "应含 A2 用户(子机构)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_ua_") && !a.StartsWith("t13l_ua1_")), "不含 A 直属用户(上级)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_unone_")), "不含未挂机构用户");
    }

    [Fact]
    public async Task Org范围_仅本部门可见()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserO, NormalAccountType);
        var accounts = await f.PageAccountsAsync();
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua1_")), "应含 A1 用户(本部门)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_ua2_")), "不含 A2 用户(子机构)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_ua_") && !a.StartsWith("t13l_ua1_")), "不含 A 直属用户(上级)");
    }

    [Fact]
    public async Task Define范围_授权机构并所属机构可见()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserD, NormalAccountType);
        var accounts = await f.PageAccountsAsync();
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua2_")), "应含 A2 用户(Define 授权机构)");
        Assert.True(accounts.Any(a => a.StartsWith("t13l_ua1_")), "应含 A1 用户(所属机构,参照恒含本人机构)");
        Assert.False(accounts.Any(a => a.StartsWith("t13l_ua_") && !a.StartsWith("t13l_ua1_")), "不含 A 直属用户");
    }

    [Fact]
    public async Task Self无角色未挂机构_空结果failClosed()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserS, NormalAccountType);
        var accounts = await f.PageAccountsAsync();
        Assert.False(accounts.Any(a => a.StartsWith("t13l_u")), "无范围未挂机构 → 看不到任何探针用户(fail-closed)");
    }

    [Fact]
    public async Task 指定机构_子树交集_越权指定空结果()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        // U_O 范围 = {A1};指定 A1 → 子树 {A1,A2} ∩ {A1} = {A1} → 仅 U_A1
        f.SetUser(f.UserO, NormalAccountType);
        var inScope = await f.PageAccountsAsync(f.OrgA1);
        Assert.True(inScope.Any(a => a.StartsWith("t13l_ua1_")), "交集内含 A1 用户");
        Assert.False(inScope.Any(a => a.StartsWith("t13l_ua2_")), "交集不含 A2 用户(不在范围集)");

        // 指定 A2 → 子树 {A2} ∩ {A1} = ∅ → 空结果(越权指定机构不泄露数据)
        var outOfScope = await f.PageAccountsAsync(f.OrgA2);
        Assert.False(outOfScope.Any(a => a.StartsWith("t13l_u")), "越权指定机构交集为空 → 空结果");
    }

    [Fact]
    public async Task 正常用户_分页参数兼容_账号过滤仍生效()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过"); }
        f.SetUser(f.UserOC, NormalAccountType);
        var diScope = f.Provider.CreateScope();
        var userService = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var page = await userService.PageAsync(new PageUserInput("t13l_ua1_", 1, 50));
        diScope.Dispose();
        Assert.True(page.List.Count > 0, "账号过滤命中 A1 用户");
        Assert.All(page.List, u => Assert.Contains("t13l_ua1_", u.Account!));
    }

    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(DbHost, DbPort);
            return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
