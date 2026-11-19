using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OtpNet;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 C 多租户业务模块 C1 测试(真实 PG,与 DataScopeTest 同源连接串;沙箱不可达时诚实降级 Assert.Fail):
/// ① CRUD:新建/部分更新/启停/软删;② 默认租户保护(不可停用/不可删除);
/// ③ 停用租户 → 该租户全员 token 版本缓存即时失效(SetStatusAsync 变更即失效);
/// ④ UserTokenStateProvider 租户态五分支(有效/停用/过期/租户不存在/默认租户);
/// ⑤ 登录一阶段租户门(停用/过期/不存在 → 拒绝);⑥ 登录两阶段有效租户正常签发 token(非回归);
/// ⑦ 批 BM D3:删除租户 → 全员 token 版本缓存即时失效;⑧ 批 BM D4:开放接口绑定租户删除拒绝(未绑定对照放行)。
/// 批 BM D2:IAsyncDisposable 探针登记兜底(用例失败也不漏行;成功路径内联清理保留,幂等)。
/// </summary>
public class BatchCTenantTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 批 BM D2:探针登记——xUnit 在用例失败后仍调用 DisposeAsync(方法体内联清理只保成功路径,
    // 失败即漏行;dev 库实测 43 历史探针租户+32 探针用户残留即此机制)。成功路径的内联 CleanupAsync 保留(幂等)。
    private readonly List<long> _probeTenantIds = new();
    private readonly List<long> _probeUserIds = new();
    private readonly List<long> _probeOpenAccessIds = new();

    public BatchCTenantTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_probeTenantIds.Count == 0 && _probeUserIds.Count == 0 && _probeOpenAccessIds.Count == 0)
        {
            return;
        }

        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            return;
        }

        using var diScope = dbScope.Provider.CreateScope();
        await CleanupAsync(diScope.ServiceProvider, _probeTenantIds, _probeUserIds, _probeOpenAccessIds);
    }

    // 开发库连接串(与 DataScopeTest 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // ─────────────────────────────────────────────────────────────
    // ① CRUD:新建 → 部分更新 → 启停 → 软删
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CRUD_新建部分更新启停软删()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var name = $"t{p}crud";
        var id = await tenantService.AddAsync(new AddTenantInput(name, "专业版", null, 5, "备注A"));
        _probeTenantIds.Add(id);
        Assert.True(id > 0);

        // 新建落库(AOP 自动填主键/审计)
        var row = await tenantRepo.GetFirstAsync(t => t.Id == id);
        Assert.NotNull(row);
        Assert.Equal("专业版", row!.Plan);
        Assert.Equal(5, row.OrderNo);
        Assert.Equal(0, row.Status);

        // 部分更新:仅非 null 字段变更(null=不改)
        await tenantService.UpdateAsync(new UpdateTenantInput(id, name, "旗舰版", null, 9, "备注B"));
        row = await tenantRepo.GetFirstAsync(t => t.Id == id);
        Assert.Equal("旗舰版", row!.Plan);
        Assert.Equal(9, row.OrderNo);
        Assert.Equal("备注B", row.Remark);

        // 全部 null → 不改(部分更新口径)
        await tenantService.UpdateAsync(new UpdateTenantInput(id, null, null, null, null, null));
        row = await tenantRepo.GetFirstAsync(t => t.Id == id);
        Assert.Equal("旗舰版", row!.Plan); // 未变

        // 启停
        await tenantService.SetStatusAsync(new SetTenantStatusInput(id, 1));
        row = await tenantRepo.GetFirstAsync(t => t.Id == id);
        Assert.Equal(1, row!.Status);

        // 分页可按名模糊命中
        var page = await tenantService.PageAsync(new PageTenantInput(name));
        Assert.Single(page.List);

        // 软删:软删过滤器下不可见
        await tenantService.DeleteAsync(id);
        Assert.Null(await tenantRepo.GetFirstAsync(t => t.Id == id));

        // 清理:物理删(唯一 Id,防残留)
        await CleanupAsync(sp, new[] { id }, Array.Empty<long>());
    }

    // ─────────────────────────────────────────────────────────────
    // ② 默认租户保护:不可停用、不可删除(防平台管理员自锁)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 默认租户_停用删除均拒绝()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();

        // 测试环境不跑种子 → 确保默认租户行存在(已存在则跳过;绝不物理删默认租户)
        await EnsureDefaultTenantAsync(tenantRepo);

        // 停用默认租户 → 拒绝
        await Assert.ThrowsAsync<AppException>(
            () => tenantService.SetStatusAsync(new SetTenantStatusInput(SqlSugarConst.DefaultTenantId, 1)));
        // 删除默认租户 → 拒绝
        await Assert.ThrowsAsync<AppException>(
            () => tenantService.DeleteAsync(SqlSugarConst.DefaultTenantId));
        // 批 BM D1:修改默认租户 → 拒绝(防自锁:改过期时间=全平台拒登,改连接串=平台数据切库)
        var ex = await Assert.ThrowsAsync<AppException>(
            () => tenantService.UpdateAsync(new UpdateTenantInput(SqlSugarConst.DefaultTenantId, null, null, DateTime.Now, null, null)));
        Assert.Equal(ErrorCodes.TenantDefaultUneditable, ex.ErrorCode);
        // 默认租户行仍在(未被误删/误改)
        var defRow = await tenantRepo.GetFirstAsync(t => t.Id == SqlSugarConst.DefaultTenantId);
        Assert.NotNull(defRow);
        Assert.Null(defRow!.ExpirationTime);
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 停用租户 → 该租户全员 token 版本缓存即时失效
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task SetStatus停用_该租户全员token缓存即时失效()
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
        var cache = sp.GetRequiredService<ICacheService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}dis", null, null, null, null));
        _probeTenantIds.Add(tenantId);

        // 该租户下两个用户,预置 token 版本缓存(模拟已登录态)
        var users = new List<SysUser>();
        for (var i = 0; i < 2; i++)
        {
            var u = await InsertUserAsync(userRepo, $"u{p}d{i}", tenantId);
            users.Add(u);
            _probeUserIds.Add(u.Id);
            await cache.SetAsync($"{CacheConst.KeyUserToken}{u.Id}", 1);
        }
        Assert.True(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{users[0].Id}"));
        Assert.True(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{users[1].Id}"));

        // 停用租户 → 该租户全员 token 缓存被删(JwtAuthHandler 下次缓存缺失回查租户态即拒绝)
        await tenantService.SetStatusAsync(new SetTenantStatusInput(tenantId, 1));
        Assert.False(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{users[0].Id}"));
        Assert.False(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{users[1].Id}"));

        await CleanupAsync(sp, new[] { tenantId }, users.Select(u => u.Id).ToList());
    }

    // ─────────────────────────────────────────────────────────────
    // ④ UserTokenStateProvider 租户态:有效/停用/过期/租户不存在 + 默认租户
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task UserTokenStateProvider_租户态五分支()
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
        var provider = sp.GetRequiredService<IUserTokenStateProvider>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var tValid = await tenantService.AddAsync(new AddTenantInput($"t{p}valid", null, null, null, null));
        _probeTenantIds.Add(tValid);
        var tDisabled = await tenantService.AddAsync(new AddTenantInput($"t{p}disb", null, null, null, null));
        _probeTenantIds.Add(tDisabled);
        await tenantService.SetStatusAsync(new SetTenantStatusInput(tDisabled, 1));
        var tExpired = await tenantService.AddAsync(new AddTenantInput($"t{p}exp", null, DateTime.Now.AddHours(-1), null, null));
        _probeTenantIds.Add(tExpired);
        const long tMissing = 999999999999999; // 不存在的租户
        await EnsureDefaultTenantAsync(sp.GetRequiredService<SqlSugarRepository<SysTenant>>()); // 默认租户分支

        var uValid = await InsertUserAsync(userRepo, $"u{p}v", tValid);
        var uDisabled = await InsertUserAsync(userRepo, $"u{p}d", tDisabled);
        var uExpired = await InsertUserAsync(userRepo, $"u{p}e", tExpired);
        var uMissing = await InsertUserAsync(userRepo, $"u{p}m", tMissing);
        var uDefault = await InsertUserAsync(userRepo, $"u{p}df", null); // TenantId null → 默认租户
        foreach (var uid in new[] { uValid.Id, uDisabled.Id, uExpired.Id, uMissing.Id, uDefault.Id })
        {
            _probeUserIds.Add(uid);
        }

        Assert.True((await provider.GetUserStateAsync(uValid.Id))!.TenantValid);   // 有效
        Assert.False((await provider.GetUserStateAsync(uDisabled.Id))!.TenantValid); // 停用
        Assert.False((await provider.GetUserStateAsync(uExpired.Id))!.TenantValid);  // 过期
        Assert.False((await provider.GetUserStateAsync(uMissing.Id))!.TenantValid);  // 租户不存在
        Assert.True((await provider.GetUserStateAsync(uDefault.Id))!.TenantValid);   // 默认租户(有效)

        await CleanupAsync(sp, new[] { tValid, tDisabled, tExpired },
            new[] { uValid.Id, uDisabled.Id, uExpired.Id, uMissing.Id, uDefault.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ 登录一阶段租户门:停用/过期/不存在 → 拒绝(有效账号,租户异常)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录_租户异常_一阶段拒绝()
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
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tDisabled = await tenantService.AddAsync(new AddTenantInput($"t{p}d", null, null, null, null));
        _probeTenantIds.Add(tDisabled);
        await tenantService.SetStatusAsync(new SetTenantStatusInput(tDisabled, 1));
        var tExpired = await tenantService.AddAsync(new AddTenantInput($"t{p}e", null, DateTime.Now.AddHours(-1), null, null));
        _probeTenantIds.Add(tExpired);
        const long tMissing = 888888888888888;

        var uDisabled = await InsertLoginUserAsync(userRepo, $"u{p}d", password, tDisabled);
        var uExpired = await InsertLoginUserAsync(userRepo, $"u{p}e", password, tExpired);
        var uMissing = await InsertLoginUserAsync(userRepo, $"u{p}x", password, tMissing);
        foreach (var uid in new[] { uDisabled.Id, uExpired.Id, uMissing.Id })
        {
            _probeUserIds.Add(uid);
        }

        var r1 = await authService.LoginAsync(uDisabled.Account, password);
        Assert.False(r1.Success);
        Assert.Equal(ErrorCodes.TenantDisabled, r1.ErrorCode); // 批 AQ:错误码口径

        var r2 = await authService.LoginAsync(uExpired.Account, password);
        Assert.False(r2.Success);
        Assert.Equal(ErrorCodes.TenantExpired, r2.ErrorCode);

        var r3 = await authService.LoginAsync(uMissing.Account, password);
        Assert.False(r3.Success);
        Assert.Equal(ErrorCodes.TenantNotFound, r3.ErrorCode);

        await CleanupAsync(sp, new[] { tDisabled, tExpired },
            new[] { uDisabled.Id, uExpired.Id, uMissing.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ⑥ 登录两阶段:有效租户正常签发 token(非回归:租户校验不误伤正常登录)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 登录_有效租户_两阶段签发token()
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
        var authService = sp.GetRequiredService<AuthService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        const string password = "P@ssw0rd!C";
        var tValid = await tenantService.AddAsync(new AddTenantInput($"t{p}ok", null, null, null, null));
        _probeTenantIds.Add(tValid);
        var user = await InsertLoginUserAsync(userRepo, $"u{p}ok", password, tValid);
        _probeUserIds.Add(user.Id);

        // 一阶段:账号密码通过 + 租户有效 → 发挑战(未绑定 → 附带密钥)
        var r1 = await authService.LoginAsync(user.Account, password);
        Assert.True(r1.Success);
        Assert.NotNull(r1.Data);
        Assert.True(r1.Data!.BindRequired);
        Assert.False(string.IsNullOrWhiteSpace(r1.Data.Secret));

        // 二阶段:用 OtpNet 由密钥生成当前 TOTP 码 → 换 token(租户有效不误伤)
        var code = new Totp(Base32Encoding.ToBytes(r1.Data.Secret!)).ComputeTotp();
        var r2 = await authService.LoginOtpAsync(r1.Data.ChallengeId, code);
        Assert.True(r2.Success);
        Assert.False(string.IsNullOrWhiteSpace(r2.Token));

        await CleanupAsync(sp, new[] { tValid }, new[] { user.Id });
    }

    // ─────────────────────────────────────────────────────────────
    // ⑦ 批 BM D3:删除租户 → 该租户全员 token 版本缓存即时失效(与停用路径同类同口径)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Delete_该租户全员token缓存即时失效()
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
        var cache = sp.GetRequiredService<ICacheService>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var tenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}del", null, null, null, null));
        _probeTenantIds.Add(tenantId);

        // 该租户下一个用户,预置 token 版本缓存(模拟已登录态)
        var u = await InsertUserAsync(userRepo, $"u{p}del", tenantId);
        _probeUserIds.Add(u.Id);
        await cache.SetAsync($"{CacheConst.KeyUserToken}{u.Id}", 1);
        Assert.True(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{u.Id}"));

        // 删除租户 → 版本缓存被删(JwtAuthHandler 缓存缺失回查:租户不存在 → 拒绝;
        // 不删则快路径命中持续放行——版本键无 TTL 且快路径不回查租户态)
        await tenantService.DeleteAsync(tenantId);
        Assert.False(await cache.ExistKeyAsync($"{CacheConst.KeyUserToken}{u.Id}"));
    }

    // ─────────────────────────────────────────────────────────────
    // ⑧ 批 BM D4:开放接口绑定租户删除拒绝(参照 D1031 口径)+ 未绑定对照放行
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Delete_开放接口绑定租户拒绝_未绑定对照放行()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();
        var openAccessRepo = sp.GetRequiredService<SqlSugarRepository<SysOpenAccess>>();

        var p = Guid.NewGuid().ToString("N")[..8];
        var boundTenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}bound", null, null, null, null));
        _probeTenantIds.Add(boundTenantId);
        var freeTenantId = await tenantService.AddAsync(new AddTenantInput($"t{p}free", null, null, null, null));
        _probeTenantIds.Add(freeTenantId);

        // 绑定开放接口(AccessKey 唯一探针前缀;探针行登记后由 DisposeAsync 兜底物理清)
        var oaId = await InsertOpenAccessAsync(openAccessRepo, $"bmm_{p}", boundTenantId);
        _probeOpenAccessIds.Add(oaId);

        // 绑定租户删除 → 拒绝
        var ex = await Assert.ThrowsAsync<AppException>(() => tenantService.DeleteAsync(boundTenantId));
        Assert.Equal(ErrorCodes.TenantOpenAccessBound, ex.ErrorCode);

        // 物理清绑定行 → 对照:同一租户删除放行
        await openAccessRepo.DeletePhysicalByAsync(u => u.Id == oaId);
        await tenantService.DeleteAsync(boundTenantId);

        // 未绑定租户删除 → 放行(对照)
        await tenantService.DeleteAsync(freeTenantId);
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────
    private static async Task<SysUser> InsertUserAsync(SqlSugarRepository<SysUser> userRepo, string account, long? tenantId)
    {
        var u = new SysUser
        {
            Account = account,
            Password = "x",
            RealName = "x",
            Status = 0,
            AccountType = 777,
            TenantId = tenantId
        };
        await userRepo.InsertAsync(u);
        return u;
    }

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

    /// <summary>插入开放接口探针行(批 BM D4;AccessKey 探针前缀唯一,DisposeAsync 兜底物理清)。</summary>
    private static async Task<long> InsertOpenAccessAsync(SqlSugarRepository<SysOpenAccess> openAccessRepo, string accessKey, long bindTenantId)
    {
        var oa = new SysOpenAccess
        {
            AccessKey = accessKey,
            AccessSecret = "x",
            BindTenantId = bindTenantId,
            BindUserId = 0
        };
        await openAccessRepo.InsertAsync(oa);
        return oa.Id;
    }

    /// <summary>确保默认租户行存在(测试环境不跑种子);已存在则跳过。绝不物理删默认租户(生产数据)。</summary>
    private static async Task EnsureDefaultTenantAsync(SqlSugarRepository<SysTenant> tenantRepo)
    {
        var existing = await tenantRepo.GetFirstAsync(t => t.Id == SqlSugarConst.DefaultTenantId);
        if (existing is null)
        {
            await tenantRepo.InsertAsync(new SysTenant
            {
                Id = SqlSugarConst.DefaultTenantId,
                Name = "默认租户",
                Status = 0,
                ExpirationTime = null
            });
        }
    }

    /// <summary>清理:物理删本用例创建的租户(排除默认租户)与用户(唯一 Id,防残留);
    /// 批 BK 泄漏修复:两阶段登录绑定路径会为探针用户落 sys_user_otp 行,用户物理删后 OTP 行成孤儿
    /// (dev 库实测每轮全量漏 1 行)——同 BatchD/BatchN 口径一并物理清。
    /// 批 BM D2:开放接口探针行(批 BM D4)同批物理清;本方法幂等,DisposeAsync 兜底复用。</summary>
    private static async Task CleanupAsync(IServiceProvider sp, IEnumerable<long> tenantIds, IEnumerable<long> userIds,
        IEnumerable<long> openAccessIds = null!)
    {
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var otpRepo = sp.GetRequiredService<SqlSugarRepository<SysUserOtp>>();

        var oids = openAccessIds?.Where(id => id > 0).ToList() ?? new List<long>();
        if (oids.Count > 0)
        {
            await sp.GetRequiredService<SqlSugarRepository<SysOpenAccess>>().DeletePhysicalByAsync(a => oids.Contains(a.Id));
        }

        var tids = tenantIds.Where(id => id != SqlSugarConst.DefaultTenantId).ToList();
        if (tids.Count > 0)
        {
            await tenantRepo.DeletePhysicalByAsync(t => tids.Contains(t.Id));
        }

        var uids = userIds.ToList();
        if (uids.Count > 0)
        {
            await otpRepo.DeletePhysicalByAsync(o => uids.Contains(o.UserId));
            await userRepo.DeletePhysicalByAsync(u => uids.Contains(u.Id));
        }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// 框架表增量建(InitTables 扫 Core 程序集 → 建 sys_tenant 等缺表,幂等)。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
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
                    // 测试占位密钥(32 字节 Base64):仅本测试解析 AuthService→CryptogramHelper 需要;加解密对称,与生产值无关
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    // Jwt 段(开发占位口径,与 appsettings 同源):两阶段登录签发 token 需要
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config); // AuthService/SysUserService 依赖
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(SysTenantService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db); // 扫 Core 程序集 → 建 sys_tenant 等缺表(增量幂等)
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
