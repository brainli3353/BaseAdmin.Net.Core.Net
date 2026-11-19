using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
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
/// 批 B 数据权限收口测试:
/// ① B1 用户机构/职位写入路径:AddAsync 带机构/职位 → OrgId/PosId 落库 + Org 范围收敛非空;
///    UpdateAsync 机构变更 → 数据权限缓存即时失效 + 范围更新;不存在的机构/职位 → 拒绝(防悬挂引用)。
/// ② B2 Define 自定义范围:GrantOrgAsync 全量替换 → GetUserOrgScopeAsync 读 SysRoleOrg 机构集;
///    越权防护(非超管授自身范围集外机构 → 拒绝;集内 → 成功);端到端(Define 范围 → 过滤 → 仅自定义机构行可见)。
/// 环境:依赖 PostgreSQL(与 DataScopeTest 同源连接串);沙箱不可达时用例诚实降级(Assert.Fail 显式声明)。
/// </summary>
public class BatchBDataScopeTest
{
    private readonly ITestOutputHelper _output;

    public BatchBDataScopeTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 DataScopeTest 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>
    /// ①B1 新建用户带机构/职位:OrgId/PosId 落库;Org(3) 范围收敛为 [用户机构] 非空;UserOutput 投影含归属。
    /// </summary>
    [Fact]
    public async Task B1_新建用户带机构职位_归属落库且Org范围收敛非空()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var posRepo = sp.GetRequiredService<SqlSugarRepository<SysPos>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var userService = sp.GetRequiredService<SysUserService>();
        var orgService = sp.GetRequiredService<SysOrgService>();

