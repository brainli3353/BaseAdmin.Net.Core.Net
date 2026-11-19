using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Text;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 N(6.4 JWT 续期/refresh token)测试(参照 SysAuthService 双 JWT 口径,见 g8):
/// 双签非空且不同/claims 一致(含 TokenVersion)/refresh 时效&gt;access 时效/配置时效生效/改密后 refresh 携旧版本(强制失效覆盖)。
/// 环境:真实 PG;探针用户 finally 清理。JWT 比对为 payload 解码(不验签,只读载)。
/// </summary>
public class BatchNRefreshTokenTest : IDisposable
{
    // 开发库连接串(与批 B/L/M 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private readonly ServiceProvider _provider;
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private long _userId;

    private const string OldPwd = "T13n_Old!pass1";
    private const string NewPwd = "T13n_New!pass2";

    public BatchNRefreshTokenTest()
    {
        if (!IsPostgresReachable())
        {
            _provider = new ServiceCollection().BuildServiceProvider();
            return;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "t13n_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "t13n-test",
                ["Jwt:Audience"] = "t13n-test-api",
                ["Cryptogram:Key"] = "dDEzbl90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM="
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSqlSugar(config);
        services.AddProjectOptions(config);
        services.AddCache(config);
        services.AddSignalR();
        services.AddAutoServices(typeof(SysUserService).Assembly);
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(db);
        _pgReachable = true;

        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = $"t13n_u_{_p}",
            // 存量 Password 列 NOT NULL(CodeFirst string 默认,使用指南坑 12)
            Password = Pbkdf2Helper.HashPassword(OldPwd),
            RealName = $"t13n_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        _userId = user.Id;
    }

    /// <summary>登录二阶段取双 token(绕过 OTP:直接调签发路径不可达——走 LoginOtpAsync 需要挑战;
    /// 故此处直接反射调 GenerateRefreshToken? 不:改为经公共面——LoginOtpAsync 需要 OTP 全链路。
    /// 实际策略:测试 GenerateJwtToken/GenerateRefreshToken 为 private → 用 LoginResult 公共面:
    /// 构造 challenge 缓存 + OTP 绑定走真链路(与 e2e 同手法,测试内 TOTP)。
    /// </summary>
    private async Task<LoginResult> LoginAsync(string password)
    {
        var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var auth = sp.GetRequiredService<AuthService>();

        // 一阶段:生成挑战(需 OTP 绑定 → 先绑)
        var r1 = await auth.LoginAsync($"t13n_u_{_p}", password);
        if (!r1.Success || r1.Data is null)
        {
            scope.Dispose();
            throw new Exception($"一阶段失败:{r1.Error}");
        }

        // TOTP 计算(与 e2e 同口径)
        var secret = r1.Data.Secret;
        string code = null!;
        if (secret is not null)
        {
            code = Totp(secret, 0);
        }
        else
        {
            // 已绑定:从 DB 解 SetupSecret 不可得(挑战内新生成)——删绑定重走
            throw new Exception("secret missing:需先删绑定(测试夹具已保证未绑定)");
        }

        var r2 = await auth.LoginOtpAsync(r1.Data.ChallengeId, code);
        scope.Dispose();
        if (!r2.Success)
        {
            throw new Exception($"二阶段失败:{r2.Error}");
        }
        return r2;
    }

    private string _userIdStr() => _userId.ToString();

    [Fact]
    public async Task 双签_非空且不同()
    {
        var result = await RunAsync(async () => await LoginAsync(OldPwd));
        Assert.False(string.IsNullOrEmpty(result.Token), "access 非空");
        Assert.False(string.IsNullOrEmpty(result.RefreshToken), "refresh 非空");
        Assert.True(result.Token != result.RefreshToken, "双 token 不同");
    }

    [Fact]
    public async Task 双token_claims一致()
    {
        var result = await RunAsync(async () => await LoginAsync(OldPwd));
        var a = DecodeClaims(result.Token!);
        var r = DecodeClaims(result.RefreshToken!);
        Assert.True((string)a["UserId"]! == (string)r["UserId"]!, "UserId 一致");
        Assert.True((string)a["Account"]! == (string)r["Account"]!, "Account 一致");
        Assert.True((string)a["AccountType"]! == (string)r["AccountType"]!, "AccountType 一致");
        Assert.True((string)a["TokenVersion"]! == (string)r["TokenVersion"]!, "TokenVersion 一致");
        Assert.True((string)a["TenantId"]! == (string)r["TenantId"]!, "TenantId 一致");
    }

    [Fact]
    public async Task refresh时效_大于access时效()
    {
        var result = await RunAsync(async () => await LoginAsync(OldPwd));
        var aExp = (decimal)DecodeClaims(result.Token!)["exp"]!;
        var rExp = (decimal)DecodeClaims(result.RefreshToken!)["exp"]!;
        Assert.True(rExp > aExp, $"refresh exp({rExp}) 应 > access exp({aExp})");
        var diffHours = (DateTimeOffset.FromUnixTimeSeconds((long)rExp).UtcDateTime - DateTimeOffset.FromUnixTimeSeconds((long)aExp).UtcDateTime).TotalHours;
        Assert.True(diffHours > 20, $"缺省 refresh(7d) 与 access(8h) 差应 >20h,实际 {diffHours:F1}h");
    }

    [Fact]
    public async Task 配置时效_24h生效()
    {
        var result = await RunAsync(async () =>
        {
            _provider.GetRequiredService<IConfiguration>()["Jwt:RefreshTokenExpireHours"] = "24";
            try
            {
                return await LoginAsync(OldPwd);
            }
            finally
            {
                _provider.GetRequiredService<IConfiguration>()["Jwt:RefreshTokenExpireHours"] = null;
            }
        });
        var aExp = DateTimeOffset.FromUnixTimeSeconds((long)(decimal)DecodeClaims(result.Token!)["exp"]!).UtcDateTime;
        var rExp = DateTimeOffset.FromUnixTimeSeconds((long)(decimal)DecodeClaims(result.RefreshToken!)["exp"]!).UtcDateTime;
        var diff = rExp - aExp;
        Assert.True(diff > TimeSpan.FromHours(15) && diff < TimeSpan.FromHours(17), $"24h-8h=16h,实际 {diff.TotalHours:F1}h");
    }

    [Fact]
    public async Task 改密后_refresh携旧TokenVersion_强制失效覆盖()
    {
        var result = await RunAsync(async () =>
        {
            var r = await LoginAsync(OldPwd);
            // 改密 → TokenVersion++
            var scope = _provider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SysUserService>();
            await svc.UpdatePassword(_userId, new UpdatePasswordInput(OldPwd, NewPwd));
            scope.Dispose();
            return r;
        });
        var rVersion = (string)DecodeClaims(result.RefreshToken!)["TokenVersion"]!;
        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = await userRepo.GetFirstAsync(u => u.Id == _userId);
        Assert.True(user!.TokenVersion > 0);
        Assert.True(user.TokenVersion.ToString() != rVersion, "refresh 携带旧 TokenVersion → JwtAuthHandler 回查比对即拒(强制失效覆盖 refresh)");
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (!_pgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        return await action();
    }

    /// <summary>JWT payload 解码(不验签,只读载)。返回 Claim 字典。</summary>
    private static Dictionary<string, object?> DecodeClaims(string token)
    {
        var parts = token.Split('.');
        var payload = parts[1];
        if (payload.Length % 4 != 0)
        {
            payload += new string('=', 4 - payload.Length % 4);
        }
        var json = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var el in doc.RootElement.EnumerateObject())
        {
            dict[el.Name] = el.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? el.Value.GetDecimal()
                : el.Value.GetString();
        }
        return dict;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t);
    }

