using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Auth;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.SeedData;
using AdminCore.Core.Services.User;
using AdminCore.Core.SqlSugar;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
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
/// 批 CN 单点登录**服务端强吊销**测试(承接批 F 1.1 软踢之上):
/// 开关 <c>sys_single_login</c> 启用时,登录签发段先递增本用户 TokenVersion 并落库 + 删版本缓存,
/// 新 token 携递增后版本、旧 token(Claim 版本落后)下次请求即被 JwtAuthHandler 拒;缺省关=多端并存。
/// 环境:真实 PG + 探针用户(AccountType=777,不碰 admin),每次登录前解绑 OTP 走真链路;finally 物理清理。
/// PG 不可达一律 Assert.Fail(禁止降级冒充通过)。
/// </summary>
public class BatchCNSingleLoginRevokeTest : IDisposable
{
    private const string DbConnectionString =
        TestPg.ConnectionString;
    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;
    private const string Pwd = "Cn1n_Pwd!pass1";
    private const string SingleLoginCode = "sys_single_login";

    private readonly ServiceProvider _provider;
    // ㉖ 平台级配置写闸适配：写 sys_single_login 前临时挂超管身份，见 SetSingleLoginAsync
    private readonly TestActorAccessor _accessor = new();
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private readonly string _account;
    private long _userId;
    private long _cfgId;
    private string? _cfgOriginalValue; // 原值(null=本夹具新建,Dispose 物理删)

    public BatchCNSingleLoginRevokeTest()
    {
        _account = $"cn_u_{_p}";
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
                ["Jwt:Secret"] = "cncn_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "cncn-test",
                ["Jwt:Audience"] = "cncn-test-api",
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
        // 放在 AddSignalR/AddAutoServices 之后注册：单服务解析取最后一次注册，确保 accessor 落到本夹具实例
        services.AddSingleton<IHttpContextAccessor>(_accessor);
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(db);
        _pgReachable = true;

        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = _account,
            Password = Pbkdf2Helper.HashPassword(Pwd),
            RealName = $"cn_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        _userId = user.Id;
    }

    // ── C1:开关启用 → 二次登录使首 token 版本落后(服务端强吊销) ──
    [Fact]
    public async Task 开关开_二次登录_首token版本落后当前库版本()
    {
        await RunAsync(async () =>
        {
            await SetSingleLoginAsync("true");

            var r1 = await LoginAsync();
            var v1 = DecodeVersion(r1.Token!);

            var r2 = await LoginAsync();
            var v2 = DecodeVersion(r2.Token!);

            var db = await CurrentDbVersionAsync();

            Assert.True(v2 > v1, $"二次登录应递增 TokenVersion(v1={v1} 应 < v2={v2})");
            Assert.Equal(db, v2);                                  // 新 token == DB(自证有效)
            Assert.True(v1 < db, $"首 token 版本({v1})应 < 当前库版本({db})→ 旧 token 下次请求被 JwtAuthHandler 拒");
        });
    }

    // ── C3:开关启用 → 新 token / DB / 版本缓存 三者一致(新登录不自杀) ──
    [Fact]
    public async Task 开关开_新token版本_与库与缓存三者一致()
    {
        await RunAsync(async () =>
        {
            await SetSingleLoginAsync("true");

            var r = await LoginAsync();
            var v = DecodeVersion(r.Token!);
            var db = await CurrentDbVersionAsync();
            var cached = await CurrentCacheVersionAsync();

            Assert.True(v > 0);
            Assert.Equal(db, v);            // 签发的新 token 版本 == DB
            Assert.Equal(v, cached);        // 回写缓存 == 新 token 版本(JwtAuthHandler 快路径放行新 token)
        });
    }

    // ── C2:开关关闭 → 多端并存,二次登录版本不变(零行为变化) ──
    [Fact]
    public async Task 开关关_二次登录_版本不变多端并存()
    {
        await RunAsync(async () =>
        {
            await SetSingleLoginAsync("false");

            var r1 = await LoginAsync();
            var v1 = DecodeVersion(r1.Token!);

            var r2 = await LoginAsync();
            var v2 = DecodeVersion(r2.Token!);

            var db = await CurrentDbVersionAsync();

            Assert.Equal(v1, v2);          // 开关关 → 不递增
            Assert.Equal(db, v2);          // 两次 token 均 == 当前库版本(均有效,并存)
        });
    }

    // ── C4:种子行(sys_single_login 开关须在配置 UI 可发现) ──
    [Fact]
    public void 种子_sys_single_login开关行存在且缺省关false()
    {
        // 纯函数断言(不依赖 PG):批 CN 开关须有 SysConfig 种子行,否则配置管理 UI 里发现不了(运维无法便捷开启)。
        var row = new SysConfigSeedData().HasData().SingleOrDefault(c => c.Code == SysConfigService.SingleLoginEnabled);
        Assert.NotNull(row);
        Assert.Equal("false", row!.Value); // 缺省关=多端并存(与 OTP 开关缺省开相反)
        Assert.True(row.SysFlag);          // 内置禁删可改值
    }

    // ── 辅助 ──

