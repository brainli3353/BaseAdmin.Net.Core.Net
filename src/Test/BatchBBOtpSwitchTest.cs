using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 批 BB「登录 OTP 全局开关(sys_otp_enabled)」验证件:被测主体 =
/// ① 开关关(值显式 False)→ <see cref="AuthService.LoginAsync"/> 密码通过**直接签发令牌**(Data=null/Token 非空/RefreshToken 非空);
/// ② 开关关 + 错密码 → 仍走失败路径(稳定错误码,无令牌);
/// ③ 开关开(True)→ 维持两阶段:密码初验出挑战(Data 非空/Token=null);
/// ④ 解析口径:仅显式 "false"(忽略大小写/首尾空白)关;true/True/缺失配置行/任何无法解析值 **fail-safe 视为开**;
/// ⑤ 探针清理:finally 复还种子值 + 物理清探针用户/OTP/登录日志,断言残留=0 且 admin 的 OTP 绑定行未动。
/// </summary>
/// <remarks>
/// 真实 PG(容器与 <c>BatchAXOtpFlowTest</c> 同款:AddSqlSugar/AddProjectOptions/AddCache/AddAutoServices/AddSignalR
/// ＋Cryptogram/Jwt 配置);PG 不可达时 Assert.Fail 降级声明。
/// 零污染:探针账号专属前缀 + 用例内物理清理;配置行复还种子值 "True"(缺失行用例先物理删、finally 复建)。
/// </remarks>
public class BatchBBOtpSwitchTest
{
    private static long _seq = 9_800_000;

