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
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 M(1.11 密码有效期)测试(参照 VerifyPwdExpirationTime 口径,见 g7):
/// 配置关闭恒 false/null 未改过=到期/刚改密=未到期/超期=到期/UpdatePassword 落 Now/ResetPwd 落 null。
/// 环境:真实 PG;探针用户(PBKDF2 真实密码,改密/重置全链路走通)finally 清理。
/// </summary>
public class BatchMPwdExpirationTest : IDisposable
{
    // 开发库连接串(与批 B/L 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private readonly ServiceProvider _provider;
    private readonly ISqlSugarClient _db;
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private long _userId;

    private const string OldPwd = "T13m_Old!pass1";
    private const string NewPwd = "T13m_New!pass2";
    private const string ResetPwdStr = "T13m_Rst!pass3";

    public BatchMPwdExpirationTest()
    {
        if (!IsPostgresReachable())
        {
            _provider = new ServiceCollection().BuildServiceProvider();
            _db = null!;
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
        services.AddSingleton<IConfiguration>(config);
        services.AddSqlSugar(config);
        services.AddProjectOptions(config);
        services.AddCache(config);
        services.AddSignalR();
        services.AddAutoServices(typeof(SysUserService).Assembly);
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(_db);
        _pgReachable = true;

        // 探针用户(真实 PBKDF2 密码,改密/重置全链路可走)
        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = $"t13m_u_{_p}",
            // 存量 Password 列 NOT NULL(CodeFirst string 默认,使用指南坑 12)
            Password = Pbkdf2Helper.HashPassword(OldPwd),
            RealName = $"t13m_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        _userId = user.Id;
    }

    private SysUserService UserService()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SysUserService>();
    }

    [Fact]
    public void 配置关闭_恒false()
    {
        RunAsync(async () =>
        {
            var expired = await UserService().IsPasswordExpiredAsync(_userId);
            Assert.False(expired, "ExpirationDays 缺省 0(关闭) → 恒 false");
        });
    }

    [Fact]
    public void 启用_未改过密码null_到期()
    {
        RunAsync(async () =>
        {
            SetExpirationDays(30);
            var expired = await UserService().IsPasswordExpiredAsync(_userId);
            Assert.True(expired, "LastChangePasswordTime=null(存量/重置后) → 到期");
        });
    }

    [Fact]
    public void 启用_刚改密_未到期()
    {
        RunAsync(async () =>
        {
            SetExpirationDays(30);
            BackfillChangeTime(DateTime.Now);
            var expired = await UserService().IsPasswordExpiredAsync(_userId);
            Assert.False(expired, "刚改密(Now) → 未到期");
        });
    }

    [Fact]
    public void 启用_超期_到期()
    {
        RunAsync(async () =>
        {
            SetExpirationDays(30);
            BackfillChangeTime(DateTime.Now.AddDays(-31));
            var expired = await UserService().IsPasswordExpiredAsync(_userId);
            Assert.True(expired, "最后改密 31 天前(>30 天) → 到期");
        });
    }

    [Fact]
    public async Task 改密_最后改密时间落库Now()
    {
        RunAsync(async () =>
        {
            var svc = UserService();
            var before = DateTime.Now;
            await svc.UpdatePassword(_userId, new UpdatePasswordInput(OldPwd, NewPwd));
            var after = DateTime.Now;

            var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
            var user = await userRepo.GetFirstAsync(u => u.Id == _userId);
            Assert.NotNull(user!.LastChangePasswordTime);
            Assert.InRange(user.LastChangePasswordTime.Value, before.AddSeconds(-2), after.AddSeconds(2));
        });
    }

    [Fact]
    public async Task 重置_最后改密时间落库null()
    {
        RunAsync(async () =>
        {
            var svc = UserService();
            await svc.ResetPwd(new ResetPwdInput(_userId, ResetPwdStr));
            var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
            var user = await userRepo.GetFirstAsync(u => u.Id == _userId);
            Assert.True(user!.LastChangePasswordTime is null, "重置后 = null(用户未自证新密码,启用有效期时必触发到期改密)");
        });
    }

    /// <summary>改配置键后实跑(同容器 IConfiguration 直接 Set——批内配置探针,不重启容器)。</summary>
    private void SetExpirationDays(int days)
    {
        _provider.GetRequiredService<IConfiguration>()["SysPassword:ExpirationDays"] = days.ToString();
    }

    private void BackfillChangeTime(DateTime t)
    {
        _db.Updateable<SysUser>().SetColumns(u => u.LastChangePasswordTime == t).Where(u => u.Id == _userId).ExecuteCommandAsync().GetAwaiter().GetResult();
    }

    private void RunAsync(Func<Task> action)
    {
        if (!_pgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        try
        {
            action().GetAwaiter().GetResult();
        }
        finally
        {
            // 配置键还原(防串用例)
            try
            {
                _provider.GetRequiredService<IConfiguration>()["SysPassword:ExpirationDays"] = null;
            }
            catch
            {
                // 忽略
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_pgReachable)
            {
                var diScope = _provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
                var recordRepo = sp.GetRequiredService<SqlSugarRepository<SysUserPasswordRecord>>();
                recordRepo.DeletePhysicalByAsync(r => r.UserId == _userId).GetAwaiter().GetResult();
                userRepo.DeletePhysicalByAsync(u => u.Id == _userId).GetAwaiter().GetResult();
                diScope.Dispose();
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
