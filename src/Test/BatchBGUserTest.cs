using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using AdminCore.Web.Entry.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 BG(user 用户管理 menu 逐页复审)测试:
/// 分配角色对话框回显端点 `GET /api/sysUser/roleIds`——GrantRole 全量替换口径(空选=清空)下
/// 无回显=管理员无从得知用户现挂角色,直接保存即静默清空全部授权(误清缺陷)。
/// 环境:真实 PG;探针用户+探针角色 finally 物理清理;用例4 为纯反射守卫(PG 不可达仍跑)。
/// </summary>
public class BatchBGUserTest : IDisposable
{
    // 开发库连接串(与批 B/L/M 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private readonly ServiceProvider _provider;
    private readonly ISqlSugarClient _db;
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private long _userId;
    private long _roleA;
    private long _roleB;

    public BatchBGUserTest()
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

        // 探针用户(无角色起步)＋两个探针角色
        var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var user = new SysUser
        {
            Account = $"t155_u_{_p}",
            // 存量 Password 列 NOT NULL(CodeFirst string 默认,使用指南坑 12)
            Password = "t155_nologin",
            RealName = $"t155_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        _userId = user.Id;
        // Code 必须带 _p(每个测试方法独立实例):S-G B2b 起 sys_role.Code 有库级部分唯一索引,
        // 固定 Code 会被同轮其它方法/上轮中断残留撞 23505(2026-10-26 全量回归实锤)
        _roleA = InsertProbeRole(sp, "t155_roleA_" + _p, "t155RA_" + _p);
        _roleB = InsertProbeRole(sp, "t155_roleB_" + _p, "t155RB_" + _p);
        scope.Dispose();
    }

    private static long InsertProbeRole(IServiceProvider sp, string name, string code)
    {
        var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
        var role = new SysRole { Name = name, Code = code, TenantId = SqlSugarConst.DefaultTenantId };
        roleRepo.InsertAsync(role).GetAwaiter().GetResult();
        return role.Id;
    }

    private SysUserService UserService()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SysUserService>();
    }

    [Fact]
    public void 回读_无角色用户_空集()
    {
        RunAsync(async () =>
        {
            var ids = await UserService().GetUserRoleIdsAsync(_userId);
            Assert.Empty(ids);
        });
    }

    [Fact]
    public void 回读_分配后回显授予集_空授予清空()
    {
        RunAsync(async () =>
        {
            var svc = UserService();
            await svc.GrantRoleAsync(new GrantRoleInput(_userId, [_roleA, _roleB]));
            var afterGrant = await svc.GetUserRoleIdsAsync(_userId);
            Assert.Equal(2, afterGrant.Count);
            Assert.Contains(_roleA, afterGrant);
            Assert.Contains(_roleB, afterGrant);

            // 既有语义守卫:空授予=清空(全量替换口径不回退)
            await svc.GrantRoleAsync(new GrantRoleInput(_userId, []));
            var afterClear = await svc.GetUserRoleIdsAsync(_userId);
            Assert.Empty(afterClear);
        });
    }

    [Fact]
    public void 回读_用户不存在_UserNotFound()
    {
        RunAsync(async () =>
        {
            var ex = Assert.Throws<AppException>(() =>
                UserService().GetUserRoleIdsAsync(long.MaxValue).GetAwaiter().GetResult());
            Assert.Equal(ErrorCodes.UserNotFound, ex.ErrorCode);
        });
    }

    [Fact]
    public void 端点_MenuPerm_复用grantRole按钮键()
    {
        // 静态守卫:防日后误摘 [MenuPerm] 让回读端点滑回默认放行(未授按钮者可见全量角色分配)
        var action = typeof(SysUserController).GetMethod(nameof(SysUserController.RoleIds));
        Assert.NotNull(action);
        var attr = action!.GetCustomAttribute<MenuPermAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("sysUser/grantRole", attr!.Permission);
    }

    private void RunAsync(Func<Task> action)
    {
        if (!_pgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        action().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (_pgReachable)
            {
                var diScope = _provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
                var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
                var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
                userRoleRepo.DeletePhysicalByAsync(u => u.UserId == _userId).GetAwaiter().GetResult();
                roleRepo.DeletePhysicalByAsync(r => r.Id == _roleA || r.Id == _roleB).GetAwaiter().GetResult();
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
