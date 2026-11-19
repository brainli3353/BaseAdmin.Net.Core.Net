using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions; // ITestOutputHelper(xunit 输出助手,降级声明写入测试输出)

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 机构数据权限测试(task_013):
/// ① 过滤器级(SqlSugarFilter 直接装配):仅本人([OwnerUser])/机构集([OwnerOrg] Contains)/全部(不装配)/
///    空机构集 fail-closed/ClearOrgFilter 复原(接口级软删过滤器幸存);
/// ② 服务级(SysOrgService.GetUserOrgScopeAsync):多角色最大范围收敛/无角色/本部门/缓存命中/变更即失效;
/// ③ 写侧(AOP):CreateOrgId/UpdateOrgId 按当前用户所属机构自动填(HttpContext 模拟)。
/// 环境说明:依赖 PostgreSQL(与 RepositoryTest 同源连接串);沙箱不可达时用例诚实降级(Assert.Fail 显式声明)。
/// </summary>
public class DataScopeTest
{
    private readonly ITestOutputHelper _output;

    public DataScopeTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 RepositoryTest 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>
    /// ① 机构范围(本部门及以下)过滤:可见机构集内行可见、集外行不可见、CreateOrgId 为空的存量行不可见(fail-closed);
    /// ClearOrgFilter 后全量复原(软删过滤器幸存——查询口径不变)。
    /// </summary>
    [Fact]
    public async Task 过滤器_机构范围_仅可见集内行且存量空值行failClosed()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();

