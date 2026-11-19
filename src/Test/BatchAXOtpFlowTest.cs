using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Auth.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 批 AX「OTP 校验三态 + pending 密钥不轮换」验证件:被测主体 =
/// ① <see cref="AuthService.LoginAsync"/> 未绑定时**复用同一 pending 密钥**(不再每次一阶段新生成);
/// ② <see cref="AuthService.LoginOtpAsync"/> 验证成功后绑定、且密钥不轮换(Enabled=true/BindTime 补齐);
/// ③ <see cref="OtpService.Validate"/> 三态(新码 Ok／同码重放 Reused／错码 Invalid)与**错误码分流**;
/// ④ 新错误码常量与中英词表齐(批 AQ 口径)。
/// </summary>
/// <remarks>
/// 真实 PG(容器与 BatchDLogTest 同款:AddSqlSugar/AddProjectOptions/AddCache/AddAutoServices/AddSignalR
/// ＋Cryptogram/Jwt 配置);PG 不可达时 Assert.Fail 降级声明。
/// 零污染:探针账号专属前缀 + 用例内物理清理(OTP 行/用户行/登录日志)。
/// </remarks>
public class BatchAXOtpFlowTest
{
    private static long _seq = 9_700_000;

    // ── ①⑤ 未绑定:两次一阶段同一密钥,且都仍要求扫码(pending 不可登录) ──────────────────────
    [Fact]
    public async Task 未绑定_两次一阶段_同一密钥且仍要求扫码()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"ax{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            var r1 = await auth.LoginAsync(account, Password);
            var r2 = await auth.LoginAsync(account, Password);

