// 批 S-G 纵深期·错误码覆盖·A 域(B5a1 臂,真实 PG;装置照抄 BatchSGSecurityDeepTest/BatchSGConsistencyTest 的
// ServiceProvider+DbScope+探针清理骨架,登录路径配 Jwt 段照 BatchAXOtpFlowTest,文件域 IWebHostEnvironment/
// IFileContentStore 照 BatchBUSysFileTest,开放接口中间件零库装置照 BatchVOpenSchemeTest):
//
// ① 被测面 = A 域(Auth / User / OTP / Password / File / Import / Export / OpenAccess)错误码中
//    「当前 src/Test 无任何断言」的 30 条(盘点件 scripts/error-code-coverage.mjs --domain=A,基线 6/40):
//    每条错误码 = 一条用例路径,调真实服务/中间件触发并**双断言**(AppException: Code==1 + ErrorCode==ErrorCodes.X;
//    LoginResult: Success==false + ErrorCode==ErrorCodes.X;中间件 HTTP 体: code==401 + errCode==ErrorCodes.X)。
//    BadRequestException(文件域)无 Code 属性(HTTP 400 语义由出口映射),断言 ErrorCode——响应体 errCode 由
//    ExceptionMiddleware 统一回带,不在本测试范围。
// ② Export 域专属错误码:ErrorCodes.cs 无 export.* 常量(导出走 dynamic_query/文件域码),A 域内 Export 零码,
//    无可测对象(脚本报告同口径)。
// ③ 预留死码(AuthAccountLocked/AuthOtpInvalid/AuthTokenExpired/AuthNoPermission):src/Core+src/Web.Core
//    生产 0 抛出点(2026-10-26 实核)——无服务分支可自然触发,本件以「常量字面 ↔ Lang.*.json 资源键」链路钉住
//    (码值被改/资源被删即红),证明该码出口渲染链路完整;触发路径缺失如实留在缺口清单,不伪造。
// ④ 探针纪律:用例 finally 物理清理(探针用户/OTP 行/密码历史/sys_file/sys_open_access/登录日志/缓存键),
//    DisposeAsync 再兜底一次(幂等);PG 不可达 Assert.Fail 禁跳过。原生 SQL 清理照 BatchSGConsistencyTest 口径。
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdminCore.Core;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.File;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using AdminCore.Web.Core.Auth;
using AdminCore.Web.Core.Auth.SignatureAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageATest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例 finally 已清,DisposeAsync 再兜底(幂等)
    private readonly List<long> _probeUserIds = new();
    private readonly List<long> _probeFileIds = new();
    private readonly List<long> _probeOpenAccessIds = new();
    private readonly List<long> _probePwdRecordUserIds = new();

    public BatchSGErrCodeCoverageATest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_probeUserIds.Count == 0 && _probeFileIds.Count == 0 && _probeOpenAccessIds.Count == 0 && _probePwdRecordUserIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        foreach (var fileId in _probeFileIds)
        {
            await dbScope.Db.Ado.ExecuteCommandAsync($"DELETE FROM sys_file WHERE id={fileId}");
        }

        foreach (var oaId in _probeOpenAccessIds)
        {
            await dbScope.Db.Ado.ExecuteCommandAsync($"DELETE FROM sys_open_access WHERE id={oaId}");
        }

        foreach (var userId in _probePwdRecordUserIds)
        {
            await dbScope.Db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_password_record WHERE \"UserId\"={userId}");
        }

        foreach (var userId in _probeUserIds)
        {
            await CleanupProbeUserAsync(dbScope.Db, userId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Auth 域(LoginResult 失败不抛异常:双断言 Success==false + ErrorCode)
    // ─────────────────────────────────────────────────────────────

    // auth.too_many_attempts:错误计数 30min/5 次——5 次错密码建立计数后,**正确密码**也被计数门拒绝(查账号前拦截)。
    [Fact]
    public async Task 登录_错误计数达上限_正确密码也拒抛AuthTooManyAttempts()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var auth = sp.GetRequiredService<AuthService>();
        var cache = sp.GetRequiredService<ICacheService>();
        var account = NewAccount("tm");
        var user = await InsertProbeUserAsync(sp, account);

        try
        {
            // 5 次错密码:每次原子 +1(首次建立 30 分钟窗口)——计数键进程内 Memory 档(装置未配 Redis 连接串)
            for (var i = 0; i < 5; i++)
            {
                var failed = await auth.LoginAsync(account, WrongPassword);
                Assert.False(failed.Success);
                Assert.Equal(ErrorCodes.AuthLoginFailed, failed.ErrorCode); // 前置路径对照(错密码 → login_failed)
            }

            // 第 6 次**正确密码**:计数门(>=5)在查账号之前 → too_many_attempts
            var r = await auth.LoginAsync(account, Password);
            Assert.False(r.Success);
            Assert.Equal(ErrorCodes.AuthTooManyAttempts, r.ErrorCode);

            _output.WriteLine("判定:5 次错密码后正确密码被拒 ErrorCode=auth.too_many_attempts(计数键在查账号前拦截)");
        }
        finally
        {
            await CleanupLoginStateAsync(sp, cache, user.Id, account);
        }
    }

    // auth.account_disabled:账号存在 + Status!=0 → 停用单独提示(与"账号或密码错误"分流)。
    [Fact]
    public async Task 登录_账号已停用_抛AuthAccountDisabled()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var auth = sp.GetRequiredService<AuthService>();
        var cache = sp.GetRequiredService<ICacheService>();
        var account = NewAccount("dis");
        var user = await InsertProbeUserAsync(sp, account, status: 1); // 停用探针

        try
        {
            var r = await auth.LoginAsync(account, Password); // 密码正确,仅状态停用
            Assert.False(r.Success);
            Assert.Equal(ErrorCodes.AuthAccountDisabled, r.ErrorCode);
        }
        finally
        {
            await CleanupLoginStateAsync(sp, cache, user.Id, account);
        }
    }

    // auth.challenge_expired:二阶段挑战失效——① challengeId 空白(前置拒绝);② 合法数字但缓存无该挑战(缺失/已消费/已过期统一按过期回复)。
    [Fact]
    public async Task 二阶段_挑战缺失或空白_抛AuthChallengeExpired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var auth = sp.GetRequiredService<AuthService>();
        var baseline = await dbScope.Db.Ado.GetIntAsync("SELECT COALESCE(MAX(id),0) FROM sys_log_login");

        try
        {
            // 分支①:challengeId 空白 → 前置拒绝(不消耗任何计数)
            var blank = await auth.LoginOtpAsync(" ", "123456");
            Assert.False(blank.Success);
            Assert.Equal(ErrorCodes.AuthChallengeExpired, blank.ErrorCode);

            // 分支②:challengeId 合法但挑战不存在 → 缓存 GetAsync 缺失 → 按过期回复(防状态泄露)
            var missing = await auth.LoginOtpAsync("990000000000000001", "123456");
            Assert.False(missing.Success);
            Assert.Equal(ErrorCodes.AuthChallengeExpired, missing.ErrorCode);

            _output.WriteLine("判定:二阶段空白/缺失挑战均回 ErrorCode=auth.challenge_expired(两分支双断言)");
        }
        finally
        {
            // 两分支登录日志 Account 均为 null:按 id 基线 + 匿名行 + 精确 Message 窄删(只清本用例插入行)
            await dbScope.Db.Ado.ExecuteCommandAsync(
                "DELETE FROM sys_log_login WHERE id > " + baseline +
                " AND \"Account\" IS NULL AND \"Message\"='登录验证已过期,请重新登录'");
        }
    }

    // 预留死码×4:生产 0 抛出点(2026-10-26 实核 src/Core+src/Web.Core)——无法经服务分支自然触发。
    // 替代证明:钉「常量字面 ↔ Lang.*.json 资源键」链路——四码是稳定码契约的一部分,码值被改/资源被删即红;
    // 触发路径缺口如实保留在 scripts/error-code-coverage.mjs 报告的"预留码"分类,不伪造服务调用。
    [Fact]
    public void 预留死码四Auth码_生产零引用_钉常量与资源键链路()
    {
        Assert.Equal("auth.account_locked", ErrorCodes.AuthAccountLocked);
        Assert.Equal("auth.otp_invalid", ErrorCodes.AuthOtpInvalid);
        Assert.Equal("auth.token_expired", ErrorCodes.AuthTokenExpired);
        Assert.Equal("auth.no_permission", ErrorCodes.AuthNoPermission);

        var zh = ReadResource("Resources/Lang.zh-CN.json");
        var en = ReadResource("Resources/Lang.en.json");
        foreach (var code in new[] { ErrorCodes.AuthAccountLocked, ErrorCodes.AuthOtpInvalid, ErrorCodes.AuthTokenExpired, ErrorCodes.AuthNoPermission })
        {
            Assert.Contains(code, zh);
            Assert.Contains(code, en);
        }

        _output.WriteLine("判定:auth.account_locked/otp_invalid/token_expired/no_permission 常量字面与中英资源键齐(生产零抛出点,见头注释③)");
    }

    // ─────────────────────────────────────────────────────────────
    // User 域(SysUserService;AppException 双断言 Code==1 + ErrorCode)
    // ─────────────────────────────────────────────────────────────

    // user.credentials_required:AddAsync 账号/密码空白 → 查库前抛(零副作用)。
    [Fact]
    public async Task 新建用户_账号或密码空白_抛UserCredentialsRequired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();

        var ex = await Assert.ThrowsAsync<AppException>(
            () => svc.AddAsync(new AddUserInput("   ", "", null, null, null, null, null)));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.UserCredentialsRequired, ex.ErrorCode);
    }

    // user.account_required:UnlockLogin 账号空白 → 清缓存前抛(零副作用)。
    [Fact]
    public async Task 解锁登录_账号空白_抛UserAccountRequired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.UnlockLogin(new UnlockLoginInput("   ")));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.UserAccountRequired, ex.ErrorCode);
    }

    // user.org_not_found:AddAsync 机构归属校验(Insert 前抛,零写库)。
    [Fact]
    public async Task 新建用户_机构不存在_抛UserOrgNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();

        var ex = await Assert.ThrowsAsync<AppException>(
            () => svc.AddAsync(new AddUserInput(NewAccount("org"), Password, null, null, null, 990000000001L, null)));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.UserOrgNotFound, ex.ErrorCode);
    }

    // user.pos_not_found:AddAsync 职位归属校验(Insert 前抛,零写库)。
    [Fact]
    public async Task 新建用户_职位不存在_抛UserPosNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();

        var ex = await Assert.ThrowsAsync<AppException>(
            () => svc.AddAsync(new AddUserInput(NewAccount("pos"), Password, null, null, null, null, 990000000002L)));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.UserPosNotFound, ex.ErrorCode);
    }

    // user.role_not_found:GrantRoleAsync 角色存在性校验(先清后挂之前抛——零写库,与 AddAsync 的"先插后校验"不同序)。
    [Fact]
    public async Task 分配角色_角色不存在_抛UserRoleNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("rnf");
        var user = await InsertProbeUserAsync(sp, account);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.GrantRoleAsync(new GrantRoleInput(user.Id, new List<long> { 990000000003L })));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserRoleNotFound, ex.ErrorCode);

            // 校验先于全量替换:守卫缺失时本探针角色链会被先清后挂——断言零角色行佐证"先校验后写"
            Assert.Equal(0, await CountByUserAsync(dbScope.Db, "sys_user_role", user.Id));
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.super_admin_undeletable:DeleteAsync 目标=AccountType 999 → 守卫先于任何写库(物理删在守卫后)。
    [Fact]
    public async Task 删除用户_目标超管_抛UserSuperAdminUndeletable且admin未删()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var admin = await FindAdminAsync(dbScope.Db);

        try
        {
            SetUser(diScope.ServiceProvider, userId: 990000000010L, accountType: 777, account: "sga5_del_op");

            var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(admin.Id, 990000000010L));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserSuperAdminUndeletable, ex.ErrorCode);
        }
        finally
        {
            // 守卫先于写库:admin 行必须原样健在(软删列未动)
            Assert.Equal(1, await dbScope.Db.Ado.GetIntAsync(
                $"SELECT COUNT(*) FROM sys_user WHERE id={admin.Id} AND \"IsDelete\"=false"));
        }
    }

    // user.self_delete_forbidden:DeleteAsync 目标=当前登录人(防自锁)——守卫先于关联清理与软删。
    [Fact]
    public async Task 删除用户_删除本人_抛UserSelfDeleteForbidden且探针未删()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("self");
        var user = await InsertProbeUserAsync(sp, account);

        try
        {
            SetUser(sp, user.Id, 777, account);

            var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(user.Id, user.Id));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserSelfDeleteForbidden, ex.ErrorCode);

            // 守卫先于软删:探针行未进回收站
            Assert.Equal(1, await dbScope.Db.Ado.GetIntAsync(
                $"SELECT COUNT(*) FROM sys_user WHERE id={user.Id} AND \"IsDelete\"=false"));
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.old_new_password_required:UpdatePassword 新旧密码空白 → 查库前抛(零副作用)。
    [Fact]
    public async Task 改密_新旧密码空白_抛UserOldNewPasswordRequired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("onp");
        var user = await InsertProbeUserAsync(sp, account);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.UpdatePassword(user.Id, new UpdatePasswordInput("", " ")));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserOldNewPasswordRequired, ex.ErrorCode);
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.old_password_wrong:旧密码恒定时间比对失败 → 策略覆写前抛;finally 复核密码哈希未变。
    [Fact]
    public async Task 改密_旧密码错误_抛UserOldPasswordWrong且密码未变()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("opw");
        var user = await InsertProbeUserAsync(sp, account);
        var md5Baseline = await PwdMd5Async(dbScope.Db, user.Id);
        var recordBaseline = await CountByUserAsync(dbScope.Db, "sys_user_password_record", user.Id);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.UpdatePassword(user.Id, new UpdatePasswordInput("Sga5_WrongOld!1", "Sga5_New!2026")));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserOldPasswordWrong, ex.ErrorCode);

            // 拒绝路径零写库:密码哈希与历史行数均未动
            Assert.Equal(md5Baseline, await PwdMd5Async(dbScope.Db, user.Id));
            Assert.Equal(recordBaseline, await CountByUserAsync(dbScope.Db, "sys_user_password_record", user.Id));
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.new_password_required:ResetPwd 新密码空白 → 查库前抛(零副作用)。
    [Fact]
    public async Task 重置密码_新密码空白_抛UserNewPasswordRequired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("npr");
        var user = await InsertProbeUserAsync(sp, account);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.ResetPwd(new ResetPwdInput(user.Id, "")));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserNewPasswordRequired, ex.ErrorCode);
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.password_weak:SysPassword:EnableStrength=true + 弱密码不过内置强度正则(策略覆写前抛,零写库)。
    [Fact]
    public async Task 重置密码_强度开启弱密码_抛UserPasswordWeak()
    {
        var overrides = new Dictionary<string, string?> { ["SysPassword:EnableStrength"] = "true" };
        await using var dbScope = await DbScope.CreateAsync(overrides);
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("weak");
        var user = await InsertProbeUserAsync(sp, account);
        var md5Baseline = await PwdMd5Async(dbScope.Db, user.Id);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.ResetPwd(new ResetPwdInput(user.Id, "weak")));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserPasswordWeak, ex.ErrorCode);

            // 策略校验先于覆写:密码哈希未动
            Assert.Equal(md5Baseline, await PwdMd5Async(dbScope.Db, user.Id));
        }
        finally
        {
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // user.password_reused:SysPassword:EnableHistory=true + 预插历史行(新密码 MD5 命中最近历史)→ 拒绝。
    [Fact]
    public async Task 改密_历史重复开启_新密码命中历史_抛UserPasswordReused()
    {
        var overrides = new Dictionary<string, string?> { ["SysPassword:EnableHistory"] = "true" };
        await using var dbScope = await DbScope.CreateAsync(overrides);
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysUserService>();
        var account = NewAccount("reuse");
        var user = await InsertProbeUserAsync(sp, account);
        var md5Baseline = await PwdMd5Async(dbScope.Db, user.Id);
        _probePwdRecordUserIds.Add(user.Id);

        // 历史探针行:PasswordMd5 = md5("Sga5_ReusedPwd!1")——与新密码明文同 MD5
        var recordRepo = sp.GetRequiredService<ISqlSugarRepository<SysUserPasswordRecord>>();
        await recordRepo.InsertAsync(new SysUserPasswordRecord
        {
            UserId = user.Id,
            PasswordMd5 = Md5Hex("Sga5_ReusedPwd!1")
        });

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.UpdatePassword(user.Id, new UpdatePasswordInput(Password, "Sga5_ReusedPwd!1")));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.UserPasswordReused, ex.ErrorCode);

            // 历史比对先于覆写:密码哈希未动、无新历史行(仅本用例预插的 1 条)
            Assert.Equal(md5Baseline, await PwdMd5Async(dbScope.Db, user.Id));
            Assert.Equal(1, await CountByUserAsync(dbScope.Db, "sys_user_password_record", user.Id));
        }
        finally
        {
            await recordRepo.DeletePhysicalByAsync(r => r.UserId == user.Id);
            await CleanupProbeUserAsync(dbScope.Db, user.Id);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Import 域(SysPosService.ImportAsync,批 CP 代表端点;全部在落库前抛,零写库)
    // ─────────────────────────────────────────────────────────────

    // import.file_empty 分支①:文件对象缺失。
    [Fact]
    public async Task 导入_文件对象缺失_抛ImportFileEmpty()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPosService>();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportAsync(null!));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.ImportFileEmpty, ex.ErrorCode);
    }

    // import.file_format_invalid:仅支持 .xlsx/.csv——.txt 后缀前置拒绝。
    [Fact]
    public async Task 导入_txt后缀_抛ImportFileFormatInvalid()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPosService>();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportAsync(BuildFormFile("职位名称,职位编码,排序,备注\n", "sga5_probe.txt")));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.ImportFileFormatInvalid, ex.ErrorCode);
    }

    // import.file_empty 分支②:仅表头零数据行(批 CPΔ 口径:解析后零数据行按空文件处理)。
    [Fact]
    public async Task 导入_仅表头零数据行_抛ImportFileEmpty()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPosService>();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportAsync(BuildFormFile("职位名称,职位编码,排序,备注\n", "sga5_probe.csv")));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.ImportFileEmpty, ex.ErrorCode);
    }

    // import.row_limit_exceeded:单次上限 5000 行(ImportService.MaxRows),5001 行数据前置拒绝(Validate 前抛,零写库)。
    [Fact]
    public async Task 导入_超五千行_抛ImportRowLimitExceeded()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysPosService>();

        var sb = new StringBuilder("职位名称,职位编码,排序,备注\n");
        for (var i = 1; i <= 5001; i++)
        {
            sb.Append($"职位{i:0000},sga5c{i:0000},{i % 100},备注{i}\n");
        }

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ImportAsync(BuildFormFile(sb.ToString(), "sga5_probe.csv")));
        Assert.Equal(1, ex.Code);
        Assert.Equal(ErrorCodes.ImportRowLimitExceeded, ex.ErrorCode);
    }

    // ─────────────────────────────────────────────────────────────
    // File 域(SysFileService;BadRequestException:断言 ErrorCode——HTTP 400 语义由出口映射,
    // 该异常类型无 Code 属性,双断言的 Code 侧落在下方 OpenApi 中间件用例的 HTTP 体 code 上)
    // ─────────────────────────────────────────────────────────────

    // file.content_required:文件对象缺失/空内容 → 管线第一关(零副作用)。
    [Fact]
    public async Task 上传_文件为空_抛FileContentRequired()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.UploadAsync(null!, null));
        Assert.Equal(ErrorCodes.FileContentRequired, ex.ErrorCode);
    }

    // file.type_not_allowed:客户端 MIME 不在 Upload:ContentType 白名单 → 管线①(零副作用)。
    [Fact]
    public async Task 上传_类型不在白名单_抛FileTypeNotAllowed()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();
        var file = BuildFormFile("probe", "sga5_probe.txt", contentType: "application/x-sga5-probe");

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.UploadAsync(file, null));
        Assert.Equal(ErrorCodes.FileTypeNotAllowed, ex.ErrorCode);
    }

    // file.size_exceeded:Upload:MaxSize=0(用例级配置覆盖)→ 任意非空文件都超限 → 管线②(零副作用)。
    [Fact]
    public async Task 上传_超大小上限_抛FileSizeExceeded()
    {
        var overrides = new Dictionary<string, string?> { ["Upload:MaxSize"] = "0" };
        await using var dbScope = await DbScope.CreateAsync(overrides);
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();
        var file = BuildFormFile("probe", "sga5_probe.txt"); // contentType 合法(text/plain 在装置白名单)

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.UploadAsync(file, null));
        Assert.Equal(ErrorCodes.FileSizeExceeded, ex.ErrorCode);
    }

    // file.suffix_not_allowed:后缀不在 Upload:AllowSuffix 表(白名单而非黑名单)→ 管线③(零副作用)。
    [Fact]
    public async Task 上传_后缀不在白名单_抛FileSuffixNotAllowed()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();
        var file = BuildFormFile("probe", "sga5_probe.xyz"); // 类型/大小合法,仅后缀非法

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.UploadAsync(file, null));
        Assert.Equal(ErrorCodes.FileSuffixNotAllowed, ex.ErrorCode);
    }

    // file.not_found:下载记录不存在(真实库查询零行,零写库)。
    [Fact]
    public async Task 下载_记录不存在_抛FileNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.DownloadAsync(990000000004L, "deadbeef"));
        Assert.Equal(ErrorCodes.FileNotFound, ex.ErrorCode);
    }

    // file.md5_mismatch:下载 URL 防篡改校验——请求 md5 与记录 FileMd5 不符(校验在取流前,Local 探针行无物理文件无碍)。
    [Fact]
    public async Task 下载_md5不符_抛FileMd5Mismatch()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysFileService>();
        var fileId = await InsertProbeFileAsync(sp, md5: new string('a', 32), provider: SysFileService.ProviderLocal);

        try
        {
            var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.DownloadAsync(fileId, "deadbeef"));
            Assert.Equal(ErrorCodes.FileMd5Mismatch, ex.ErrorCode);
        }
        finally
        {
            await DeleteProbeFileAsync(dbScope.Db, fileId);
        }
    }

    // file.content_missing:记录存在 + md5 匹配 + Provider=Db 但 sys_file_content 无内容行 → 取流 null(真实库路径)。
    [Fact]
    public async Task 下载_Db档无内容行_抛FileContentMissing()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysFileService>();
        var md5 = new string('b', 32);
        var fileId = await InsertProbeFileAsync(sp, md5: md5, provider: SysFileService.ProviderDb);

        try
        {
            var ex = await Assert.ThrowsAsync<BadRequestException>(() => svc.DownloadAsync(fileId, md5));
            Assert.Equal(ErrorCodes.FileContentMissing, ex.ErrorCode);
        }
        finally
        {
            await DeleteProbeFileAsync(dbScope.Db, fileId);
        }
    }

    // file.not_found_ids:UpdateFileByDataId 权威集合含不存在文件 → 回填前抛 400 防悬空引用(零写库)。
    [Fact]
    public async Task 业务关联同步_集合含不存在文件_抛FileNotFoundIds()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysFileService>();

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => svc.UpdateFileByDataIdAsync(990000000005L, new List<long> { 990000000006L }));
        Assert.Equal(ErrorCodes.FileNotFoundIds, ex.ErrorCode);
    }

    // ─────────────────────────────────────────────────────────────
    // OpenAccess 域(SysOpenAccessService;AppException 双断言)
    // ─────────────────────────────────────────────────────────────

    // open_access.not_found:身份行不存在——改绑/删除/重置密钥三入口同码(GetFirstAsync 查不到统一抛)。
    [Fact]
    public async Task 开放接口身份_不存在_三入口同抛OpenAccessNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysOpenAccessService>();
        var ghostId = 990000000007L;

        var exUpdate = await Assert.ThrowsAsync<AppException>(
            () => svc.UpdateAsync(new UpdateOpenAccessInput(ghostId, 0, SqlSugarConst.DefaultTenantId)));
        Assert.Equal(1, exUpdate.Code);
        Assert.Equal(ErrorCodes.OpenAccessNotFound, exUpdate.ErrorCode);

        var exDelete = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(ghostId));
        Assert.Equal(1, exDelete.Code);
        Assert.Equal(ErrorCodes.OpenAccessNotFound, exDelete.ErrorCode);

        var exReset = await Assert.ThrowsAsync<AppException>(() => svc.ResetSecretAsync(ghostId));
        Assert.Equal(1, exReset.Code);
        Assert.Equal(ErrorCodes.OpenAccessNotFound, exReset.ErrorCode);
    }

    // open_access.access_key_exists:建号 AccessKey 唯一性校验(Insert 前抛——探针行在库,新号同 key 被拒)。
    [Fact]
    public async Task 建号_AccessKey已存在_抛OpenAccessAccessKeyExists()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysOpenAccessService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysOpenAccess>>();
        var accessKey = $"sga5ak{Guid.NewGuid():N}"[..20];
        var probe = new SysOpenAccess
        {
            AccessKey = accessKey,
            AccessSecret = "sga5_probe_secret_not_real",
            BindUserId = 0,
            BindTenantId = SqlSugarConst.DefaultTenantId
        };
        await repo.InsertAsync(probe);
        _probeOpenAccessIds.Add(probe.Id);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.AddAsync(new AddOpenAccessInput(accessKey, 0, SqlSugarConst.DefaultTenantId)));
            Assert.Equal(1, ex.Code);
            Assert.Equal(ErrorCodes.OpenAccessAccessKeyExists, ex.ErrorCode);

            // 唯一性守卫先于插入:库内仍只有探针这一行
            Assert.Equal(1, await repo.AsQueryable().Where(a => a.AccessKey == accessKey).CountAsync());
        }
        finally
        {
            await repo.DeletePhysicalByAsync(a => a.AccessKey == accessKey);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // OpenApi 中间件(PermissionMiddleware 开放接口前置段;零库装置照 BatchVOpenSchemeTest,
    // HTTP 体双断言 code==401 + errCode==ErrorCodes.X——ApiResult camelCase 出口)
    // ─────────────────────────────────────────────────────────────

    // common.open_api_scheme_missing:[OpenApi] 端点缺 AuthenticationSchemes 声明 → 401(防裸开放接口)。
    [Fact]
    public async Task 开放端点_缺Scheme声明_401体errCode为OpenApiSchemeMissing()
    {
        var auth = new FakeAuthService(new HashSet<string>());
        var (context, next, _) = BuildOpenApiContext(string.Empty, auth); // schemes 空白
        var (provider, db) = BuildOpenApiProvider(context);

        var mw = new PermissionMiddleware(next.Invoke, db, provider.GetRequiredService<IServiceScopeFactory>());
        await mw.InvokeAsync(context);

        Assert.False(next.Called);
        var (code, errCode) = await ReadApiResultAsync(context);
        Assert.Equal(401, code);
        Assert.Equal(ErrorCodes.OpenApiSchemeMissing, errCode);
        await provider.DisposeAsync();
    }

    // common.open_api_auth_failed:[OpenApi] 端点声明了 scheme 但认证全失败 → 401(细分消息经 Items 透传)。
    [Fact]
    public async Task 开放端点_认证失败_401体errCode为OpenApiAuthFailed()
    {
        var auth = new FakeAuthService(new HashSet<string>(), failMsg: "sga5 签名校验失败:签名不匹配");
        var (context, next, _) = BuildOpenApiContext("SigA", auth);
        var (provider, db) = BuildOpenApiProvider(context);

        var mw = new PermissionMiddleware(next.Invoke, db, provider.GetRequiredService<IServiceScopeFactory>());
        await mw.InvokeAsync(context);

        Assert.False(next.Called);
        var (code, errCode) = await ReadApiResultAsync(context);
        Assert.Equal(401, code);
        Assert.Equal(ErrorCodes.OpenApiAuthFailed, errCode);
        await provider.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────────
    // 造数与清理辅助
    // ─────────────────────────────────────────────────────────────
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";
    private const string Password = "Sga5_Pwd!2026";
    private const string WrongPassword = "Sga5_Wrong!x";

    // 账号上限 20 字符(短 tag 时原 [..20] 会越界;此处按实际长度截断,长度不足不补)
    private static string NewAccount(string tag)
    {
        var account = $"sga5{tag}{Interlocked.Increment(ref _seq):x5}";
        return account.Length > 20 ? account[..20] : account;
    }
    private static long _seq = 0xA5000;

    private static string ReadResource(string relative)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relative));

    private static string Md5Hex(string text)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<string> PwdMd5Async(ISqlSugarClient db, long userId)
        => await db.Ado.GetStringAsync($"SELECT md5(\"Password\") FROM sys_user WHERE id={userId}");

    private static async Task<int> CountByUserAsync(ISqlSugarClient db, string table, long userId)
        => await db.Ado.GetIntAsync($"SELECT COUNT(*) FROM {table} WHERE \"UserId\"={userId}");

    private static async Task<int> CountByFileIdAsync(ISqlSugarClient db, string table, long id)
        => await db.Ado.GetIntAsync($"SELECT COUNT(*) FROM {table} WHERE id={id}");

    /// <summary>库中真实超管 admin 行(前置自证:无行即装置不满足,明确失败;照 BatchSGSecurityDeepTest 口径)。</summary>
    private static async Task<(long Id, string PwdMd5)> FindAdminAsync(ISqlSugarClient db)
    {
        var ids = await db.Ado.SqlQueryAsync<long>(
            "SELECT id FROM sys_user WHERE \"Account\"='admin' AND \"IsDelete\"=false");
        var id = Assert.Single(ids);
        var pwdMd5 = await db.Ado.GetStringAsync($"SELECT md5(\"Password\") FROM sys_user WHERE id={id}");
        return (id, pwdMd5);
    }

    private static async Task<SysUser> InsertProbeUserAsync(IServiceProvider sp, string account, int status = 0)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = account,
            Password = Pbkdf2Helper.HashPassword(Password),
            RealName = "SG-A5 探针",
            Status = status,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        await repo.InsertAsync(user);
        return user;
    }

    /// <summary>文件探针行(不写物理文件/内容行——被测路径在取流前抛,照 BatchBUSysFileTest D2 造数口径)。</summary>
    private static async Task<long> InsertProbeFileAsync(IServiceProvider sp, string md5, string provider)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysFile>>();
        var entity = new SysFile
        {
            FileMd5 = md5,
            SizeKb = 1,
            FileName = $"sga5_{Guid.NewGuid():N}",
            FilePath = "2026/10/26",
            Suffix = ".txt",
            Provider = provider,
            Url = "/uploads/2026/10/26/sga5_probe.txt"
        };
        await repo.InsertAsync(entity);
        return entity.Id;
    }

    private static async Task DeleteProbeFileAsync(ISqlSugarClient db, long fileId)
    {
        if (fileId == 0)
        {
            return;
        }

        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_file WHERE id={fileId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_file_content WHERE \"SysFileId\"={fileId}");
    }

    /// <summary>登录态清理:缓存计数键 + 探针用户/OTP 行 + 按探针账号的登录日志(物理删,幂等;照 BatchAXOtpFlowTest)。</summary>
    private static async Task CleanupLoginStateAsync(IServiceProvider sp, ICacheService cache, long userId, string account)
    {
        await cache.RemoveAsync($"{CacheConst.KeyPasswordError}{account}");
        await cache.RemoveAsync($"{CacheConst.KeyOtpError}{account}");
        await CleanupProbeUserAsync(sp.GetRequiredService<ISqlSugarClient>(), userId);
        var loginLogRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogLogin>>();
        await loginLogRepo.DeletePhysicalByAsync(l => l.Account == account);
    }

    /// <summary>物理清理探针用户及关联表行(原生 DELETE;照 BatchSGConsistencyTest 口径,幂等)。</summary>
    private static async Task CleanupProbeUserAsync(ISqlSugarClient db, long userId)
    {
        if (userId == 0)
        {
            return;
        }

        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_notice_user WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_otp WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_password_record WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_role WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user WHERE id={userId}");
    }

    /// <summary>构造导入用 IFormFile(csv 纯文本;表头列名 = ImportPosInput 的 ImportColumn Header)。</summary>
    private static IFormFile BuildFormFile(string content, string fileName, string contentType = "text/csv")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    /// <summary>模拟登录身份(与 BatchSGSecurityDeepTest.SetUser 同口径:JwtClaimNames 同键)。</summary>
    private static void SetUser(IServiceProvider provider, long userId, int accountType, string? account)
    {
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, userId.ToString()),
            new Claim(JwtClaimNames.Account, account ?? $"u{userId}"),
            new Claim(JwtClaimNames.AccountType, accountType.ToString()),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        accessor.HttpContext = context;
    }

    // ─────────────────────────────────────────────────────────────
    // OpenApi 中间件零库装置(照 BatchVOpenSchemeTest:Fake IAuthenticationService + [OpenApi] 端点元数据;
    // 被测两分支在认证前置段即 return,不触达租户/机构过滤,故零库可跑)
    // ─────────────────────────────────────────────────────────────
    private sealed class FakeAuthService : IAuthenticationService
    {
        private readonly HashSet<string> _succeedSchemes;
        private readonly string? _failMsg;

        public FakeAuthService(HashSet<string> succeedSchemes, string? failMsg = null)
        {
            _succeedSchemes = succeedSchemes;
            _failMsg = failMsg;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme)
        {
            if (_succeedSchemes.Contains(scheme))
            {
                var identity = new ClaimsIdentity(new[]
                {
                    new Claim("AccountType", "999"),
                    new Claim("UserId", "1"),
                }, "Fake");
                var principal = new ClaimsPrincipal(identity);
                context.User = principal;
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(), scheme)));
            }

            if (_failMsg is not null)
            {
                context.Items[SignatureAuthenticationDefaults.AuthenticateFailMsgKey] = _failMsg;
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal? principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, ClaimsPrincipal? principal) => Task.CompletedTask;
    }

    private sealed class RecordingNext
    {
        public bool Called;
        public Task Invoke(HttpContext ctx) { Called = true; return Task.CompletedTask; }
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        private readonly HttpContext _context;
        public FixedHttpContextAccessor(HttpContext context) => _context = context;
        public HttpContext? HttpContext { get => _context; set { } }
    }

    private static (HttpContext context, RecordingNext next, FakeAuthService auth) BuildOpenApiContext(
        string schemes, FakeAuthService auth)
    {
        var next = new RecordingNext();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(auth)
                .BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = "/api/openDemo/hello";

        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new object[]
            {
                new AdminCore.Core.Attributes.OpenApiAttribute(),
                new AuthorizeAttribute { AuthenticationSchemes = schemes },
            }),
            "test");
        context.SetEndpoint(endpoint);
        return (context, next, auth);
    }

    private static (ServiceProvider provider, ISqlSugarClient db) BuildOpenApiProvider(HttpContext context)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "sga5_test_secret_key_32bytes______________",
                ["Jwt:Issuer"] = "sga5-test",
                ["Jwt:Audience"] = "sga5-test-api",
                ["Cryptogram:Key"] = "c2dhNV90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM=",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar(config);
        services.AddSingleton<IHttpContextAccessor>(new FixedHttpContextAccessor(context));
        services.AddSingleton<UserManager>(
            sp => new UserManager(sp.GetRequiredService<IHttpContextAccessor>()));
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ISqlSugarClient>());
    }

    /// <summary>读统一响应体(出口单源 AdminJsonOptions:camelCase + long→string):返回 (code, errCode)。</summary>
    private static async Task<(int Code, string? ErrCode)> ReadApiResultAsync(HttpContext context)
    {
        var ms = (MemoryStream)context.Response.Body;
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        var code = doc.RootElement.GetProperty("code").GetInt32();
        var errCode = doc.RootElement.TryGetProperty("errCode", out var ec) ? ec.GetString() : null;
        return (code, errCode);
    }

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG 的最小宿主(照 BatchSGSecurityDeepTest DbScope;登录路径补 Jwt 段照 BatchAXOtpFlowTest;
    // 文件域补 IWebHostEnvironment/IFileContentStore 与 Upload 白名单照 BatchBUSysFileTest;
    // configOverrides 供用例级配置覆盖——密码策略/MaxSize 等,默认配置不受影响)
    // ─────────────────────────────────────────────────────────────
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;

        public ServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        private string? _tempRoot;

        public static async Task<DbScope> CreateAsync(IReadOnlyDictionary<string, string?>? configOverrides = null)
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                scope.Provider = new ServiceCollection().BuildServiceProvider();
                return scope;
            }

            scope._tempRoot = Directory.CreateTempSubdirectory("sga5_").FullName;
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要(照既有 SG 装置)
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    // 登录路径(Jwt 段照 BatchAXOtpFlowTest;失败用例不签发,但 AddProjectOptions 绑定需要完整段)
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api",
                    // 文件域管线白名单(照生产 appsettings 形状;FileSizeExceeded 用例经 configOverrides 覆盖 MaxSize)
                    ["Upload:ContentType:0"] = "text/plain",
                    ["Upload:ContentType:1"] = "text/csv",
                    ["Upload:AllowSuffix:txt"] = "txt",
                    ["Upload:AllowSuffix:jpg"] = "jpg",
                    ["Upload:MaxSize"] = "10240"
                })
                .AddInMemoryCollection(configOverrides)
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR(); // SysUserService → SysOnlineUserService → IHubContext(测试无连接时推送 no-op,同批 SG 装配)
            // 文件域(SysFileService 构造依赖;照 BatchBUSysFileTest:宿主环境打桩 + 内容存取真实现)
            services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment(scope._tempRoot));
            services.AddSingleton<IFileContentStore, FileContentStore>();
            services.AddAutoServices(typeof(AuthService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            if (_tempRoot is not null && Directory.Exists(_tempRoot))
            {
                try
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
                catch
                {
                    // 临时目录清理尽力而为(Windows 文件占用竞态不影响测试结论)
                }
            }
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchSGConsistencyTest 口径)。</summary>
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

    /// <summary>最小 IWebHostEnvironment:仅 ContentRootPath 有值(DefaultFileProvider 只读它;照 BatchBUSysFileTest)。</summary>
    private sealed class FakeWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Production";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(contentRoot, "wwwroot");
        public IWebHostEnvironment UseContentRoot(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            return this;
        }

        public IWebHostEnvironment UseWebRoot(string? webRootPath)
        {
            WebRootPath = webRootPath ?? string.Empty;
            return this;
        }
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public void Dispose() { }
        public IChangeToken Watch(string filter) => new CancellationChangeToken(CancellationToken.None);
        public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
        public IFileInfo GetFileInfo(string subpath) => new NullFileInfo();
    }

    /// <summary>空 IFileInfo(Exists=false;照 BatchBUSysFileTest)。</summary>
    private sealed class NullFileInfo : IFileInfo
    {
        public bool Exists => false;
        public long Length => 0;
        public string PhysicalPath => string.Empty;
        public string Name => string.Empty;
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => throw new NotSupportedException();
    }
}
