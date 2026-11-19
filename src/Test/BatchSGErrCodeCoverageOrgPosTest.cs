// 批 S-G 纵深期·错误码覆盖补漏小片（B 域·机构/职位，父层自写）：覆盖 7 条"服务校验分支明确、无需外部故障注入"的错误码——
// org.name_required / org.parent_not_found / org.not_found / org.has_children /
// pos.name_code_required / pos.code_exists / pos.not_found。
// WHY 小片：大臂（30+ 码）多次中途失败，改**小片可验证增量**（每片 5–10 码、独立文件、独立探针清理）；
//   覆盖读数唯一家＝`node scripts/error-code-coverage.mjs`（本文件不硬编码总数）。
// 本片有意不含 org.has_users / pos.has_users / pos.creator_only：那三条需探针用户（SysUser 挂靠＋Claim 归属匹配），
//   留作同域下一片，避免把两类探针复杂度混进一片。
// 身份口径（实读服务实现，非推测）：`SysOrgService.GetOperatorOrgScopeAsync` 与 `SysPosService.CheckOwnership`
//   在**未认证**时分别返回 null / 直接放行（无 Claim ⇒ 不参与范围与归属裁剪），故本夹具用空 HttpContext 即可走到目标分支。
// 纪律：真实 PG；探针 finally 物理删（Deleteable＝物理 DELETE，绕开软删过滤器）；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.Services.Pos.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageOrgPosTest : IAsyncLifetime
{
    private readonly List<long> _orgIds = new();
    private readonly List<long> _posIds = new();
    /// <summary>探针用户 Id（职位域"关联用户拒删"用例自建 ⇒ 见该用例注释：**绝不留库**）。</summary>
    private readonly List<long> _userIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_orgIds.Count == 0 && _posIds.Count == 0 && _userIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        await PhysicalDeleteAsync<SysOrg>(scope.Db, _orgIds);
        await PhysicalDeleteAsync<SysPos>(scope.Db, _posIds);
        // 探针用户最后删（职位先删也不影响：本表只按 Id 物理删，不参与职位断言）
        await PhysicalDeleteAsync<SysUser>(scope.Db, _userIds);
    }

    [Fact]
    public async Task 机构域_四条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysOrgService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysOrg>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // ① 名称空白 → org.name_required(校验在范围判定之前,无身份即可达)
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddOrgInput(0, " ", null)));
        Assert.Equal(ErrorCodes.OrgNameRequired, ex1.ErrorCode);

        // ② 父不存在 → org.parent_not_found(Pid 非 0 且 Code 为空跳过唯一校验)
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddOrgInput(990_000_000_000_910L, $"sgx_org_{suffix}", null)));
        Assert.Equal(ErrorCodes.OrgParentNotFound, ex2.ErrorCode);

        // ③ 删除不存在 id → org.not_found(在范围判定之前)
        var ex3 = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(990_000_000_000_911L));
        Assert.Equal(ErrorCodes.OrgNotFound, ex3.ErrorCode);

        // ④ 有子节点拒删 → org.has_children(自建父子探针)
        var parent = new SysOrg { Pid = 0, Name = $"sgx_org_p_{suffix}", Code = null, Sort = 0 };
        await repo.InsertAsync(parent);
        _orgIds.Add(parent.Id);
        var child = new SysOrg { Pid = parent.Id, Name = $"sgx_org_c_{suffix}", Code = null, Sort = 0 };
        await repo.InsertAsync(child);
        _orgIds.Add(child.Id);

        var ex4 = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(parent.Id));
        Assert.Equal(ErrorCodes.OrgHasChildren, ex4.ErrorCode);

        // 反向哨兵:物理删子节点后父节点应可删(证明 ④ 的因由＝子节点存在,而非"该服务删除必失败";
        // 用 DeletePhysicalByAsync 绕开软删过滤器,免得软删子行仍被后续用例的同父判重/建树扫到)
        await repo.DeletePhysicalByAsync(o => o.Id == child.Id);
        await svc.DeleteAsync(parent.Id);
    }

    [Fact]
    public async Task 机构域_关联用户拒删_精确断言()
    {
        // 覆盖 org.has_users（此前"需写库"的真缺口）。**本用例不插任何行**——依据是实读的抛点顺序：
        //   `SysOrgService.DeleteAsync` = 不存在 → 范围 → **有子节点** → **关联用户** → `DeleteByIdAsync`，
        //   命中本码即返回 ⇒ 天然零写库。故只需在库内动态发现"叶子且被用户挂靠"的真实机构（实读 2026-10-26 存在多条）。
        // WHY 不插一行用户：插了就多一条"删不干净就串扰后续"的尾巴（本仓 `sys_user` 已有数百个 `t13q_u_*` 残留为证）；
        //   能靠真实数据钉住的，就不该制造残留。
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysOrgService>();
        var orgRepo = sp.GetRequiredService<ISqlSugarRepository<SysOrg>>();
        var userRepo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();

        long targetId = 0;
        foreach (var org in await orgRepo.AsQueryable().Where(o => o.Pid != 0).Take(40).ToListAsync())
        {
            // 必须先排掉"有子节点"的：那条会命中 org.has_children，测不到本码（抛点顺序决定）
            if (await orgRepo.IsAnyAsync(c => c.Pid == org.Id))
            {
                continue;
            }

            if (await userRepo.IsAnyAsync(u => u.OrgId == org.Id))
            {
                targetId = org.Id;
                break;
            }
        }

        Assert.True(targetId != 0,
            "前置不成立：库内找不到『叶子且被用户挂靠』的机构。本用例不插行（见上），故此处必须红，禁止假绿");

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(targetId));
        Assert.Equal(ErrorCodes.OrgHasUsers, ex.ErrorCode);
    }

    [Fact]
    public async Task 职位域_三条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysPosService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysPos>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var code = $"sgx_pos_{suffix}";

        // ① 名称或编码空白 → pos.name_code_required
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddPosInput(null, code)));
        Assert.Equal(ErrorCodes.PosNameCodeRequired, ex1.ErrorCode);

        // ② Code 全表唯一 → pos.code_exists(先自建探针行再以同码新增)
        var probe = new SysPos { Name = $"sgx_pos_{suffix}", Code = code, Sort = 0, Remark = null };
        await repo.InsertAsync(probe);
        _posIds.Add(probe.Id);
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddPosInput($"sgx_pos_dup_{suffix}", code)));
        Assert.Equal(ErrorCodes.PosCodeExists, ex2.ErrorCode);

        // ③ 更新不存在 id → pos.not_found(在归属校验之前;同码亦覆盖 DeleteAsync 的 not_found 分支)
        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdatePosInput(990_000_000_000_912L, "sgx", "sgx_code")));
        Assert.Equal(ErrorCodes.PosNotFound, ex3.ErrorCode);

        var ex4 = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(990_000_000_000_913L));
        Assert.Equal(ErrorCodes.PosNotFound, ex4.ErrorCode);

        // 反向哨兵:更新自身且 Code 不变应成功(证明 ② 的因由＝与他行撞码,唯一性校验已排除自身)
        await svc.UpdateAsync(new UpdatePosInput(probe.Id, $"sgx_pos_ren_{suffix}", code));
    }

    [Fact]
    public async Task 职位域_关联用户拒删_精确断言()
    {
        // 覆盖 pos.has_users。与机构域那条**相反，本条必须写库**：实读全库 `sys_pos` 没有任何被 `sys_user."PosId"`
        //   引用的行（`WHERE EXISTS(...)` 返回 0 行）⇒ 没有可复用的真实存量数据，只能自建探针并负责删干净。
        // 身份口径：`CheckOwnership` 首条件是 `_userManager.IsAuthenticated && UserId is > 0`（实读 `SysPosService.cs:44-45`）
        //   ⇒ 本夹具的 `HttpContext=null` 走短路放行，不会被 `pos.creator_only` 抢先（这条决定了本用例可行的前提）。
        // 探针用户**只填必要列**：实读 `sys_user` 无默认值的 NOT NULL 列＝`Account/Password/RealName/Status/IsDelete/CreateTime`。
        //   ⚠ **刻意不克隆 admin 行**（本仓 e2e 旧惯例 `INSERT … SELECT * FROM sys_user WHERE Account='admin'`）——
        //   克隆会把真实口令散列等凭据列复制进一条探针行，本用例不需要，就不冒这个险。
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysPosService>();
        var posRepo = sp.GetRequiredService<ISqlSugarRepository<SysPos>>();
        var userRepo = sp.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var pos = new SysPos { Name = $"sgx_posu_{suffix}", Code = $"sgx_posu_{suffix}", Sort = 0, Remark = null };
        await posRepo.InsertAsync(pos);
        _posIds.Add(pos.Id);

        var probeUser = new SysUser
        {
            Account = $"sgx_posu_{suffix}",
            Password = "sgx-probe-not-a-credential",
            RealName = "SGX职位探针",
            PosId = pos.Id,
        };
        await userRepo.InsertAsync(probeUser);
        _userIds.Add(probeUser.Id);

        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(pos.Id));
            Assert.Equal(ErrorCodes.PosHasUsers, ex.ErrorCode);

            // 反向哨兵：物理删探针用户后，**同一个职位应可删** ⇒ 证明上一步的因由＝用户挂靠，而非"删除必失败"
            await userRepo.DeletePhysicalByAsync(u => u.Id == probeUser.Id);
            _userIds.Remove(probeUser.Id);
            await svc.DeleteAsync(pos.Id);
        }
        finally
        {
            // 无论断言走到哪一步，探针用户都不留库（本仓已为数百个 `t13q_u_*` 残留付过债，见专项件 §13）
            await userRepo.DeletePhysicalByAsync(u => u.Id == probeUser.Id);
        }
    }

    private static async Task PhysicalDeleteAsync<T>(ISqlSugarClient db, List<long> ids)
        where T : class, new()
    {
        if (ids.Count == 0)
        {
            return;
        }

        await db.Deleteable<T>().In(ids.ToArray()).ExecuteCommandAsync();
    }

    /// <summary>最小宿主(照 BatchSGErrCodeCoverageExtraTest):真实 PG + 自动服务注册 + 空 HttpContext(未认证)。</summary>
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
            services.AddSignalR();
            services.AddAutoServices(typeof(SysOrgService).Assembly);

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

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
