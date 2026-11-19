using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
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
/// 批 CG(G29 机构操作范围服务端强制)验证——参照 Admin.NET D2009/D2003/D2010 移植 + G29 裁决补强"跨范围挂载":
/// Add:顶级创建仅超管(org.top_forbidden)/父机构须在范围内(org.parent_out_of_scope,空集 fail-closed);
/// Update:目标须在范围内(org.out_of_scope)/改挂顶级仅超管(org.top_move_forbidden)/新父须在范围内(org.parent_out_of_scope,严于参照);
/// Delete:目标须在范围内(org.out_of_scope)。超管/All 范围/未认证放行(null scope)。
/// 范围集复用 GetUserOrgScopeAsync 枢纽(与批 H 2.5 树裁剪同一口径:UI 禁用集=API 放行集)。
/// 环境:真实 PG;探针(双树 A→A1→A2 / B 顶级 + 角色 Org/OrgAndChild/All + 用户)finally 物理清理。
/// </summary>
public class BatchCGOrgScopeTest
{
    // 开发库连接串(与批 B/L 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private const int AdminAccountType = 999;
    private const int NormalAccountType = 777;

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Provider { get; }
        public FakeHttpContextAccessor FakeAccessor { get; } = new();
        public bool PgReachable { get; }

        private IServiceScope? Scope { get; set; }
        private ISqlSugarClient? Db { get; set; }
        private SysOrgService? Svc { get; set; }

        public long OrgA { get; }
        public long OrgA1 { get; }
        public long OrgA2 { get; }
        public long OrgB { get; }

        public long UserAdmin { get; }
        public long UserO { get; }
        public long UserOC { get; }
        public long UserAll { get; }
        public long UserNone { get; }

        private long RoleOrg { get; }
        private long RoleOrgAndChild { get; }
        private long RoleAll { get; }

        public Fixture()
        {
            if (!IsPostgresReachable())
            {
                Provider = new ServiceCollection().BuildServiceProvider();
                return;
            }

            var p = Guid.NewGuid().ToString("N");
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
            services.AddAutoServices(typeof(SysOrgService).Assembly);
            Provider = services.BuildServiceProvider();

            Db = Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(Db);
            Scope = Provider.CreateScope();
            Svc = Scope.ServiceProvider.GetRequiredService<SysOrgService>();
            PgReachable = true;

            var diScope = Provider.CreateScope();
            var sp = diScope.ServiceProvider;
            var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
            var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
            var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
            var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();

            // 双树:A → A1 → A2(操作者主场)/ B 顶级(范围外对照)
            OrgA = InsertOrg(orgRepo, $"t15cg_a_{p}", 0);
            OrgA1 = InsertOrg(orgRepo, $"t15cg_a1_{p}", OrgA);
            OrgA2 = InsertOrg(orgRepo, $"t15cg_a2_{p}", OrgA1);
            OrgB = InsertOrg(orgRepo, $"t15cg_b_{p}", 0);

            // 角色:Org(3)/OrgAndChild(2)/All(1)
            RoleOrg = InsertRole(roleRepo, $"t15cg_ro_{p}", DataScopeEnum.Org);
            RoleOrgAndChild = InsertRole(roleRepo, $"t15cg_roc_{p}", DataScopeEnum.OrgAndChild);
            RoleAll = InsertRole(roleRepo, $"t15cg_rall_{p}", DataScopeEnum.All);

            // 用户(探针哑哈希,不登录;OrgId 决定 Org/OrgAndChild 锚点)
            UserAdmin = InsertUser(userRepo, $"t15cg_admin_{p}", AdminAccountType, null);
            UserO = InsertUser(userRepo, $"t15cg_uo_{p}", NormalAccountType, OrgA1);
            UserOC = InsertUser(userRepo, $"t15cg_uoc_{p}", NormalAccountType, OrgA1);
            UserAll = InsertUser(userRepo, $"t15cg_uall_{p}", NormalAccountType, OrgB);
            UserNone = InsertUser(userRepo, $"t15cg_unone_{p}", NormalAccountType, OrgA1);

            BindUser(userRoleRepo, UserO, RoleOrg);
            BindUser(userRoleRepo, UserOC, RoleOrgAndChild);
            BindUser(userRoleRepo, UserAll, RoleAll);
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
            // 存量 Password 列 NOT NULL(使用指南坑 12)——探针填哑哈希,不登录
            var user = new SysUser
            {
                Account = account,
                Password = "t15cg_probe_never_login",
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

        /// <summary>设模拟登录身份(与生产 JwtClaimNames 同键,批 B/L 同口径)。</summary>
        public void SetUser(long userId, int accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, $"t15cg_{userId}"),
                new Claim(JwtClaimNames.AccountType, accountType.ToString()),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            FakeAccessor.HttpContext = context;
        }

        /// <summary>取服务(夹具单作用域持有,Dispose 统一释放)。</summary>
        public SysOrgService GetService() => Svc!;

        /// <summary>取根 DbContext(夹具持有)。</summary>
        public ISqlSugarClient GetDb() => Db!;

        /// <summary>物理清理本用例新建机构。</summary>
        public void DeleteOrgs(params long[] ids)
        {
            if (ids.Length == 0)
            {
                return;
            }

            Scope.ServiceProvider.GetRequiredService<SqlSugarRepository<SysOrg>>()
                .DeletePhysicalByAsync(u => ids.Contains(u.Id)).GetAwaiter().GetResult();
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

            var userIds = new[] { UserAdmin, UserO, UserOC, UserAll, UserNone };
            userRoleRepo.DeletePhysicalByAsync(u => userIds.Contains(u.UserId)).GetAwaiter().GetResult();
            userRepo.DeletePhysicalByAsync(u => userIds.Contains(u.Id)).GetAwaiter().GetResult();
            roleRepo.DeletePhysicalByAsync(u => new[] { RoleOrg, RoleOrgAndChild, RoleAll }.Contains(u.Id)).GetAwaiter().GetResult();
            orgRepo.DeletePhysicalByAsync(u => new[] { OrgA, OrgA1, OrgA2, OrgB }.Contains(u.Id)).GetAwaiter().GetResult();
            diScope.Dispose();
            Scope?.Dispose();
            Provider.Dispose();
        }
    }

