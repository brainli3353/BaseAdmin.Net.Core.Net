// 批 B3a 越权收口测试补齐(真实 PG;DbScope 装置/探针清理纪律照抄 BatchSGConsistencyTest;PG 不可达时诚实降级 Assert.Fail,禁止冒充通过):
// ① 超管写守卫×2:SysUserService.ResetPwd / GrantRoleAsync 目标=AccountType=999(库中真实 admin) ⇒
//    AppException.ErrorCode=UserSuperAdminReadonly,且守卫先于任何写库抛出——
//    finally 断言 admin 行 Password 哈希(md5)/TokenVersion/密码历史行数未变 + sys_user_role 行数未变;
//    WHY ResetPwd 传新口令、GrantRoleAsync 传 RoleIds=null(全量替换语义=清空角色链):守卫一旦缺失即真实改写
//    admin 凭证/授权,负探针最能钉住"守卫先于写库"次序;两用例正常路径零写库(守卫在前抛出)。
// ② 公告详情 IDOR 收口:SysNoticeService.GetDetailAsync——超管放行;发布人(PublicUserId)/收件人(sys_notice_user)放行;
//    无关用户抛 NoticeNotFound(与不存在同码,防存在性泄露)。
// 探针纪律:finally 物理清理(失败也不漏),DisposeAsync 再兜底一次(幂等);
// WHY 原生 SQL 清理:同 BatchSGConsistencyTest 口径——不依赖全局过滤器状态,清理路径单一。
using System.Net.Sockets;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchSGSecurityDeepTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例 finally 已清,DisposeAsync 再兜底(幂等)
    private readonly List<long> _probeUserIds = new();
    private readonly List<long> _probeNoticeIds = new();

    public BatchSGSecurityDeepTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_probeUserIds.Count == 0 && _probeNoticeIds.Count == 0)
        {
            return;
        }

        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable)
        {
            return;
        }

        foreach (var noticeId in _probeNoticeIds)
        {
            await CleanupNoticeAsync(dbScope.Db, noticeId);
        }

        foreach (var userId in _probeUserIds)
        {
            await CleanupProbeAsync(dbScope.Db, userId);
        }
    }

    // 开发库连接串(与 BatchSGConsistencyTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private const int SuperAdminAccountType = 999;
    private const int NormalAccountType = 777;

    // ─────────────────────────────────────────────────────────────
    // ① 超管守卫 a):ResetPwd 目标=999 ⇒ UserSuperAdminReadonly + admin 行未动
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 重置密码_目标超管_抛UserSuperAdminReadonly且admin行未动()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var admin = await FindAdminAsync(dbScope.Db);
        var md5Baseline = admin.PwdMd5;
        var tokenVersionBaseline = admin.TokenVersion;
        var pwdRecordBaseline = await CountByUserAsync(dbScope.Db, "sys_user_password_record", admin.Id);
        var roleBaseline = await CountByUserAsync(dbScope.Db, "sys_user_role", admin.Id);

        try
        {
            // 次级管理员操作者身份(守卫只看目标 AccountType;挂普通管理员贴真实越权场景;守卫场景不落库,假 id 无副作用)
            SetUser(diScope.ServiceProvider, userId: 990000000001L, accountType: NormalAccountType, account: "sgb3a_op");

            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.ResetPwd(new ResetPwdInput(admin.Id, "SgB3a_Probe!2026")));
            Assert.Equal(ErrorCodes.UserSuperAdminReadonly, ex.ErrorCode);

            // 守卫先于写库:主断言后立即复核(守卫缺失时此处先红并保住原始异常)
            Assert.Equal(md5Baseline, await AdminPwdMd5Async(dbScope.Db, admin.Id));
            Assert.Equal(tokenVersionBaseline, await AdminTokenVersionAsync(dbScope.Db, admin.Id));
            Assert.Equal(pwdRecordBaseline, await CountByUserAsync(dbScope.Db, "sys_user_password_record", admin.Id));

            _output.WriteLine($"判定:ResetPwd 抛 ErrorCode={ErrorCodes.UserSuperAdminReadonly};admin 未动 md5={md5Baseline[..8]}.. TokenVersion={tokenVersionBaseline} 密码历史={pwdRecordBaseline} 角色行={roleBaseline}");
        }
        finally
        {
            Assert.Equal(md5Baseline, await AdminPwdMd5Async(dbScope.Db, admin.Id));
            Assert.Equal(tokenVersionBaseline, await AdminTokenVersionAsync(dbScope.Db, admin.Id));
            Assert.Equal(pwdRecordBaseline, await CountByUserAsync(dbScope.Db, "sys_user_password_record", admin.Id));
            Assert.Equal(roleBaseline, await CountByUserAsync(dbScope.Db, "sys_user_role", admin.Id));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ① 超管守卫 b):GrantRoleAsync 目标=999 ⇒ UserSuperAdminReadonly + admin 角色行未动
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 分配角色_目标超管_抛UserSuperAdminReadonly且角色行未动()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysUserService>();
        var admin = await FindAdminAsync(dbScope.Db);
        var roleBaseline = await CountByUserAsync(dbScope.Db, "sys_user_role", admin.Id);
        var tokenVersionBaseline = admin.TokenVersion;

        try
        {
            SetUser(diScope.ServiceProvider, userId: 990000000002L, accountType: NormalAccountType, account: "sgb3a_op2");

            // RoleIds=null=全量清空语义:守卫缺失 ⇒ DeletePhysicalByAsync 真清 admin 角色链,finally 行数断言必抓到
            var ex = await Assert.ThrowsAsync<AppException>(
                () => svc.GrantRoleAsync(new GrantRoleInput(admin.Id, null)));
            Assert.Equal(ErrorCodes.UserSuperAdminReadonly, ex.ErrorCode);

            Assert.Equal(roleBaseline, await CountByUserAsync(dbScope.Db, "sys_user_role", admin.Id));
            Assert.Equal(tokenVersionBaseline, await AdminTokenVersionAsync(dbScope.Db, admin.Id));

            _output.WriteLine($"判定:GrantRoleAsync 抛 ErrorCode={ErrorCodes.UserSuperAdminReadonly};admin 未动 角色行={roleBaseline} TokenVersion={tokenVersionBaseline}");
        }
        finally
        {
            Assert.Equal(roleBaseline, await CountByUserAsync(dbScope.Db, "sys_user_role", admin.Id));
            Assert.Equal(tokenVersionBaseline, await AdminTokenVersionAsync(dbScope.Db, admin.Id));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ② 公告详情 IDOR a):超管/发布人/收件人三视角放行
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 公告详情_超管发布人收件人均放行()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysNoticeService>();
        var admin = await FindAdminAsync(dbScope.Db);
        long noticeId = 0;
        long userIdA = 0;
        long userIdB = 0;
        long userIdC = 0;

        try
        {
            var (notice, userA, userB, userC) = await CreateNoticeProbesAsync(dbScope.Db, NewMarker());
            noticeId = notice.Id;
            userIdA = userA.Id;
            userIdB = userB.Id;
            userIdC = userC.Id;

            // 超管视角:无条件放行
            SetUser(diScope.ServiceProvider, admin.Id, SuperAdminAccountType, "admin");
            var byAdmin = await svc.GetDetailAsync(noticeId);
            Assert.NotNull(byAdmin);
            Assert.Equal(noticeId, byAdmin!.Id);
            Assert.Equal(userIdA, byAdmin.PublicUserId);

            // 发布人视角(PublicUserId=A):放行
            SetUser(diScope.ServiceProvider, userIdA, NormalAccountType, userA.Account);
            var byPublisher = await svc.GetDetailAsync(noticeId);
            Assert.NotNull(byPublisher);
            Assert.Equal(noticeId, byPublisher!.Id);

            // 收件人视角(sys_notice_user 行 UserId=B):放行
            SetUser(diScope.ServiceProvider, userIdB, NormalAccountType, userB.Account);
            var byReceiver = await svc.GetDetailAsync(noticeId);
            Assert.NotNull(byReceiver);
            Assert.Equal(noticeId, byReceiver!.Id);

            _output.WriteLine($"判定:超管/发布人A/收件人B 三视角 GetDetailAsync 均返回公告 {noticeId} 详情");
        }
        finally
        {
            await CleanupNoticeAsync(dbScope.Db, noticeId);
            await CleanupProbeAsync(dbScope.Db, userIdA);
            await CleanupProbeAsync(dbScope.Db, userIdB);
            await CleanupProbeAsync(dbScope.Db, userIdC);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ② 公告详情 IDOR b):无关用户(非发布人非收件人)⇒ NoticeNotFound
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 公告详情_无关用户_抛NoticeNotFound()
    {
        await using var dbScope = await DbScope.CreateAsync();
        if (!dbScope.PgReachable) { Assert.Fail(Downgrade); }

        using var diScope = dbScope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysNoticeService>();
        var admin = await FindAdminAsync(dbScope.Db);
        long noticeId = 0;
        long userIdA = 0;
        long userIdB = 0;
        long userIdC = 0;

        try
        {
            var (notice, userA, userB, userC) = await CreateNoticeProbesAsync(dbScope.Db, NewMarker());
            noticeId = notice.Id;
            userIdA = userA.Id;
            userIdB = userB.Id;
            userIdC = userC.Id;

            // 无关用户视角(C 既非发布人亦无收件行):NoticeNotFound 防存在性泄露
            SetUser(diScope.ServiceProvider, userIdC, NormalAccountType, userC.Account);
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(noticeId));
            Assert.Equal(ErrorCodes.NoticeNotFound, ex.ErrorCode);

            _output.WriteLine($"判定:无关用户C 调 GetDetailAsync({noticeId}) 抛 ErrorCode={ErrorCodes.NoticeNotFound}");
        }
        finally
        {
            await CleanupNoticeAsync(dbScope.Db, noticeId);
            await CleanupProbeAsync(dbScope.Db, userIdA);
            await CleanupProbeAsync(dbScope.Db, userIdB);
            await CleanupProbeAsync(dbScope.Db, userIdC);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 造数与清理辅助
    // ─────────────────────────────────────────────────────────────
    private static string NewMarker() => $"sgb3a{Guid.NewGuid():N}"[..14];

    /// <summary>库中真实超管 admin 行(前置自证:无行即装置不满足,明确失败)。</summary>
    private static async Task<AdminRow> FindAdminAsync(ISqlSugarClient db)
    {
        var ids = await db.Ado.SqlQueryAsync<long>(
            "SELECT id FROM sys_user WHERE \"Account\"='admin' AND \"IsDelete\"=false");
        var id = Assert.Single(ids);
        var pwdMd5 = await db.Ado.GetStringAsync($"SELECT md5(\"Password\") FROM sys_user WHERE id={id}");
        var tokenVersion = await db.Ado.GetIntAsync($"SELECT \"TokenVersion\" FROM sys_user WHERE id={id}");
        return new AdminRow(id, pwdMd5, tokenVersion);
    }

    private sealed record AdminRow(long Id, string PwdMd5, int TokenVersion);

    private static async Task<string> AdminPwdMd5Async(ISqlSugarClient db, long adminId)
        => await db.Ado.GetStringAsync($"SELECT md5(\"Password\") FROM sys_user WHERE id={adminId}");

    private static async Task<int> AdminTokenVersionAsync(ISqlSugarClient db, long adminId)
        => await db.Ado.GetIntAsync($"SELECT \"TokenVersion\" FROM sys_user WHERE id={adminId}");

    private static async Task<int> CountByUserAsync(ISqlSugarClient db, string table, long userId)
        => await db.Ado.GetIntAsync($"SELECT COUNT(*) FROM {table} WHERE \"UserId\"={userId}");

    /// <summary>
    /// 公告探针装置:3 个普通探针用户(A/B/C,AccountType=777 非 999,同租户)+1 条公告(发布人=A,草稿态)
    /// +1 行收件(收件人=B)。C 无任何关联——IDOR 拒绝视角。
    /// </summary>
    private async Task<(SysNotice Notice, SysUser UserA, SysUser UserB, SysUser UserC)> CreateNoticeProbesAsync(
        ISqlSugarClient db, string marker)
    {
        var userA = await InsertProbeUserAsync(db, marker, "a");
        var userB = await InsertProbeUserAsync(db, marker, "b");
        var userC = await InsertProbeUserAsync(db, marker, "c");

        var notice = new SysNotice
        {
            Title = $"{marker}_t",
            Content = "sgb3a 探针公告正文",
            Type = NoticeTypeEnum.Notice,
            PublicUserId = userA.Id,
            PublicUserName = userA.RealName,
            Status = NoticeStatusEnum.Draft,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        await db.Insertable(notice).ExecuteCommandAsync();
        _probeNoticeIds.Add(notice.Id);

        await db.Insertable(new SysNoticeUser { NoticeId = notice.Id, UserId = userB.Id }).ExecuteCommandAsync();

        // 前置自证:收件行确实落库(原生计数,绕过全局过滤器)
        var receiverRows = await db.Ado.GetIntAsync(
            $"SELECT COUNT(*) FROM sys_notice_user WHERE \"NoticeId\"={notice.Id} AND \"UserId\"={userB.Id}");
        Assert.Equal(1, receiverRows);

        return (notice, userA, userB, userC);
    }

    private async Task<SysUser> InsertProbeUserAsync(ISqlSugarClient db, string marker, string suffix)
    {
        var user = new SysUser
        {
            Account = $"{marker}_{suffix}",
            Password = "sgb3a_nologin",
            RealName = $"探针{suffix.ToUpperInvariant()}",
            Status = 0,
            AccountType = NormalAccountType,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        await db.Insertable(user).ExecuteCommandAsync();
        _probeUserIds.Add(user.Id);
        return user;
    }

    /// <summary>模拟登录身份(与 BatchLOrgScopeTest.SetUser 同口径:JwtClaimNames 同键,租户与探针数据同域)。</summary>
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

    /// <summary>物理清理公告探针(收件行+公告主行,原生 DELETE;幂等)。</summary>
    private static async Task CleanupNoticeAsync(ISqlSugarClient db, long noticeId)
    {
        if (noticeId == 0)
        {
            return;
        }

        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_notice_user WHERE \"NoticeId\"={noticeId}");
        await db.Ado.ExecuteCommandAsync($"DELETE FROM sys_notice WHERE id={noticeId}");
    }

    /// <summary>物理清理探针用户及关联表行(原生 DELETE;照抄 BatchSGConsistencyTest 口径,幂等)。</summary>
    private static async Task CleanupProbeAsync(ISqlSugarClient db, long userId)
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

    // ─────────────────────────────────────────────────────────────
    // 装置:真实 PG 的最小宿主(照抄 BatchSGConsistencyTest DbScope;服务经 AddAutoServices 自动发现)
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
            services.AddSignalR(); // SysUserService/SysNoticeService → SysOnlineUserService → IHubContext(测试无连接时推送 no-op,同批 AF/UX/SG 装配)
            services.AddAutoServices(typeof(SysUserService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

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
}