        var marker = $"t13_org_{Guid.NewGuid():N}";
        const long OrgA = 111;
        const long OrgB = 222;
        try
        {
            // 前置清理(用例可重入)+ 种子:集内 2 行 / 集外 2 行 / 存量空值 1 行
            await CleanAsync(db, marker);
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_a", CreateOrgId = OrgA });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_b", CreateOrgId = OrgB });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_x", CreateOrgId = 999 });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_y", CreateOrgId = 998 });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_null", CreateOrgId = null });

            var all = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(5, all.Count);

            // 机构范围:可见集 {OrgA, OrgB} → 仅 2 行(空值存量行 fail-closed 不可见)
            Assert.True(
                SqlSugarFilter.SetOrgEntityFilter(db, 42, DataScopeEnum.OrgAndChild, new List<long> { OrgA, OrgB }),
                "有 [OwnerOrg] 属性实体应装配过滤器");
            var visible = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(2, visible.Count);
            Assert.All(visible, u => Assert.True(u.CreateOrgId is OrgA or OrgB, $"行应属于可见机构集,实际 {u.CreateOrgId}"));

            // ClearOrgFilter → 全量复原(软删过滤幸存:物理行 5 条,过滤口径不变)
            SqlSugarFilter.ClearOrgFilter(db);
            var afterClear = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(5, afterClear.Count);
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(db); // 防过滤器状态泄漏到同库后续用例
            await CleanAsync(db, marker);
        }
    }

    /// <summary>① 空机构集(本部门但用户未挂机构等场景)→ fail-closed:机构范围查无行。</summary>
    [Fact]
    public async Task 过滤器_空机构集_failClosed查无行()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();

        var marker = $"t13_empty_{Guid.NewGuid():N}";
        try
        {
            await CleanAsync(db, marker);
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_a", CreateOrgId = 111 });

            Assert.True(SqlSugarFilter.SetOrgEntityFilter(db, 42, DataScopeEnum.Org, new List<long>()));
            var visible = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Empty(visible); // Contains(空集) → 无行(fail-closed)
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(db);
            await CleanAsync(db, marker);
        }
    }

    /// <summary>① 仅本人(4)→ [OwnerUser] 路径:只看自己创建的行(继承的 CreateUserId 标记生效)。</summary>
    [Fact]
    public async Task 过滤器_仅本人_只看自己创建的行()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();

        var marker = $"t13_self_{Guid.NewGuid():N}";
        const long Me = 777;
        const long Other = 888;
        try
        {
            await CleanAsync(db, marker);
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_m1", CreateUserId = Me });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_m2", CreateUserId = Me });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_o1", CreateUserId = Other });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_o2", CreateUserId = Other });

            Assert.True(SqlSugarFilter.SetOrgEntityFilter(db, Me, DataScopeEnum.Self, new List<long>()));
            var visible = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(2, visible.Count);
            Assert.All(visible, u => Assert.Equal(Me, u.CreateUserId));
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(db);
            await CleanAsync(db, marker);
        }
    }

    /// <summary>① 全部(1)→ 不装配(返回 false,查询口径不变)。</summary>
    [Fact]
    public async Task 过滤器_全部范围_不装配()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();

        var marker = $"t13_all_{Guid.NewGuid():N}";
        try
        {
            await CleanAsync(db, marker);
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_a", CreateOrgId = 111 });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_b", CreateOrgId = 999 });

            Assert.False(SqlSugarFilter.SetOrgEntityFilter(db, 42, DataScopeEnum.All, new List<long>()));
            var visible = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(2, visible.Count); // 未装配 → 全量可见
        }
        finally
        {
            await CleanAsync(db, marker);
        }
    }

    /// <summary>① IgnoreOrgData&lt;T&gt;() 查询级放行:机构过滤生效下,放行查询恢复全量,且共享过滤状态不受影响(后续普通查询仍被过滤)。</summary>
    [Fact]
    public async Task 过滤器_IgnoreOrgData_查询级放行恢复全量()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();

        var marker = $"t13_ignore_{Guid.NewGuid():N}";
        try
        {
            await CleanAsync(db, marker);
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_a", CreateOrgId = 111 });
            await repo.InsertAsync(new TestDataScopeProbe { Name = marker + "_x", CreateOrgId = 999 });

            Assert.True(SqlSugarFilter.SetOrgEntityFilter(db, 42, DataScopeEnum.Org, new List<long> { 111 }));
            var visible = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Single(visible);
            Assert.Equal(111, visible[0].CreateOrgId);

            // 查询级放行:当前查询实例恢复全量;共享过滤状态不变 → 后续普通查询仍被过滤
            var all = await db.IgnoreOrgData<TestDataScopeProbe>().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Equal(2, all.Count);
            var still = await repo.AsQueryable().Where(u => u.Name!.StartsWith(marker)).ToListAsync();
            Assert.Single(still);
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(db);
            await CleanAsync(db, marker);
        }
    }

    /// <summary>② 多角色最大范围收敛:角色{仅本人, 本部门及以下} + 用户机构 A(子树 A→B)→ (OrgAndChild, [A, B]);
    /// 且两缓存键落库(KeyRoleMaxDataScope + KeyUserOrg)。</summary>
    [Fact]
    public async Task 服务_多角色收敛_取最大范围且机构集为子树()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        var prefix = Guid.NewGuid().ToString("N");
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var orgService = sp.GetRequiredService<SysOrgService>();
        var cache = sp.GetRequiredService<ICacheService>();

        var orgRoot = new SysOrg { Name = $"t13_org_{prefix}_root", Pid = 0 };
        var orgA = new SysOrg { Name = $"t13_org_{prefix}_a", Pid = 0 };
        var orgB = new SysOrg { Name = $"t13_org_{prefix}_b", Pid = 0 }; // 稍后改挂 A
        await orgRepo.InsertAsync(orgRoot);
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        var user = new SysUser { Account = $"t13_user_{prefix}", Password = "x", RealName = "测试", OrgId = orgA.Id };
        await userRepo.InsertAsync(user);
        var roleSelf = new SysRole { Name = $"t13_role_{prefix}_self", Code = $"t13_role_{prefix}_self", DataScope = 4, Remark = "" };
        var roleChild = new SysRole { Name = $"t13_role_{prefix}_child", Code = $"t13_role_{prefix}_child", DataScope = 2, Remark = "" };
        await roleRepo.InsertAsync(roleSelf);
        await roleRepo.InsertAsync(roleChild);

        try
        {
            // 组树:B 挂 A 下 → A 的子树 = {A, B}(orgRoot 为未用占位根,同批清理)
            await FixPidAsync(orgRepo, orgB.Id, orgA.Id);

            await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = roleSelf.Id });
            await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = roleChild.Id });

            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.OrgAndChild, maxScope); // Min(4, 2) = 2
            Assert.Equal(new List<long> { orgA.Id, orgB.Id }, orgIds.OrderBy(x => x).ToList());

            // 两缓存键同批落库
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyRoleMaxDataScope + user.Id));
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyUserOrg + user.Id));
        }
        finally
        {
            await CleanupAsync(db, new[] { user.Id }, new[] { orgRoot.Id, orgA.Id, orgB.Id }, new[] { roleSelf.Id, roleChild.Id });
        }
    }

    /// <summary>② 无角色用户 → (仅本人, 空集)(最小权限起步)。</summary>
    [Fact]
    public async Task 服务_无角色_仅本人空集()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var orgService = sp.GetRequiredService<SysOrgService>();
        var user = new SysUser { Account = $"t13_user_{Guid.NewGuid():N}", Password = "x", RealName = "测试" };
        await userRepo.InsertAsync(user);
        try
        {
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Self, maxScope);
            Assert.Empty(orgIds);
        }
        finally
        {
            await CleanupAsync(dbScope.Db, new[] { user.Id }, Array.Empty<long>(), Array.Empty<long>());
        }
    }

    /// <summary>② 本部门(3)→ 仅用户所属机构(不含子孙)。</summary>
    [Fact]
    public async Task 服务_本部门_仅所属机构()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        var prefix = Guid.NewGuid().ToString("N");
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var orgService = sp.GetRequiredService<SysOrgService>();

        var orgA = new SysOrg { Name = $"t13_org_{prefix}_a", Pid = 0 };
        var orgB = new SysOrg { Name = $"t13_org_{prefix}_b", Pid = 0 };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.UpdateAsync(await FixPidAsync(orgRepo, orgB.Id, orgA.Id)); // B 挂 A 下(验证本部门不含子孙)
        var user = new SysUser { Account = $"t13_user_{prefix}", Password = "x", RealName = "测试", OrgId = orgA.Id };
        await userRepo.InsertAsync(user);
        var role = new SysRole { Name = $"t13_role_{prefix}", Code = $"t13_role_{prefix}", DataScope = 3, Remark = "" };
        await roleRepo.InsertAsync(role);

        try
        {
            await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Org, maxScope);
            Assert.Equal(new List<long> { orgA.Id }, orgIds); // 不含子孙 B
        }
        finally
        {
            await CleanupAsync(db, new[] { user.Id }, new[] { orgA.Id, orgB.Id }, new[] { role.Id });
        }
    }

    /// <summary>② 缓存命中 + 变更即失效:DataScope 3→2 后缓存仍返旧值(证明命中),DeleteUserOrgCacheAsync 后返新值(子树)。</summary>
    [Fact]
    public async Task 服务_缓存命中与变更即失效()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        var prefix = Guid.NewGuid().ToString("N");
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var orgService = sp.GetRequiredService<SysOrgService>();
        var cache = sp.GetRequiredService<ICacheService>();

        var orgA = new SysOrg { Name = $"t13_org_{prefix}_a", Pid = 0 };
        var orgB = new SysOrg { Name = $"t13_org_{prefix}_b", Pid = 0 };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.UpdateAsync(await FixPidAsync(orgRepo, orgB.Id, orgA.Id));
        var user = new SysUser { Account = $"t13_user_{prefix}", Password = "x", RealName = "测试", OrgId = orgA.Id };
        await userRepo.InsertAsync(user);
        var role = new SysRole { Name = $"t13_role_{prefix}", Code = $"t13_role_{prefix}", DataScope = 3, Remark = "" };
        await roleRepo.InsertAsync(role);

        try
        {
            await userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = role.Id });

            var first = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Org, first.MaxScope);

            // 直接改库 DataScope 3→2(不经服务,隔离测试缓存行为):缓存命中 → 仍返旧值(3, [A])
            role.DataScope = 2;
            await roleRepo.UpdateAsync(role);
            var cached = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.Org, cached.MaxScope);
            Assert.Equal(new List<long> { orgA.Id }, cached.OrgIds);

            // 变更即失效 → 新值(2, [A, B])
            await SqlSugarFilter.DeleteUserOrgCacheAsync(cache, user.Id);
            var fresh = await orgService.GetUserOrgScopeAsync(user.Id);
            Assert.Equal(DataScopeEnum.OrgAndChild, fresh.MaxScope);
            Assert.Equal(new List<long> { orgA.Id, orgB.Id }, fresh.OrgIds.OrderBy(x => x).ToList());
        }
        finally
        {
            await CleanupAsync(db, new[] { user.Id }, new[] { orgA.Id, orgB.Id }, new[] { role.Id });
        }
    }

    /// <summary>③ 写侧 AOP:HttpContext 模拟登录用户 → 插入 CreateOrgId 自动填 = 用户所属机构;
    /// 更新 EntityBaseUpdateData 时 UpdateOrgId 自动填(同值,每请求 memo 不重复查库)。</summary>
    [Fact]
    public async Task AOP_写入侧_CreateOrgId与UpdateOrgId按用户机构自动填()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        var prefix = Guid.NewGuid().ToString("N");
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var probeRepo = sp.GetRequiredService<SqlSugarRepository<TestDataScopeProbe>>();
        var updateProbeRepo = sp.GetRequiredService<SqlSugarRepository<TestDataScopeUpdateProbe>>();

        var org = new SysOrg { Name = $"t13_org_{prefix}", Pid = 0 };
        await orgRepo.InsertAsync(org);
        var user = new SysUser { Account = $"t13_user_{prefix}", Password = "x", RealName = "测试", OrgId = org.Id };
        await userRepo.InsertAsync(user);

        var marker = $"t13_aop_{prefix}";
        try
        {
            // 模拟登录上下文(UserId/Account/TenantId Claims)
            dbScope.FakeAccessor.HttpContext = UserContext(user.Id, user.Account!);

            var probe = new TestDataScopeProbe { Name = marker };
            Assert.True(await probeRepo.InsertAsync(probe));
            Assert.Equal(org.Id, probe.CreateOrgId); // AOP 查 sys_user.OrgId 自动填

            var uprobe = new TestDataScopeUpdateProbe { Name = marker + "_u" };
            Assert.True(await updateProbeRepo.InsertAsync(uprobe));
            Assert.Equal(org.Id, uprobe.CreateOrgId);
            var fetched = await updateProbeRepo.GetFirstAsync(u => u.Name == marker + "_u");
            fetched!.Name = marker + "_u_edited";
            Assert.True(await updateProbeRepo.UpdateAsync(fetched));
            Assert.Equal(org.Id, fetched.UpdateOrgId); // AOP 更新路径自动填
        }
        finally
        {
            dbScope.FakeAccessor.HttpContext = null; // 复位,防影响同库后续用例
            await CleanAsync(db, marker);
            await CleanupAsync(db, new[] { user.Id }, new[] { org.Id }, Array.Empty<long>());
        }
    }

    /// <summary>② 机构改挂载失效钩子:机构 B 从 A 下迁到 C 下 → 新旧锚点子树内用户(A 用户/C 用户)数据权限缓存即时失效。</summary>
    [Fact]
    public async Task 服务_机构改挂载_受影响用户缓存即时失效()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        var prefix = Guid.NewGuid().ToString("N");
        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
        var orgService = sp.GetRequiredService<SysOrgService>();
        var cache = sp.GetRequiredService<ICacheService>();

        var orgA = new SysOrg { Name = $"t13_org_{prefix}_a", Pid = 0 };
        var orgB = new SysOrg { Name = $"t13_org_{prefix}_b", Pid = 0 };
        var orgC = new SysOrg { Name = $"t13_org_{prefix}_c", Pid = 0 };
        await orgRepo.InsertAsync(orgA);
        await orgRepo.InsertAsync(orgB);
        await orgRepo.InsertAsync(orgC);
        await orgRepo.UpdateAsync(await FixPidAsync(orgRepo, orgB.Id, orgA.Id)); // B 初始挂 A 下
        var userA = new SysUser { Account = $"t13_user_{prefix}_a", Password = "x", RealName = "测试", OrgId = orgA.Id };
        var userC = new SysUser { Account = $"t13_user_{prefix}_c", Password = "x", RealName = "测试", OrgId = orgC.Id };
        await userRepo.InsertAsync(userA);
        await userRepo.InsertAsync(userC);
        var role = new SysRole { Name = $"t13_role_{prefix}", Code = $"t13_role_{prefix}", DataScope = 2, Remark = "" };
        await roleRepo.InsertAsync(role);
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = userA.Id, RoleId = role.Id });
        await userRoleRepo.InsertAsync(new SysUserRole { UserId = userC.Id, RoleId = role.Id });

        try
        {
            // 预热两用户缓存:userA 范围集 = 子树(A) = {A, B};userC = {C}
            var scopeA = await orgService.GetUserOrgScopeAsync(userA.Id);
            Assert.Equal(new List<long> { orgA.Id, orgB.Id }, scopeA.OrgIds.OrderBy(x => x).ToList());
            var scopeC = await orgService.GetUserOrgScopeAsync(userC.Id);
            Assert.Equal(new List<long> { orgC.Id }, scopeC.OrgIds);
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyUserOrg + userA.Id));
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyUserOrg + userC.Id));

            // 改挂载:B 从 A 下迁到 C 下(经服务,触发失效钩子;位置参数记录仅传 Id + Pid,余为 null=不改)
            await orgService.UpdateAsync(new UpdateOrgInput(orgB.Id, orgC.Id, null, null, null, null, null));

            // 受影响用户(A 锚点子树 {A, B} ∪ C 锚点子树 {C})缓存已失效
            Assert.False(await cache.ExistKeyAsync(CacheConst.KeyUserOrg + userA.Id));
            Assert.False(await cache.ExistKeyAsync(CacheConst.KeyUserOrg + userC.Id));

            // 重算:userA 范围集 = {A}(B 已迁出),userC = {C, B}(B 迁入)
            var freshA = await orgService.GetUserOrgScopeAsync(userA.Id);
            Assert.Equal(new List<long> { orgA.Id }, freshA.OrgIds);
            var freshC = await orgService.GetUserOrgScopeAsync(userC.Id);
            Assert.Equal(new List<long> { orgC.Id, orgB.Id }.OrderBy(x => x).ToList(), freshC.OrgIds.OrderBy(x => x).ToList());
        }
        finally
        {
            await CleanupAsync(db, new[] { userA.Id, userC.Id }, new[] { orgA.Id, orgB.Id, orgC.Id }, new[] { role.Id });
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    /// <summary>改机构 Pid(读回实体改字段再整对象更新,走仓储口径)。</summary>
    private static async Task<SysOrg> FixPidAsync(SqlSugarRepository<SysOrg> orgRepo, long orgId, long newPid)
    {
        var org = await orgRepo.GetFirstAsync(u => u.Id == orgId);
        org!.Pid = newPid;
        await orgRepo.UpdateAsync(org);
        return org;
    }

    /// <summary>探针表清理(原生 SQL 物理删,Name 前缀)。</summary>
    private static Task CleanAsync(ISqlSugarClient db, string marker)
    {
        db.Ado.ExecuteCommand("delete from test_data_scope_probe where \"Name\" like @m", new { m = marker + "%" });
        db.Ado.ExecuteCommand("delete from test_data_scope_update_probe where \"Name\" like @m", new { m = marker + "%" });
        return Task.CompletedTask;
    }

    /// <summary>测试种子物理清理(sys_user/sys_org/sys_role/sys_user_role,按 Id 精确删,幂等可重入)。</summary>
    private static async Task CleanupAsync(ISqlSugarClient db, long[] userIds, long[] orgIds, long[] roleIds)
    {
        foreach (var userId in userIds)
        {
            await db.Ado.ExecuteCommandAsync("delete from sys_user_role where \"UserId\" = @id", new { id = userId });
        }

        foreach (var roleId in roleIds)
        {
            await db.Ado.ExecuteCommandAsync("delete from sys_user_role where \"RoleId\" = @id", new { id = roleId });
        }

        if (orgIds.Length > 0)
        {
            await db.Ado.ExecuteCommandAsync($"delete from sys_org where id in ({string.Join(",", orgIds)})");
        }

        if (roleIds.Length > 0)
        {
            await db.Ado.ExecuteCommandAsync($"delete from sys_role where id in ({string.Join(",", roleIds)})");
        }

        foreach (var userId in userIds)
        {
            await db.Ado.ExecuteCommandAsync("delete from sys_user where id = @id", new { id = userId });
        }
    }

    /// <summary>构造模拟登录上下文(UserId/Account/AccountType/TenantId Claims,与生产 JwtClaimNames 同键)。</summary>
    private static HttpContext UserContext(long userId, string account)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, userId.ToString()),
            new Claim(JwtClaimNames.Account, account),
            new Claim(JwtClaimNames.AccountType, "777"),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        return context;
    }

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计填充读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// 探针实体建表(InitTables 扫测试程序集)+ SqlSugarFilter.Init(Core + 测试程序集)。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
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
            services.AddSqlSugar(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(SysOrgService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            Db.HealColumnCase();
            Db.InitTables(typeof(TestDataScopeProbe).Assembly); // 扫测试程序集 → 建探针表(框架表已存在,增量幂等)
            SqlSugarFilter.Init(typeof(SqlSugarSetup).Assembly, typeof(TestDataScopeProbe).Assembly); // 数据权限实体注册(含探针)
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 RepositoryTest 口径)。</summary>
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