    /// <summary>真链路登录(一阶段取挑战/secret → TOTP → 二阶段签发),每次先解绑 OTP 保证未绑态。</summary>
    private async Task<LoginResult> LoginAsync()
    {
        using (var otpScope = _provider.CreateScope())
        {
            await otpScope.ServiceProvider.GetRequiredService<SqlSugarRepository<SysUserOtp>>()
                .DeletePhysicalByAsync(o => o.UserId == _userId);
        }

        var scope = _provider.CreateScope();
        try
        {
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            var r1 = await auth.LoginAsync(_account, Pwd);
            if (!r1.Success || r1.Data is null)
            {
                throw new Exception($"一阶段失败:{r1.Error}");
            }
            var secret = r1.Data.Secret;
            if (string.IsNullOrEmpty(secret))
            {
                throw new Exception("secret missing(应已解绑)");
            }
            var code = Totp(secret, 0);
            var r2 = await auth.LoginOtpAsync(r1.Data.ChallengeId, code);
            if (!r2.Success)
            {
                throw new Exception($"二阶段失败:{r2.Error}");
            }
            return r2;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>写 sys_single_login 配置(夹具首次新建、Dispose 恢复/删);经 SysConfigService 使缓存即时失效。</summary>
    private async Task SetSingleLoginAsync(string value)
    {
        var cfgSvc = _provider.GetRequiredService<SysConfigService>();
        // ㉖ 平台级配置写闸（`SysConfigService.EnsurePlatformAdmin`）：本夹具写的是平台开关
        // `sys_single_login`，非超管会被 Forbidden 拦下 ⇒ 仅这一段临时挂超管身份，
        // 结束后立即还原未认证基线（登录链路/审计口径不受影响）。
        _accessor.SetActor(1L, UserManager.SuperAdminAccountType.ToString());
        try
        {
            if (_cfgId == 0)
            {
                var cfgRepo = _provider.GetRequiredService<SqlSugarRepository<SysConfig>>();
                var existing = await cfgRepo.GetFirstAsync(u => u.Code == SingleLoginCode);
                if (existing is not null)
                {
                    _cfgId = existing.Id;
                    _cfgOriginalValue = existing.Value;
                }
                else
                {
                    _cfgId = await cfgSvc.AddAsync(new AddConfigInput("批CN单登录", SingleLoginCode, "false", "Sys"));
                    _cfgOriginalValue = null; // 本夹具新建
                }
            }
            await cfgSvc.UpdateAsync(new UpdateConfigInput(_cfgId, Value: value));
        }
        finally
        {
            _accessor.Clear();
        }
    }

    private async Task<int> CurrentDbVersionAsync()
    {
        var scope = _provider.CreateScope();
        try
        {
            var user = await scope.ServiceProvider.GetRequiredService<SqlSugarRepository<SysUser>>()
                .GetFirstAsync(u => u.Id == _userId);
            return user!.TokenVersion;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private async Task<int> CurrentCacheVersionAsync()
    {
        var cache = _provider.GetRequiredService<ICacheService>();
        return await cache.GetAsync<int>($"{CacheConst.KeyUserToken}{_userId}");
    }

    private static int DecodeVersion(string token)
    {
        var claims = DecodeClaims(token);
        return int.Parse((string)claims["TokenVersion"]!);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!_pgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }
        await action();
    }

    /// <summary>JWT payload 解码(不验签,只读载)。</summary>
    private static Dictionary<string, object?> DecodeClaims(string token)
    {
        var payload = token.Split('.')[1];
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

    private static string Totp(string secretB32, int offset)
    {
        var a = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int bits = 0, bitCount = 0;
        foreach (var c in secretB32.ToUpper())
        {
            int v = a.IndexOf(c);
            if (v < 0) continue;
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

    public void Dispose()
    {
        try
        {
            if (!_pgReachable) return;
            var scope = _provider.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<SqlSugarRepository<SysUserPasswordRecord>>()
                .DeletePhysicalByAsync(r => r.UserId == _userId).GetAwaiter().GetResult();
            sp.GetRequiredService<SqlSugarRepository<SysUserOtp>>()
                .DeletePhysicalByAsync(o => o.UserId == _userId).GetAwaiter().GetResult();
            sp.GetRequiredService<SqlSugarRepository<SysUser>>()
                .DeletePhysicalByAsync(u => u.Id == _userId).GetAwaiter().GetResult();
            // 恢复 sys_single_login:本夹具新建则物理删,否则回填原值
            if (_cfgId != 0)
            {
                var cfgRepo = sp.GetRequiredService<SqlSugarRepository<SysConfig>>();
                var row = cfgRepo.GetFirstAsync(u => u.Id == _cfgId).GetAwaiter().GetResult();
                if (row is not null)
                {
                    if (_cfgOriginalValue is null)
                    {
                        cfgRepo.DeletePhysicalByAsync(u => u.Id == _cfgId).GetAwaiter().GetResult();
                    }
                    else
                    {
                        row.Value = _cfgOriginalValue;
                        cfgRepo.UpdateAsync(row).GetAwaiter().GetResult();
                    }
                }
            }
            scope.Dispose();
        }
        finally
        {
            _provider.Dispose();
        }
    }
}