            Assert.True(r1.Success && r2.Success, "两次一阶段都应成功");
            Assert.True(r1.Data!.BindRequired && r2.Data!.BindRequired, "未绑定 ⇒ 两次都应要求扫码(pending 不算已绑定)");
            Assert.NotNull(r1.Data.Secret);
            Assert.Equal(r1.Data.Secret, r2.Data.Secret);   // 批 AX F2 核心判据:不再轮换
        }
        finally
        {
            await CleanupAsync(sp, user.Id, account);
        }
    }

    // ── ② 绑定成功:密钥不变、Enabled=true、BindTime 非空(由 pending 转正时补齐) ─────────────
    [Fact]
    public async Task 绑定成功后_密钥不变_Enabled且BindTime非空()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"ax{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            var r1 = await auth.LoginAsync(account, Password);
            var secret = r1.Data!.Secret!;
            var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
            var r2 = await auth.LoginOtpAsync(r1.Data.ChallengeId, code);
            Assert.True(r2.Success, $"二阶段应成功(错误码={r2.ErrorCode})");

            // 再走一阶段:已绑定 ⇒ 不再要求扫码
            // 注意口径:绑定成功后一阶段**按设计不再回传密钥**(Secret=null,避免把已绑定密钥交给客户端),
            // 故"密钥未轮换"的判据落在**库内密文解密结果**＝首次 pending 密钥(见下),而不是响应字段。
            var r3 = await auth.LoginAsync(account, Password);
            Assert.False(r3.Data!.BindRequired);
            Assert.Null(r3.Data.Secret);

            // 库内状态:Enabled=true / BindTime 非空(由 pending 转正时补齐) / 密文解出＝原密钥(未轮换)
            var otpRepo = sp.GetRequiredService<ISqlSugarRepository<SysUserOtp>>();
            var row = await otpRepo.GetFirstAsync(o => o.UserId == user.Id);
            Assert.NotNull(row);
            Assert.True(row!.Enabled);
            Assert.NotNull(row.BindTime);          // 批 AX F2:pending 行转正时补绑定时刻
            Assert.NotNull(row.LastUsedTimeStep);
            Assert.Equal(secret, sp.GetRequiredService<CryptogramHelper>().Decrypt(row.SecretCipher!));
        }
        finally
        {
            await CleanupAsync(sp, user.Id, account);
        }
    }

    // ── ③ 三态纯函数(无 DB):新码 Ok／同码重放 Reused／错码 Invalid ──────────────────────────
    [Fact]
    public void 校验三态_新码Ok_同码Reused_错码Invalid()
    {
        var otp = new OtpService();
        var secret = otp.GenerateSetupSecret();
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var first = otp.Validate(code, secret, null);
        Assert.True(first.IsValid);
        Assert.NotNull(first.MatchedStep);

        // 同一码 + 已用基线 ⇒ 重放(不是"错码")
        var replay = otp.Validate(code, secret, first.MatchedStep);
        Assert.False(replay.IsValid);
        Assert.True(replay.IsReused);

        // 密钥不匹配的码 ⇒ 错码(与重放区分)
        var otherSecret = otp.GenerateSetupSecret();
        var otherCode = new Totp(Base32Encoding.ToBytes(otherSecret)).ComputeTotp();
        var wrong = otp.Validate(otherCode, secret, null);
        Assert.False(wrong.IsValid);
        Assert.False(wrong.IsReused);

        Assert.False(otp.Validate("", secret, null).IsValid);
        Assert.False(otp.Validate(code, "NOT-BASE32!!", null).IsValid);
    }

    // ── ④ 错误码分流:同一码二次提交 ⇒ auth.otp_reused;错码 ⇒ auth.otp_verify_failed ────────
    [Fact]
    public async Task 错误码分流_重放回otp_reused_错码回otp_verify_failed()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var sp = unit.ServiceProvider;
        var account = $"ax{Interlocked.Increment(ref _seq)}";
        var user = await InsertUserAsync(sp, account);
        var auth = sp.GetRequiredService<AuthService>();

        try
        {
            var r1 = await auth.LoginAsync(account, Password);
            var secret = r1.Data!.Secret!;
            var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
            Assert.True((await auth.LoginOtpAsync(r1.Data.ChallengeId, code)).Success);

            // 新挑战 + **同一个码** ⇒ 命中防重放 ⇒ 新错误码(修前只会回笼统"校验码验证失败")
            var r3 = await auth.LoginAsync(account, Password);
            var replay = await auth.LoginOtpAsync(r3.Data!.ChallengeId, code);
            Assert.False(replay.Success);
            Assert.Equal(ErrorCodes.AuthOtpReused, replay.ErrorCode);

            // 错码(用另一密钥算出的码) ⇒ 维持既有错误码
            var r4 = await auth.LoginAsync(account, Password);
            var otherCode = new Totp(Base32Encoding.ToBytes(new OtpService().GenerateSetupSecret())).ComputeTotp();
            var wrong = await auth.LoginOtpAsync(r4.Data!.ChallengeId, otherCode);
            Assert.False(wrong.Success);
            Assert.Equal(ErrorCodes.AuthOtpVerifyFailed, wrong.ErrorCode);
        }
        finally
        {
            await CleanupAsync(sp, user.Id, account);
        }
    }

    // ── ⑥ 常量与中英词表齐(新错误码必须三处同步:常量/资源/defaults) ─────────────────────────
    [Fact]
    public void 新错误码_常量与中英词表齐()
    {
        Assert.Equal("auth.otp_reused", ErrorCodes.AuthOtpReused);

        var zh = ReadResource("Resources/Lang.zh-CN.json");
        var en = ReadResource("Resources/Lang.en.json");
        Assert.Contains("auth.otp_reused", zh);
        Assert.Contains("auth.otp_reused", en);
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    private const string Password = "P@ssw0rd!AX";

    private static string ReadResource(string relative)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relative));

    private static async Task<SysUser> InsertUserAsync(IServiceProvider sp, string account)
    {
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = account,
            Password = Pbkdf2Helper.HashPassword(Password),
            RealName = "AX 探针",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        await repo.InsertAsync(user);
        return user;
    }

    // 零污染清理:OTP 行 / 登录日志 / 用户行(物理删,只针对本用例探针账号)
    private static async Task CleanupAsync(IServiceProvider sp, long userId, string account)
    {
        var otpRepo = sp.GetRequiredService<ISqlSugarRepository<SysUserOtp>>();
        var userRepo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var loginLogRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogLogin>>();

        await otpRepo.DeletePhysicalByAsync(o => o.UserId == userId);
        await loginLogRepo.DeletePhysicalByAsync(l => l.Account == account);
        await userRepo.DeletePhysicalByAsync(u => u.Id == userId);
    }

    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

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
            services.AddCache(config);
            services.AddSignalR();
            services.AddAutoServices(typeof(AuthService).Assembly);
            _provider = services.BuildServiceProvider();
            Provider = _provider;   // 用例侧经 Provider.CreateScope 取服务(漏赋值会得到 null ⇒ ArgumentNullException)
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