    /// <summary>
    /// 运行级唯一后缀(2026-10-27 加)。<c>Account</c> 上有唯一索引 <c>u_sys_user_account("Account","TenantId") WHERE "IsDelete"=false</c>,
    /// 而 <see cref="_seq"/> 只保证<b>同一轮内</b>唯一 ⇒ 一旦某轮中断留下 <c>bb9800001</c>,下一轮同值必撞唯一索引,
    /// 且<b>症状表现为"另一条无关用例红"</b>(本仓 2026-10-27 实测踩过:红跑残留 bb9800001..4)。
    /// 加运行级前缀后残留与新探针不再互撞;前缀 <c>bb</c> 保留,人工清残留仍可 <c>"Account" LIKE 'bb%'</c>。
    /// </summary>
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..6];

    // 种子行 Id(SysConfigSeedData:登录 OTP 第二道校验开关)
    private const long OtpConfigId = 1300000000143L;

    // ── ① 关:密码直登——令牌非空、无挑战、附带 refresh token ────────────────────────────────
    [Fact]
    public async Task 开关关_正确密码_直登令牌非空且无挑战()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"bb{RunTag}{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var adminBefore = await AdminOtpSignatureAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            await SetOtpConfigValueAsync(sp, "False");
            var r = await auth.LoginAsync(account, Password);

            Assert.True(r.Success, $"开关关应密码直登成功(错误码={r.ErrorCode})");
            Assert.False(string.IsNullOrEmpty(r.Token), "开关关应直接签发 access token");
            Assert.Null(r.Data);                              // 无挑战对象
            Assert.False(string.IsNullOrEmpty(r.RefreshToken), "开关关应与二阶段同口径签发 refresh token");
        }
        finally
        {
            await FinishAsync(sp, user.Id, account, adminBefore);
        }
    }

    // ── ② 关:错密码仍失败——稳定错误码、无令牌 ────────────────────────────────────────────
    [Fact]
    public async Task 开关关_错密码_失败带错误码且无令牌()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"bb{RunTag}{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var adminBefore = await AdminOtpSignatureAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            await SetOtpConfigValueAsync(sp, "False");
            var r = await auth.LoginAsync(account, "bb-wrong-password");

            Assert.False(r.Success, "开关关不等于免密——错密码必须失败");
            Assert.Equal(ErrorCodes.AuthLoginFailed, r.ErrorCode);
            Assert.Null(r.Token);
            Assert.Null(r.Data);
        }
        finally
        {
            await FinishAsync(sp, user.Id, account, adminBefore);
        }
    }

    // ── ③ 开:维持两阶段——初验出挑战、无令牌 ──────────────────────────────────────────────
    [Fact]
    public async Task 开关开_密码初验_出挑战且无令牌()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"bb{RunTag}{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var adminBefore = await AdminOtpSignatureAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            await SetOtpConfigValueAsync(sp, "True");
            var r = await auth.LoginAsync(account, Password);

            Assert.True(r.Success, $"开关开应初验成功(错误码={r.ErrorCode})");
            Assert.Null(r.Token);
            Assert.NotNull(r.Data);
            Assert.False(string.IsNullOrEmpty(r.Data!.ChallengeId), "开关开应出挑战");
        }
        finally
        {
            await FinishAsync(sp, user.Id, account, adminBefore);
        }
    }

    // ── ④ 解析口径:仅显式 false 关;true/True/垃圾/空/缺失行 fail-safe 视为开 ───────────────
    [Fact]
    public async Task 解析口径_仅显式false关_其余开()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"bb{RunTag}{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var adminBefore = await AdminOtpSignatureAsync(sp);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            // 显式 false(忽略大小写/首尾空白)→ 关:密码直登
            foreach (var value in new[] { "false", "false ", " False " })
            {
                await SetOtpConfigValueAsync(sp, value);
                var r = await auth.LoginAsync(account, Password);
                Assert.True(r.Success, $"开关关(value=\"{value}\")应直登成功(错误码={r.ErrorCode})");
                Assert.False(string.IsNullOrEmpty(r.Token), $"开关关(value=\"{value}\")应签发令牌");
                Assert.Null(r.Data);
            }

            // true/True/无法解析值/空串 → 开:出挑战
            foreach (var value in new[] { "true", "True", "garbage", "" })
            {
                await SetOtpConfigValueAsync(sp, value);
                var r = await auth.LoginAsync(account, Password);
                Assert.True(r.Success, $"开关开(value=\"{value}\")应初验成功(错误码={r.ErrorCode})");
                Assert.Null(r.Token);
                Assert.False(string.IsNullOrEmpty(r.Data?.ChallengeId), $"开关开(value=\"{value}\")应出挑战");
            }

            // 配置行缺失 → fail-safe 视为开(绝不静默关闭二次验证)
            await DeleteOtpConfigAsync(sp);
            var missing = await auth.LoginAsync(account, Password);
            Assert.True(missing.Success, $"配置行缺失应初验成功(错误码={missing.ErrorCode})");
            Assert.Null(missing.Token);
            Assert.False(string.IsNullOrEmpty(missing.Data?.ChallengeId), "配置行缺失应 fail-safe 视为开");
        }
        finally
        {
            await FinishAsync(sp, user.Id, account, adminBefore);
        }
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    private const string Password = "P@ssw0rd!BB";

    private static async Task<SysUser> InsertUserAsync(IServiceProvider sp, string account)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = account,
            Password = Pbkdf2Helper.HashPassword(Password),
            RealName = "BB 探针",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        await repo.InsertAsync(user);
        return user;
    }

    /// <summary>设置 sys_otp_enabled 值:行存在走服务写路径(变更主动失效缓存);缺失按种子行同 Id 补建。</summary>
    private static async Task SetOtpConfigValueAsync(IServiceProvider sp, string value)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysConfig>>();
        var svc = sp.GetRequiredService<SysConfigService>();
        var row = await repo.GetFirstAsync(c => c.Code == SysConfigService.OtpEnabled);
        if (row is null)
        {
            await repo.InsertAsync(new SysConfig
            {
                Id = OtpConfigId,
                Name = "登录 OTP 第二道校验开关",
                Code = SysConfigService.OtpEnabled,
                Value = value,
                GroupCode = "Default",
                SysFlag = true,
                Remark = "批 BB 探针(复还种子值)"
            });
            await sp.GetRequiredService<AdminCore.Core.Cache.ICacheService>().RemoveAsync($"{CacheConst.KeyConfig}{SysConfigService.OtpEnabled}");
        }
        else
        {
            // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):仅"写开关"这一段临时挂超管,
            // 结束后立即还原未认证基线——登录者身份保持探针普通用户(777),OTP 挑战/直登语义不变。
            var accessor = (TestActorAccessor)sp.GetRequiredService<IHttpContextAccessor>();
            var previous = accessor.HttpContext;
            accessor.SetSuperAdmin();
            try
            {
                await svc.UpdateAsync(new UpdateConfigInput(row.Id, Value: value));
            }
            finally
            {
                accessor.HttpContext = previous;
            }
        }
    }

    /// <summary>物理删除 sys_otp_enabled 行并失效缓存(验证 fail-safe)。</summary>
    private static async Task DeleteOtpConfigAsync(IServiceProvider sp)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysConfig>>();
        await repo.DeletePhysicalByAsync(c => c.Code == SysConfigService.OtpEnabled);
        await sp.GetRequiredService<AdminCore.Core.Cache.ICacheService>().RemoveAsync($"{CacheConst.KeyConfig}{SysConfigService.OtpEnabled}");
    }

    /// <summary>收尾:复还种子值 True + 物理清探针行,断言残留=0 且 admin 的 OTP 绑定行未动。</summary>
    private static async Task FinishAsync(IServiceProvider sp, long userId, string account, string adminBefore)
    {
        await SetOtpConfigValueAsync(sp, "True");

        var otpRepo = sp.GetRequiredService<ISqlSugarRepository<SysUserOtp>>();
        var userRepo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var logRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogLogin>>();
        await otpRepo.DeletePhysicalByAsync(o => o.UserId == userId);
        await logRepo.DeletePhysicalByAsync(l => l.Account == account);
        await userRepo.DeletePhysicalByAsync(u => u.Id == userId);

        Assert.False(await otpRepo.IsAnyAsync(o => o.UserId == userId), "探针 OTP 行残留");
        Assert.False(await logRepo.IsAnyAsync(l => l.Account == account), "探针登录日志残留");
        Assert.False(await userRepo.IsAnyAsync(u => u.Id == userId), "探针用户残留");
        Assert.Equal(adminBefore, await AdminOtpSignatureAsync(sp));
    }

    /// <summary>admin 的 OTP 绑定行指纹(批 AY 教训:探针运行绝不得动真实用户绑定)。</summary>
    private static async Task<string> AdminOtpSignatureAsync(IServiceProvider sp)
    {
        var userRepo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var admin = await userRepo.GetFirstAsync(u => u.Account == "admin");
        if (admin is null)
        {
            return "no-admin";
        }

        var otpRepo = sp.GetRequiredService<ISqlSugarRepository<SysUserOtp>>();
        var rows = await otpRepo.AsQueryable()
            .Where(o => o.UserId == admin.Id)
            .OrderBy(o => o.Id)
            .ToListAsync();
        return string.Join("|", rows.Select(r => $"{r.Id}:{r.Enabled}:{r.SecretCipher}:{r.LastUsedTimeStep}:{r.BindTime}"));
    }

    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        // ㉖ 唯一身份构造点:默认未认证(登录者身份由 LoginAsync 入参决定,不读此 accessor)
        public TestActorAccessor Accessor { get; } = new();

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
            services.AddCache(config);
            services.AddSignalR();
            services.AddAutoServices(typeof(AuthService).Assembly);
            // ㉖ 放在 AddAutoServices 之后注册:单服务解析取最后一次注册(须在 AddSqlSugar 的 TryAdd 之后亦满足)
            services.AddSingleton<IHttpContextAccessor>(Accessor);
            _provider = services.BuildServiceProvider();
            Provider = _provider;
            var db = _provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(db);
            PgReachable = true;
        }

        public void Dispose() => _provider?.Dispose();

        private static string DbConnectionString =>
            TestPg.ConnectionString;

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                return client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}