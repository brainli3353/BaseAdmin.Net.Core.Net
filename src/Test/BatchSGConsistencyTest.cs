// 批 S-G 数据一致性止血 B1b(真实 PG,DbScope 样板照抄 BatchUXDateRangeTest;PG 不可达时诚实降级 Assert.Fail):
// ① 删用户级联补全:SysUserService.DeleteAsync 原先只物理清 sys_user_role,sys_user_otp(OTP 绑定含 AES 密文)
//    与 sys_user_password_record(密码历史)残留——探针用户+三张关联表各一行,调 DeleteAsync 后断言
//    三张关联表按该 UserId 计数均 0、sys_user 主记录软删 IsDelete=true;
// ② 零库静态守卫:SysMenuController.Delete(菜单软删+sys_role_menu 物理删两次写)带 [UnitOfWork]。
// 探针纪律:finally 物理清理(失败也不漏),DisposeAsync 再兜底一次(幂等);
// WHY 原生 SQL 清理:主记录已被软删(IsDelete=true)后对仓储 Deleteable 不可见(全局过滤器,
// 批 BH RoleTest 实测口径),关联表/角色行一并走原生 DELETE 保持清理不依赖过滤器状态。
using System.Net.Sockets;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.User;
using AdminCore.Core.SqlSugar;
using AdminCore.Web.Entry.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchSGConsistencyTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例 finally 已清,DisposeAsync 再兜底(幂等)
    private readonly List<long> _probeUserIds = new();
    private readonly List<long> _probeRoleIds = new();

    public BatchSGConsistencyTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_probeUserIds.Count == 0 && _probeRoleIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        foreach (var userId in _probeUserIds)
        {
            await CleanupProbeAsync(dbScope.Db, userId);
        }

        foreach (var roleId in _probeRoleIds)
        {
            await dbScope.Db.Ado.ExecuteCommandAsync($"DELETE FROM sys_role WHERE id={roleId}");
        }
    }

    // 开发库连接串(与 BatchUXDateRangeTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // ① 删用户级联:三张关联表物理清 + 主记录软删
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 删用户_级联物理清角色与OTP与密码历史_主记录软删()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var marker = NewMarker();
        var userId = 0L;
        var roleId = 0L;

        try
        {
            // 探针用户(AccountType=777 非 999 可删;Password 列 NOT NULL 存量口径,同 BatchBGUserTest)
            var user = new SysUser
            {
                Account = $"{marker}_u",
                Password = "sgb1b_nologin",
                RealName = marker,
                Status = 0,
                AccountType = 777,
                TenantId = SqlSugarConst.DefaultTenantId
            };
            await dbScope.Db.Insertable(user).ExecuteCommandAsync();
            userId = user.Id;
            _probeUserIds.Add(userId);

            // 探针角色 + 三张关联表各一行(otp 密文占位串,不涉及真实绑定)
            var role = new SysRole { Name = $"{marker}_r", Code = $"SG{marker}", TenantId = SqlSugarConst.DefaultTenantId };
            await dbScope.Db.Insertable(role).ExecuteCommandAsync();
            roleId = role.Id;
            _probeRoleIds.Add(roleId);

            await dbScope.Db.Insertable(new SysUserRole { UserId = userId, RoleId = roleId }).ExecuteCommandAsync();
            await dbScope.Db.Insertable(new SysUserOtp
            {
                UserId = userId,
                SecretCipher = $"sgb1b-probe-{marker}",
                Enabled = true,
                BindTime = DateTime.Now
            }).ExecuteCommandAsync();
            await dbScope.Db.Insertable(new SysUserPasswordRecord
            {
                UserId = userId,
                PasswordMd5 = "sgb1bprobe0000000000000000000000",
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();

            // 前置自证:三行确实落库(计数按 UserId,原生 SQL 绕过全局过滤器)
            Assert.Equal(1, await CountRawAsync(dbScope.Db, "sys_user_role", userId));
            Assert.Equal(1, await CountRawAsync(dbScope.Db, "sys_user_otp", userId));
            Assert.Equal(1, await CountRawAsync(dbScope.Db, "sys_user_password_record", userId));

            // 被测:删除用户(currentUserId 传 0 ≠ 雪花 Id,本人禁删判定通过)
            await svc.DeleteAsync(userId, 0);

            // 断言:三张关联表按该 UserId 计数均 0(级联物理清;若回退为只清 user_role 此处失败)
            Assert.Equal(0, await CountRawAsync(dbScope.Db, "sys_user_role", userId));
            Assert.Equal(0, await CountRawAsync(dbScope.Db, "sys_user_otp", userId));
            Assert.Equal(0, await CountRawAsync(dbScope.Db, "sys_user_password_record", userId));

            // 主记录软删而非物理删:物理行仍在且 IsDelete=true(原生 SQL,软删行被全局过滤器滤出可查询集)
            var softDeleted = await dbScope.Db.Ado.GetIntAsync(
                $"SELECT COUNT(*) FROM sys_user WHERE id={userId} AND \"IsDelete\"=true");
            Assert.Equal(1, softDeleted);

            _output.WriteLine("判定:DeleteAsync 后 sys_user_role/sys_user_otp/sys_user_password_record 按 UserId 计数均 0,sys_user 行 IsDelete=true");
        }
        finally
        {
            await CleanupProbeAsync(dbScope.Db, userId);
            if (roleId != 0)
            {
                await dbScope.Db.Ado.ExecuteCommandAsync($"DELETE FROM sys_role WHERE id={roleId}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ② 零库静态守卫:菜单删除端点带 [UnitOfWork](软删+角色授权物理删两次写须同事务)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void 菜单删除端点_带UnitOfWork特性()
    {
        var action = typeof(SysMenuController).GetMethod(nameof(SysMenuController.Delete));
        Assert.NotNull(action);
        Assert.NotNull(action!.GetCustomAttribute<UnitOfWorkAttribute>());
    }

    private static string NewMarker() => $"sgb1b{Guid.NewGuid():N}"[..14];

    /// <summary>按 UserId 计数(原生 SQL;PG 列名 Pascal 须带引号)。</summary>
    private static async Task<int> CountRawAsync(ISqlSugarClient db, string table, long userId)
        => await db.Ado.GetIntAsync($"SELECT COUNT(*) FROM {table} WHERE \"UserId\"={userId}");

    /// <summary>
    /// 物理清理探针用户及三张关联表行(原生 DELETE;幂等:行已删均无副作用)。
    /// WHY 原生 SQL:主记录可能已被软删(IsDelete=true),仓储 Deleteable 全局过滤器看不见它——
    /// 关联表行虽可被 Deleteable 清,统一走原生 DELETE 保持清理路径单一、不依赖过滤器状态。
    /// </summary>
    private static async Task CleanupProbeAsync(ISqlSugarClient db, long userId)
    {
        if (userId == 0)
        {
            return;
        }

        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_otp WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_password_record WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user_role WHERE \"UserId\"={userId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_user WHERE id={userId}");
    }

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG 的最小宿主(照抄 BatchUXDateRangeTest DbScope;SysUserService 经 AddAutoServices 自动发现)
    // ─────────────────────────────────────────────────────────────
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;

        public ServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                scope.Provider = new ServiceCollection().BuildServiceProvider();
                return scope;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR(); // SysUserService → SysOnlineUserService → IHubContext(测试无连接时推送 no-op,同批 AF/UX 装配)
            services.AddAutoServices(typeof(SysUserService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchUXDateRangeTest 口径)。</summary>
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
}
