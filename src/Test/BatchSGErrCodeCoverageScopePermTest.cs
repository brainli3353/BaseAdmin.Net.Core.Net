// 批 S-G 纵深期·错误码覆盖补漏小片（B 域·通知归属 1 码 ＋ 角色机构 2 码 ＋ 字段权限开关 2 码）。
// 覆盖：notice.creator_only / role.org_not_found / role.operator_unresolved / role.org_out_of_scope /
//       field_scope.table_required / field_scope.table_not_configurable。
// 抛点实读：`SysNoticeService.cs:304`（经公开入口 `DeleteAsync`→私有 `RequireOwnerAsync`）、
//   `SysRoleService.cs:387`（机构悬挂引用）与 `:402`（非超管且操作者不可解析）、
//   `SysRoleTableService.cs:404`（表名空）与 `:411`（表名不在实体元数据可配置集）。
// 身份分支的**顺序**利用了实读结论：`GrantOrgAsync` 先做机构存在校验（未认证也能触发 org_not_found），
//   再判 `!IsSuperAdmin`——**未认证**时 `IsSuperAdmin=false` 且 `UserId=null` ⇒ 正落进 `operator_unresolved`，
//   所以这条码用匿名身份反而最短路径。
// `role.org_out_of_scope` 的前置是"操作者的机构范围集"——**不必插 sys_user_org**：实读 `SysOrgService.cs:395-404`
//   可知**库里不存在的用户**＝无角色 ⇒ `maxScope=Self` ＋ 空机构集 ⇒ 授权任何机构都算越界。
//   用不存在 id 造该身份比插关联表更干净（不依赖既有数据、不污染真用户的范围缓存）。
// 纪律：真实 PG；探针 finally 物理删；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageScopePermTest : IAsyncLifetime
{
    private readonly List<long> _noticeIds = new();
    private readonly List<long> _roleIds = new();
    private readonly List<long> _orgIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_noticeIds.Count + _roleIds.Count + _orgIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync(null);
        if (!scope.PgReachable)
        {
            return;
        }

        if (_noticeIds.Count > 0)
        {
            await scope.Db.Deleteable<SysNotice>().In(_noticeIds.ToArray()).ExecuteCommandAsync();
        }

        if (_roleIds.Count > 0)
        {
            await scope.Db.Deleteable<SysRole>().In(_roleIds.ToArray()).ExecuteCommandAsync();
        }

        if (_orgIds.Count > 0)
        {
            await scope.Db.Deleteable<SysOrg>().In(_orgIds.ToArray()).ExecuteCommandAsync();
        }
    }

    [Fact]
    public async Task 通知归属_非发布人越权_精确断言()
    {
        // 已认证非超管 UserId=2（`IsSuperAdmin ⇐ AccountType==999`），而探针的创建者改写成 1 ⇒ 必落越权分支
        await using var scope = await DbScope.CreateAsync(new AuthenticatedAccessor(2L, "1"));
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysNoticeService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysNotice>>();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var probe = new SysNotice
        {
            Title = $"sgx_notice_{tag}",
            Content = "探针正文",
            Type = NoticeTypeEnum.Notice,
            Status = NoticeStatusEnum.Draft,
            PublicUserName = string.Empty,
            PublicOrgName = string.Empty
        };
        await repo.InsertAsync(probe);
        _noticeIds.Add(probe.Id);
        // 创建者改写成"别人"（插入时审计会写当前操作者 2 ⇒ 会等于本人而放行）；列名 Pascal 带引号、主键小写 id
        await scope.Db.Ado.ExecuteCommandAsync($"""update sys_notice set "CreateUserId"=1 where id={probe.Id}""");

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(probe.Id));
        Assert.Equal(ErrorCodes.NoticeCreatorOnly, ex.ErrorCode);

        // 反向哨兵：把创建者改回当前操作者后越权必须消失（证明因由＝非发布人，而非 Delete 必抛）。
        // 放行后会继续走"已发布不可删"之类的状态校验 ⇒ 只断言**不再是 creator_only**。
        await scope.Db.Ado.ExecuteCommandAsync($"""update sys_notice set "CreateUserId"=2 where id={probe.Id}""");
        var ex2 = await Record.ExceptionAsync(() => svc.DeleteAsync(probe.Id));
        Assert.True(ex2 is not AppException app || app.ErrorCode != ErrorCodes.NoticeCreatorOnly,
            "改成发布人本人后仍报 creator_only ⇒ 归属校验未消费 UserId（不该发生）");
    }

    [Fact]
    public async Task 角色机构授权与字段权限开关_四条校验码_精确断言()
    {
        // 本用例**用匿名身份**：`GrantOrgAsync` 的机构存在校验在超管判定之前，越晚的分支越难构造（见文件头注释）
        await using var scope = await DbScope.CreateAsync(null);
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var roleSvc = sp.GetRequiredService<SysRoleService>();
        var tableSvc = sp.GetRequiredService<SysRoleTableService>();
        var roleRepo = sp.GetRequiredService<ISqlSugarRepository<SysRole>>();
        var orgRepo = sp.GetRequiredService<ISqlSugarRepository<SysOrg>>();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var role = new SysRole { Name = $"sgx_role_{tag}", Code = $"sgx_role_{tag}", SysFlag = false, Remark = string.Empty };
        await roleRepo.InsertAsync(role);
        _roleIds.Add(role.Id);

        // ① 授权不存在的机构 → role.org_not_found（防悬挂引用）
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            roleSvc.GrantOrgAsync(new GrantOrgInput(role.Id, [990_000_000_000_970L])));
        Assert.Equal(ErrorCodes.RoleOrgNotFound, ex1.ErrorCode);

        // ② 非超管且操作者不可解析 → role.operator_unresolved。
        //    ⚠ 实读纠正（本拍踩过）：越权判定**整块包在 `if (orgIds.Count > 0)` 内**（`SysRoleService.cs:393`），
        //    传 null/空集会连越权判定一起跳过（实测不抛）⇒ 必须给**真实存在的机构**才走得到该分支。
        var probeOrg = new SysOrg { Pid = 0, Name = $"sgx_org_{tag}", Code = $"sgx_org_{tag}", Sort = 1, Status = 0, Remark = string.Empty };
        await orgRepo.InsertAsync(probeOrg);
        _orgIds.Add(probeOrg.Id);
        var ex2 = await Assert.ThrowsAsync<AppException>(() => roleSvc.GrantOrgAsync(new GrantOrgInput(role.Id, [probeOrg.Id])));
        Assert.Equal(ErrorCodes.RoleOperatorUnresolved, ex2.ErrorCode);
    }

    [Fact]
    public async Task 角色机构授权_越出操作者范围_精确断言()
    {
        // 已认证但**库里不存在**的用户 ⇒ 无角色 ⇒ 范围集为空（见文件头）⇒ 授权任何机构都越界
        await using var scope = await DbScope.CreateAsync(new AuthenticatedAccessor(990_000_000_000_971L, "1"));
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var roleSvc = sp.GetRequiredService<SysRoleService>();
        var roleRepo = sp.GetRequiredService<ISqlSugarRepository<SysRole>>();
        var orgRepo = sp.GetRequiredService<ISqlSugarRepository<SysOrg>>();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var role = new SysRole { Name = $"sgx_role_{tag}", Code = $"sgx_role_{tag}", SysFlag = false, Remark = string.Empty };
        await roleRepo.InsertAsync(role);
        _roleIds.Add(role.Id);
        var probeOrg = new SysOrg { Pid = 0, Name = $"sgx_org_{tag}", Code = $"sgx_org_{tag}", Sort = 1, Status = 0, Remark = string.Empty };
        await orgRepo.InsertAsync(probeOrg);
        _orgIds.Add(probeOrg.Id);

        var ex = await Assert.ThrowsAsync<AppException>(() => roleSvc.GrantOrgAsync(new GrantOrgInput(role.Id, [probeOrg.Id])));
        Assert.Equal(ErrorCodes.RoleOrgOutOfScope, ex.ErrorCode);
    }

    [Fact]
    public async Task 字段权限开关_两条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync(null);
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var tableSvc = diScope.ServiceProvider.GetRequiredService<SysRoleTableService>();

        // ③ 表名空 → field_scope.table_required
        var ex3 = await Assert.ThrowsAsync<AppException>(() => tableSvc.SetEnabledAsync(" ", true));
        Assert.Equal(ErrorCodes.FieldScopeTableRequired, ex3.ErrorCode);

        // ④ 表名不在实体元数据可配置集 → field_scope.table_not_configurable（防乱写表名污染开关表）
        var ex4 = await Assert.ThrowsAsync<AppException>(() => tableSvc.SetEnabledAsync("sgx_no_such_table", true));
        Assert.Equal(ErrorCodes.FieldScopeTableNotConfigurable, ex4.ErrorCode);
    }

    /// <summary>可造身份的执行上下文（null ⇒ 匿名基线）。</summary>
    private sealed class AuthenticatedAccessor : IHttpContextAccessor
    {
        public AuthenticatedAccessor(long userId, string accountType)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, $"sgx_tester_{userId}"),
                new Claim(JwtClaimNames.AccountType, accountType)
            ], "TestAuth");
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }

        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>最小宿主：真实 PG ＋ 内存缓存；accessor 可注入。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync(IHttpContextAccessor? accessor)
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
                    ["Cache:Type"] = "Memory",
                    ["Yit:WorkerId"] = "1",
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(accessor ?? new AnonymousAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR();
            services.AddAutoServices(typeof(SysRoleService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

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

    private sealed class AnonymousAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