    private static bool IsPostgresReachable()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", 5432);
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

    /// <summary>超管放行:顶级创建 + 改挂顶级均不拦截(参照 D2009/D2010 豁免面)。</summary>
    [Fact]
    public async Task 超管_顶级创建与改挂顶级放行()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserAdmin, AdminAccountType);
        var svc = f.GetService();

        long created = 0, moved = 0;
        try
        {
            // 顶级创建(非超管面会拒的面,超管放行)
            created = await svc.AddAsync(new AddOrgInput(0, "t15cg_admin_top", null));
            Assert.NotEqual(0, created);

            // 改挂顶级(D2010 面,超管放行)
            moved = await svc.AddAsync(new AddOrgInput(created, "t15cg_admin_child", null));
            Assert.NotEqual(0, moved);
            await svc.UpdateAsync(new UpdateOrgInput(moved, 0, null, null, null, null, null));
            var db = f.GetDb();
            var after = await db.Queryable<SysOrg>().FirstAsync(u => u.Id == moved);
            Assert.Equal(0, after.Pid);
        }
        finally
        {
            f.DeleteOrgs(moved, created);
        }
    }

    /// <summary>Org 范围新建:顶级拒(D2009 同型)/本机构下放行/范围外父(上级+他树)拒(D2003 同型)。</summary>
    [Fact]
    public async Task Org范围_新建顶级拒绝且本机构下放行范围外父拒绝()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserO, NormalAccountType); // 范围 = {A1}
        var svc = f.GetService();

        long okNew = 0;
        try
        {
            var exTop = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddOrgInput(0, "t15cg_o_top", null)));
            Assert.Equal(ErrorCodes.OrgTopForbidden, exTop.ErrorCode);

            // 本机构下(父=自身机构)放行
            okNew = await svc.AddAsync(new AddOrgInput(f.OrgA1, "t15cg_o_ok", null));
            Assert.NotEqual(0, okNew);

            // 上级机构 A 范围外 ⇒ 拒
            var exParent = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddOrgInput(f.OrgA, "t15cg_o_out1", null)));
            Assert.Equal(ErrorCodes.OrgParentOutOfScope, exParent.ErrorCode);

            // 他树顶级 B 范围外 ⇒ 拒
            var exOther = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddOrgInput(f.OrgB, "t15cg_o_out2", null)));
            Assert.Equal(ErrorCodes.OrgParentOutOfScope, exOther.ErrorCode);
        }
        finally
        {
            f.DeleteOrgs(okNew);
        }
    }

    /// <summary>Org 范围更新:范围外目标拒(上级+他树)/本机构改名放行。</summary>
    [Fact]
    public async Task Org范围_范围外目标拒绝且本机构改名放行()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserO, NormalAccountType); // 范围 = {A1}
        var svc = f.GetService();

        var exA = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateOrgInput(f.OrgA, null, "t15cg_o_ren_a", null, null, null, null)));
        Assert.Equal(ErrorCodes.OrgOutOfScope, exA.ErrorCode);

        var exB = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateOrgInput(f.OrgB, null, "t15cg_o_ren_b", null, null, null, null)));
        Assert.Equal(ErrorCodes.OrgOutOfScope, exB.ErrorCode);

        // 本机构改名(不传 Pid=保持挂载)放行
        await svc.UpdateAsync(new UpdateOrgInput(f.OrgA1, null, "t15cg_o_ren_ok", null, null, null, null));
        var db = f.GetDb();
        var after = await db.Queryable<SysOrg>().FirstAsync(u => u.Id == f.OrgA1);
        Assert.Equal("t15cg_o_ren_ok", after.Name);
    }

    /// <summary>Org 范围改挂:改挂顶级拒(D2010 同型)/跨范围挂载到新父拒(G29 裁决,严于参照)。</summary>
    [Fact]
    public async Task Org范围_改挂顶级拒绝且跨范围挂载拒绝()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserO, NormalAccountType); // 范围 = {A1}
        var svc = f.GetService();

        // 目标 A1 在范围内,但改挂顶级 ⇒ 拒
        var exTop = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateOrgInput(f.OrgA1, 0, null, null, null, null, null)));
        Assert.Equal(ErrorCodes.OrgTopMoveForbidden, exTop.ErrorCode);

        // 目标 A1 在范围内,但新父 B 范围外(跨范围挂载)⇒ 拒
        var exCross = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateOrgInput(f.OrgA1, f.OrgB, null, null, null, null, null)));
        Assert.Equal(ErrorCodes.OrgParentOutOfScope, exCross.ErrorCode);

        // 对照:未发生迁移(两次拒后 A1 仍挂 A 下)
        var db = f.GetDb();
        var after = await db.Queryable<SysOrg>().FirstAsync(u => u.Id == f.OrgA1);
        Assert.Equal(f.OrgA, after.Pid);
    }

    /// <summary>删除:Org 范围删范围外目标拒 / OrgAndChild 范围删自身子树叶子放行。</summary>
    [Fact]
    public async Task 删除_Org范围范围外拒绝且OrgAndChild子树叶子放行()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        var svc = f.GetService();

        f.SetUser(f.UserO, NormalAccountType); // 范围 = {A1}
        var exB = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(f.OrgB));
        Assert.Equal(ErrorCodes.OrgOutOfScope, exB.ErrorCode);

        // OrgAndChild 范围 = {A1,A2}:A2 叶子(无子节点无关联用户)在范围内 ⇒ 放行
        f.SetUser(f.UserOC, NormalAccountType);
        await svc.DeleteAsync(f.OrgA2);
        // 软删行被全局 IsDelete 过滤器滤出可查询集 ⇒ raw SQL 断言 IsDelete=true
        var db = f.GetDb();
        var softDeleted = await db.Ado.GetIntAsync($"SELECT COUNT(*) FROM sys_org WHERE id={f.OrgA2} AND \"IsDelete\"=true");
        Assert.True(softDeleted == 1, "软删生效(IsDelete=true)");
    }

    /// <summary>All 范围(非超管 AccountType)放行:顶级创建不拦截(范围枢纽返回 All ⇒ null scope)。</summary>
    [Fact]
    public async Task All范围非超管_顶级创建放行()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserAll, NormalAccountType);
        var svc = f.GetService();

        long created = 0;
        try
        {
            created = await svc.AddAsync(new AddOrgInput(0, "t15cg_all_top", null));
            Assert.NotEqual(0, created);
        }
        finally
        {
            f.DeleteOrgs(created);
        }
    }

    /// <summary>无角色用户(fail-closed):范围空集 ⇒ 新建父校验与删除目标校验全拒,不泄露不越权。</summary>
    [Fact]
    public async Task 无角色_空集failClosed全拒()
    {
        using var f = new Fixture();
        if (!f.PgReachable) { Assert.Fail(Downgrade); }
        f.SetUser(f.UserNone, NormalAccountType); // 挂 A1 但无角色 ⇒ 范围空集
        var svc = f.GetService();

        var exAdd = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddOrgInput(f.OrgA1, "t15cg_none_add", null)));
        Assert.Equal(ErrorCodes.OrgParentOutOfScope, exAdd.ErrorCode);

        var exDel = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(f.OrgA1));
        Assert.Equal(ErrorCodes.OrgOutOfScope, exDel.ErrorCode);
    }
}
