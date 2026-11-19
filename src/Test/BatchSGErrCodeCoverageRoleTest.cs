// 批 S-G 纵深期·错误码覆盖补漏小片（B 域·角色 7 码）——真库档（写 `sys_role` 探针行，finally 物理删）。
// 覆盖：role.name_code_required / data_scope_invalid（Add 与 Update **双入口**）/ code_exists /
//       name_required / builtin_undeletable / menu_not_found / code_exists_update。
// 抛出点实读（`src/Core/Services/Role/SysRoleService.cs`）：:100 / :110 与 :152 / :105 / :143 / :193 / :239 / :322。
// 不依赖既有数据：内置角色(SysFlag)与"复制撞码"两类都用**自插探针**构造前置，避免"库里恰好没有"导致静默失测。
// 纪律：真实 PG；探针 finally 物理删；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageRoleTest : IAsyncLifetime
{
    private readonly List<long> _roleIds = new();

    // 本会话探针的编码前缀（GUID 后缀）。WHY 需要它：`CopyRoleAsync` 的**成功路径**会造出副本行，
    // 而副本不在 `_roleIds` 里 ⇒ 一旦断言在"预期抛异常但实际成功"时失败，副本就成了**孤儿行**
    // （2026-10-26 本拍实测抓到 2 条 `探针重复 - copy` 残留）。⇒ 清理必须按前缀兜底，不能只按 id。
    private readonly string _probeCodePrefix = "sgx_";

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private readonly string _probeCodeSuffix = Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_roleIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        var ids = _roleIds.ToArray();
        await scope.Db.Deleteable<SysRole>().In(ids).ExecuteCommandAsync();
        // 前缀兜底：连本会话产生但未被记录 id 的副本行一起清（见 `_probeCodePrefix` 注释）
        var suffix = _probeCodeSuffix;
        await scope.Db.Deleteable<SysRole>()
            .Where(u => u.Code!.StartsWith(_probeCodePrefix) && u.Code!.Contains(suffix))
            .ExecuteCommandAsync();
    }

    [Fact]
    public async Task 角色域_七条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysRoleService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysRole>>();
        var suffix = _probeCodeSuffix;
        string Code(string tag) => $"sgx_{tag}_{suffix}";

        // ① 名称或编码为空 → role.name_code_required
        var ex1 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddRoleInput(" ", Code("a"), null, null)));
        Assert.Equal(ErrorCodes.RoleNameCodeRequired, ex1.ErrorCode);

        // ② DataScope 越界 → role.data_scope_invalid(Add 入口;白名单 1-5,越界必须拒绝而非静默兜底)
        var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddRoleInput("n", Code("b"), null, 9)));
        Assert.Equal(ErrorCodes.RoleDataScopeInvalid, ex2.ErrorCode);

        // ③ 编码重复 → role.code_exists
        var dup = new SysRole { Name = "探针重复", Code = Code("dup"), SysFlag = false, Remark = string.Empty };
        await repo.InsertAsync(dup);
        _roleIds.Add(dup.Id);
        var ex3 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddRoleInput("n", Code("dup"), null, null)));
        Assert.Equal(ErrorCodes.RoleCodeExists, ex3.ErrorCode);

        // ④ 更新时名称为空 → role.name_required
        var ex4 = await Assert.ThrowsAsync<AppException>(() => svc.UpdateAsync(new UpdateRoleInput(dup.Id, " ", null, null)));
        Assert.Equal(ErrorCodes.RoleNameRequired, ex4.ErrorCode);

        // ⑤ DataScope 越界的 Update 入口（同码第二入口，此前从未被测试触达）
        var ex5 = await Assert.ThrowsAsync<AppException>(() => svc.UpdateAsync(new UpdateRoleInput(dup.Id, "改名", null, 0)));
        Assert.Equal(ErrorCodes.RoleDataScopeInvalid, ex5.ErrorCode);

        // ⑥ 内置角色禁删 → role.builtin_undeletable（自插 SysFlag=true 探针，不赌库里恰好有内置角色）
        var builtin = new SysRole { Name = "探针内置", Code = Code("sys"), SysFlag = true, Remark = string.Empty };
        await repo.InsertAsync(builtin);
        _roleIds.Add(builtin.Id);
        var ex6 = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(builtin.Id));
        Assert.Equal(ErrorCodes.RoleBuiltinUndeletable, ex6.ErrorCode);
        Assert.NotNull(await repo.GetFirstAsync(u => u.Id == builtin.Id)); // 断言"确实没被删掉"

        // ⑦ 授权不存在的菜单 → role.menu_not_found
        var ex7 = await Assert.ThrowsAsync<AppException>(() =>
            svc.GrantMenuAsync(new GrantMenuInput(dup.Id, [990_000_000_000_960L])));
        Assert.Equal(ErrorCodes.RoleMenuNotFound, ex7.ErrorCode);

        // ⑧ 复制撞码 → role.code_exists_update（复制生成 "<Code> - copy"，预插同名行即撞）
        // 复制生成的新码＝`role.Code + " - copy"`（实读 SysRoleService.cs:318）⇒ 预插行必须撞**被复制那个角色**的码
        var copyProbe = new SysRole { Name = "探针复制", Code = dup.Code + " - copy", SysFlag = false, Remark = string.Empty };
        await repo.InsertAsync(copyProbe);
        _roleIds.Add(copyProbe.Id);
        var ex8 = await Assert.ThrowsAsync<AppException>(() => svc.CopyRoleAsync(dup.Id));
        Assert.Equal(ErrorCodes.RoleCodeExistsUpdate, ex8.ErrorCode);

        // 反向哨兵：DataScope 合法值(1-5)应真的落库（证明 ②⑤ 的因由＝取值越界，而非该方法必抛）
        await svc.UpdateAsync(new UpdateRoleInput(dup.Id, "合法改名", null, 2));
        Assert.Equal(2, (await repo.GetFirstAsync(u => u.Id == dup.Id))!.DataScope);
    }

    /// <summary>最小宿主（照 BatchSGErrCodeCoverageSerialTest）：真实 PG ＋ 内存缓存。</summary>
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
                    ["Cache:Type"] = "Memory",
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

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