        var prefix = Guid.NewGuid().ToString("N");
        var org = new SysOrg { Name = $"t13b_org_{prefix}", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var pos = new SysPos { Name = $"t13b_pos_{prefix}", Code = $"t13b_pos_{prefix}", Sort = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var role = new SysRole { Name = $"t13b_role_{prefix}", Code = $"t13b_role_{prefix}", DataScope = 3, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await orgRepo.InsertAsync(org);
        await posRepo.InsertAsync(pos);
        await roleRepo.InsertAsync(role);

        var userId = 0L;
        try
        {
            // B1:AddAsync 带 OrgId + PosId(角色挂接一并传入)
            userId = await userService.AddAsync(
                new AddUserInput($"t13b_user_{prefix}", "Abc123!x", "测试", 0, new List<long> { role.Id }, org.Id, pos.Id));

            var saved = await userRepo.GetFirstAsync(u => u.Id == userId);
            Assert.Equal(org.Id, saved!.OrgId);
            Assert.Equal(pos.Id, saved.PosId);

            // 范围收敛:Org(3) → [用户机构] 非空(B1 前 OrgId 恒 null → 恒空集,本用例即验证收口)
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(userId);
            Assert.Equal(DataScopeEnum.Org, maxScope);
            Assert.Equal(new List<long> { org.Id }, orgIds);

            // UserOutput 投影含 OrgId/PosId(前端编辑回显)
            var page = await userService.PageAsync(new PageUserInput($"t13b_user_{prefix}", 1, 50));
            var row = Assert.Single(page.List, u => u.Id == userId);
            Assert.Equal(org.Id, row.OrgId);
            Assert.Equal(pos.Id, row.PosId);
        }
        finally
        {
            await CleanupAsync(sp, new[] { userId }, new[] { org.Id }, new[] { pos.Id }, new[] { role.Id });
        }
    }

    /// <summary>
    /// ①B1 更新用户机构变更:部分更新落库 + 数据权限缓存即时失效(变更前命中旧值,变更后收敛新机构)。
    /// </summary>
    [Fact]
    public async Task B1_更新用户机构变更_缓存即时失效且范围收敛新机构()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var userService = sp.GetRequiredService<SysUserService>();
        var orgService = sp.GetRequiredService<SysOrgService>();

        var prefix = Guid.NewGuid().ToString("N");
        var orgA = new SysOrg { Name = $"t13b_org_{prefix}_a", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgB = new SysOrg { Name = $"t13b_org_{prefix}_b", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var role = new SysRole { Name = $"t13b_role_{prefix}", Code = $"t13b_role_{prefix}", DataScope = 3, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await roleRepo.InsertAsync(role);

        // 直接建用户(挂 orgA)+ 角色(Org 范围)
        var user = new SysUser { Account = $"t13b_user_{prefix}", Password = "x", RealName = "测试", OrgId = orgA.Id, TenantId = SqlSugarConst.DefaultTenantId };
        await userRepo.InsertAsync(user);
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        try
        {
            // 基线:Org 范围 → [orgA]
            var first = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Org, first.MaxScope);
            Assert.Equal(new List<long> { orgA.Id }, first.OrgIds);

            // B1:UpdateAsync 改机构 orgA→orgB(部分更新,PosId 不动)
            await userService.UpdateAsync(new UpdateUserInput(user.Id, user.Account!, user.RealName, null, orgB.Id, null));

            var saved = await userRepo.GetFirstAsync(u => u.Id == user.Id);
            Assert.Equal(orgB.Id, saved!.OrgId);

            // 服务已即时失效缓存 → 收敛新机构 [orgB](若未失效则缓存命中仍返 [orgA])
            var after = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Org, after.MaxScope);
            Assert.Equal(new List<long> { orgB.Id }, after.OrgIds);
        }
        finally
        {
            await CleanupAsync(sp, new[] { user.Id }, new[] { orgA.Id, orgB.Id }, Array.Empty<long>(), new[] { role.Id });
        }
    }

    /// <summary>①B1 存在性校验:AddAsync/UpdateAsync 传入不存在的机构/职位 → 拒绝(防悬挂引用)。</summary>
    [Fact]
    public async Task B1_机构职位不存在_AddUpdate均拒绝()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var userService = sp.GetRequiredService<SysUserService>();

        var prefix = Guid.NewGuid().ToString("N");
        const long GhostOrg = 999999999999999L;
        const long GhostPos = 999999999999888L;

        var userId = 0L;
        try
        {
            // AddAsync 传幽灵机构 → 拒绝
            await Assert.ThrowsAsync<AppException>(() =>
                userService.AddAsync(new AddUserInput($"t13b_user_{prefix}", "Abc123!x", "测试", 0, null, GhostOrg, null)));

            // 先建一个真实用户,再 UpdateAsync 传幽灵职位 → 拒绝
            var user = new SysUser { Account = $"t13b_user_{prefix}", Password = "x", RealName = "测试", TenantId = SqlSugarConst.DefaultTenantId };
            await userRepo.InsertAsync(user);
            userId = user.Id;
            await Assert.ThrowsAsync<AppException>(() =>
                userService.UpdateAsync(new UpdateUserInput(user.Id, user.Account!, user.RealName, null, null, GhostPos)));
        }
        finally
        {
            await CleanupAsync(sp, new[] { userId }, Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>());
        }
    }

    /// <summary>
    /// ②B2 Define 自定义范围:超管授权机构集 → 该角色用户 GetUserOrgScopeAsync 收敛 Define(5) + 授权机构集(读 SysRoleOrg)。
    /// </summary>
    [Fact]
    public async Task B2_Define授权_收敛角色机构集()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var roleService = sp.GetRequiredService<SysRoleService>();
        var orgService = sp.GetRequiredService<SysOrgService>();

        var prefix = Guid.NewGuid().ToString("N");
        var orgA = new SysOrg { Name = $"t13b_org_{prefix}_a", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgB = new SysOrg { Name = $"t13b_org_{prefix}_b", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgC = new SysOrg { Name = $"t13b_org_{prefix}_c", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.InsertAsync(orgC);
        var defineRole = new SysRole { Name = $"t13b_role_{prefix}", Code = $"t13b_role_{prefix}", DataScope = 5, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await roleRepo.InsertAsync(defineRole);

        var user = new SysUser { Account = $"t13b_user_{prefix}", Password = "x", RealName = "测试", TenantId = SqlSugarConst.DefaultTenantId };
        await userRepo.InsertAsync(user);
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = defineRole.Id });

        try
        {
            // 超管(999)授权 [A, B](越权防护豁免)
            dbScope.FakeAccessor.HttpContext = UserContext(1, "superadmin", "999");
            await roleService.GrantOrgAsync(new GrantOrgInput(defineRole.Id, new List<long> { orgA.Id, orgB.Id }));

            // 回读:该角色自定义机构集 = [A, B]
            var granted = await roleService.GetOrgListAsync(defineRole.Id);
            Assert.Equal(new List<long> { orgA.Id, orgB.Id }, granted.OrderBy(x => x).ToList());

            // 该角色用户范围收敛:Define(5) + [A, B](读 SysRoleOrg;B2 前恒空集 fail-closed)
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Define, maxScope);
            Assert.Equal(new List<long> { orgA.Id, orgB.Id }, orgIds.OrderBy(x => x).ToList());
        }
        finally
        {
            dbScope.FakeAccessor.HttpContext = null;
            await CleanupAsync(sp, new[] { user.Id }, new[] { orgA.Id, orgB.Id, orgC.Id }, Array.Empty<long>(), new[] { defineRole.Id });
        }
    }

    /// <summary>
    /// ②B2 越权防护:非超管(OrgAndChild 锚 A,可见 {A, B})授予集外机构 C → 拒绝;授予集内机构 A → 成功。
    /// </summary>
    [Fact]
    public async Task B2_越权防护_集外机构拒绝集内成功()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var roleService = sp.GetRequiredService<SysRoleService>();

        var prefix = Guid.NewGuid().ToString("N");
        // 机构树:A(根)→ B(A 子);C 独立根(集外)
        var orgA = new SysOrg { Name = $"t13b_org_{prefix}_a", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgB = new SysOrg { Name = $"t13b_org_{prefix}_b", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgC = new SysOrg { Name = $"t13b_org_{prefix}_c", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.InsertAsync(orgC);
        orgB.Pid = orgA.Id;
        await orgRepo.UpdateAsync(orgB);

        // 操作者:普通(777),OrgId=A,持 OrgAndChild(2) 角色 → 可见 {A, B}
        var opRole = new SysRole { Name = $"t13b_oprole_{prefix}", Code = $"t13b_oprole_{prefix}", DataScope = 2, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await roleRepo.InsertAsync(opRole);
        var opUser = new SysUser { Account = $"t13b_op_{prefix}", Password = "x", RealName = "操作者", OrgId = orgA.Id, TenantId = SqlSugarConst.DefaultTenantId };
        await userRepo.InsertAsync(opUser);
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = opUser.Id, RoleId = opRole.Id });

        // 目标角色:Define(5)
        var targetRole = new SysRole { Name = $"t13b_target_{prefix}", Code = $"t13b_target_{prefix}", DataScope = 5, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await roleRepo.InsertAsync(targetRole);

        try
        {
            // 模拟操作者上下文(777 非超管)
            dbScope.FakeAccessor.HttpContext = UserContext(opUser.Id, opUser.Account!, "777");

            // 集外机构 C → 越权拒绝
            await Assert.ThrowsAsync<AppException>(() =>
                roleService.GrantOrgAsync(new GrantOrgInput(targetRole.Id, new List<long> { orgC.Id })));

            // 集内机构 A → 成功
            await roleService.GrantOrgAsync(new GrantOrgInput(targetRole.Id, new List<long> { orgA.Id }));
            var granted = await roleService.GetOrgListAsync(targetRole.Id);
            Assert.Equal(new List<long> { orgA.Id }, granted);
        }
        finally
        {
            dbScope.FakeAccessor.HttpContext = null;
            await CleanupAsync(sp, new[] { opUser.Id }, new[] { orgA.Id, orgB.Id, orgC.Id }, Array.Empty<long>(), new[] { opRole.Id, targetRole.Id });
        }
    }

    /// <summary>
    /// ②B2 端到端:Define 授权 [A, B] → 用户范围 (Define, [A, B]) → SqlSugarFilter 装配 → 仅 A/B 机构行可见,
    /// C 机构行与存量空值行 fail-closed 不可见(ClearOrgFilter 后全量复原)。
    /// </summary>
    [Fact]
    public async Task B2_Define端到端_仅自定义机构行可见()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var probeRepo = sp.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();
        var roleService = sp.GetRequiredService<SysRoleService>();
        var orgService = sp.GetRequiredService<SysOrgService>();

        var prefix = Guid.NewGuid().ToString("N");
        var marker = $"t13b_e2e_{prefix}";
        var orgA = new SysOrg { Name = $"t13b_org_{prefix}_a", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgB = new SysOrg { Name = $"t13b_org_{prefix}_b", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        var orgC = new SysOrg { Name = $"t13b_org_{prefix}_c", Pid = 0, TenantId = SqlSugarConst.DefaultTenantId };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.InsertAsync(orgC);
        var defineRole = new SysRole { Name = $"t13b_role_{prefix}", Code = $"t13b_role_{prefix}", DataScope = 5, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
        await roleRepo.InsertAsync(defineRole);
        var user = new SysUser { Account = $"t13b_user_{prefix}", Password = "x", RealName = "测试", TenantId = SqlSugarConst.DefaultTenantId };
        await userRepo.InsertAsync(user);
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = defineRole.Id });

        // 前置清理(用例可重入)
        await probeRepo.DeletePhysicalByAsync(u => u.Name!.StartsWith(marker));

        try
        {
            // 超管授权 [A, B]
            dbScope.FakeAccessor.HttpContext = UserContext(1, "superadmin", "999");
            await roleService.GrantOrgAsync(new GrantOrgInput(defineRole.Id, new List<long> { orgA.Id, orgB.Id }));
            // 复位上下文(null):探针行显式 CreateOrgId 不被 AOP 覆盖
            dbScope.FakeAccessor.HttpContext = null;

            // 探针行:集内 A/B 各 1、集外 C 1、存量空值 1
            await probeRepo.InsertAsync(new TestDataScopeProbe { Name = marker + "_a", CreateOrgId = orgA.Id });
            await probeRepo.InsertAsync(new TestDataScopeProbe { Name = marker + "_b", CreateOrgId = orgB.Id });
            await probeRepo.InsertAsync(new TestDataScopeProbe { Name = marker + "_c", CreateOrgId = orgC.Id });
            await probeRepo.InsertAsync(new TestDataScopeProbe { Name = marker + "_null", CreateOrgId = null });
            var all = await probeRepo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(4, all.Count);

            // 范围收敛(Define, [A, B])→ 装配过滤器
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Define, maxScope);
            Assert.True(
                SqlSugarFilter.SetOrgEntityFilter(db, user.Id, maxScope, orgIds),
                "Define 范围有 [OwnerOrg] 属性实体应装配过滤器");

            var visible = await probeRepo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(2, visible.Count);
            Assert.All(visible, u => Assert.True(u.CreateOrgId == orgA.Id || u.CreateOrgId == orgB.Id, $"行应属自定义机构集,实际 {u.CreateOrgId}"));

            // ClearOrgFilter 复原(接口级软删过滤器幸存,查询口径不变)
            SqlSugarFilter.ClearOrgFilter(db);
            var restored = await probeRepo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(4, restored.Count);
        }
        finally
        {
            dbScope.FakeAccessor.HttpContext = null;
            await probeRepo.DeletePhysicalByAsync(u => u.Name!.StartsWith(marker));
            await CleanupAsync(sp, new[] { user.Id }, new[] { orgA.Id, orgB.Id, orgC.Id }, Array.Empty<long>(), new[] { defineRole.Id });
        }
    }

    /// <summary>测试种子物理清理(sys_user/sys_org/sys_pos/sys_role/sys_user_role/sys_role_org,按 Id 精确删,幂等可重入;走仓储实体映射防 PG 列名大小写坑)。</summary>
    private static async Task CleanupAsync(IServiceProvider sp, long[] userIds, long[] orgIds, long[] posIds, long[] roleIds)
    {
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var roleOrgRepo = sp.GetRequiredService<SqlSugarRepository<SysRoleOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var posRepo = sp.GetRequiredService<SqlSugarRepository<SysPos>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();

        if (userIds.Length > 0)
        {
            await userRoleRepo.DeletePhysicalByAsync(u => userIds.Contains(u.UserId));
        }

        if (roleIds.Length > 0)
        {
            await userRoleRepo.DeletePhysicalByAsync(u => roleIds.Contains(u.RoleId));
            await roleOrgRepo.DeletePhysicalByAsync(u => roleIds.Contains(u.RoleId));
        }

        if (orgIds.Length > 0)
        {
            await orgRepo.DeletePhysicalByAsync(u => orgIds.Contains(u.Id));
        }

        if (posIds.Length > 0)
        {
            await posRepo.DeletePhysicalByAsync(u => posIds.Contains(u.Id));
        }

        if (roleIds.Length > 0)
        {
            await roleRepo.DeletePhysicalByAsync(u => roleIds.Contains(u.Id));
        }

        if (userIds.Length > 0)
        {
            await userRepo.DeletePhysicalByAsync(u => userIds.Contains(u.Id));
        }
    }

    /// <summary>构造模拟登录上下文(UserId/Account/AccountType/TenantId Claims,与生产 JwtClaimNames 同键)。</summary>
    private static HttpContext UserContext(long userId, string account, string accountType)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, userId.ToString()),
            new Claim(JwtClaimNames.Account, account),
            new Claim(JwtClaimNames.AccountType, accountType),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        return context;
    }

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计填充 + 越权防护操作者解析读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// 框架表增量建(InitTables 扫 Core 程序集 → 建 sys_role_org 等缺表,幂等)+ 探针表(扫测试程序集)
    /// + SqlSugarFilter.Init(Core + 测试程序集)。PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        public FakeHttpContextAccessor FakeAccessor { get; } = new();

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
                    ["Yit:WorkerId"] = "1"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            // 模拟用户上下文访问器须在 AddSqlSugar 之前注册(TryAdd 不遮蔽已注册实例)
            services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
            services.AddSingleton<IConfiguration>(config); // SysUserService 依赖(批 A 教训:缺此注册 DI 解析失败)
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(SysOrgService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db); // 扫 Core 程序集 → 建 sys_role_org 等缺表(存量框架表增量幂等)
            Db.InitTables(typeof(TestDataScopeProbe).Assembly); // 扫测试程序集 → 建探针表
            SqlSugarFilter.Init(typeof(SqlSugarSetup).Assembly, typeof(TestDataScopeProbe).Assembly); // 数据权限实体注册(含探针)
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 DataScopeTest 口径)。</summary>
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
    }
}