    private static string Totp(string secretB32, int offset = 0)
    {
        var A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int bits = 0;
        int bitCount = 0;
        foreach (var c in secretB32.ToUpper())
        {
            int v = A.IndexOf(c);
            if (v < 0)
            {
                continue;
            }
            bits = (bits << 5) | v;
            bitCount += 5;
            while (bitCount >= 8)
            {
                key.Add((byte)((bits >> (bitCount - 8)) & 0xFF));
                bitCount -= 8;
            }
        }
        var t = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset);
        var buf = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            buf[i] = (byte)(t & 0xFF);
            t >>= 8;
        }
        using var hmac = new System.Security.Cryptography.HMACSHA1(key.ToArray());
        var h = hmac.ComputeHash(buf);
        int o = h[19] & 15;
        int code = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (code % 1_000_000).ToString("D6");
    }

    public void Dispose()
    {
        try
        {
            if (_pgReachable)
            {
                var scope = _provider.CreateScope();
                var sp = scope.ServiceProvider;
                var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
                var recordRepo = sp.GetRequiredService<SqlSugarRepository<SysUserPasswordRecord>>();
                var otpRepo = sp.GetRequiredService<SqlSugarRepository<SysUserOtp>>();
                recordRepo.DeletePhysicalByAsync(r => r.UserId == _userId).GetAwaiter().GetResult();
                otpRepo.DeletePhysicalByAsync(o => o.UserId == _userId).GetAwaiter().GetResult();
                userRepo.DeletePhysicalByAsync(u => u.Id == _userId).GetAwaiter().GetResult();
                scope.Dispose();
            }
        }
        finally
        {
            _provider.Dispose();
        }
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
}
