// 批 S-G 纵深期·错误码覆盖补漏小片（B 域·菜单 3 码 ＋ 配置 1 码 ＋ 岗位归属 1 码）。
// 覆盖：menu.type_invalid / menu.permission_required / menu.has_children / config.builtin_undeletable / pos.creator_only。
// 抛点实读：`SysMenuService.cs:338`（type 非 1/2/3）、`:343`（type=3 无权限串）、`:278`（有子节点）、
//           `SysConfigService.cs:206`（SysFlag）、`SysPosService.cs:47`（已认证非超管且非创建者）。
// 身份面（`UserManager` 实读）：`IsAuthenticated ⇐ Identity.IsAuthenticated`、`UserId ⇐ Claim UserId`、
//   `IsSuperAdmin ⇐ AccountType == 999`；而 `CheckOwnership` 注释自证"**匿名/无 Claims 放行**"
//   ⇒ 本拍专门为它建一份**已认证非超管**身份（AccountType=1）的夹具，否则这条码永远测不到。
// 不赌既有数据：内置配置(SysFlag)与"有子菜单"都用自插探针构造前置。
// 纪律：真实 PG；探针 finally 物理删（含 `CopyRole` 式成功路径按前缀兜底，见框架索引 §五）。
using System.Net.Sockets;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Services.Menu.Dto;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.SqlSugar;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageMenuCfgPosTest : IAsyncLifetime
{
    private readonly List<long> _menuIds = new();
    private readonly List<long> _configIds = new();
    private readonly List<long> _posIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_menuIds.Count + _configIds.Count + _posIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync(null);
        if (!scope.PgReachable)
        {
            return;
        }

        if (_menuIds.Count > 0)
        {
            await scope.Db.Deleteable<SysMenu>().In(_menuIds.ToArray()).ExecuteCommandAsync();
        }

        if (_configIds.Count > 0)
        {
            await scope.Db.Deleteable<SysConfig>().In(_configIds.ToArray()).ExecuteCommandAsync();
        }

        if (_posIds.Count > 0)
        {
            await scope.Db.Deleteable<SysPos>().In(_posIds.ToArray()).ExecuteCommandAsync();
        }
    }

    [Fact]
    public async Task 菜单与配置_四条校验码_精确断言()
    {
        // 身份＝超管：④ 内置配置禁删走 `SysConfigService.DeleteAsync`，其第一行是 ㉖ 平台级写闸
        // （非超管先抛 Forbidden）。用超管身份才能穿闸、命中 config.builtin_undeletable 本分支。
        await using var scope = await DbScope.CreateAsync(TestActorAccessor.SuperAdmin());
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var menuSvc = sp.GetRequiredService<SysMenuService>();
        var cfgSvc = sp.GetRequiredService<SysConfigService>();
        var menuRepo = sp.GetRequiredService<ISqlSugarRepository<SysMenu>>();
        var cfgRepo = sp.GetRequiredService<ISqlSugarRepository<SysConfig>>();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // ① type 非 1/2/3 → menu.type_invalid
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            menuSvc.AddAsync(new AddMenuInput(0, $"sgx_menu_{tag}", "探针", 9, null, 100, null)));
        Assert.Equal(ErrorCodes.MenuTypeInvalid, ex1.ErrorCode);

        // ② type=3(按钮)无权限串 → menu.permission_required（按钮无权限键＝权限体系形同虚设）
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            menuSvc.AddAsync(new AddMenuInput(0, $"sgx_btn_{tag}", "探针按钮", 3, " ", 100, null)));
        Assert.Equal(ErrorCodes.MenuPermissionRequired, ex2.ErrorCode);

        // ③ 有子节点禁删 → menu.has_children（自插父＋子，不赌库里恰好有父子菜单）
        var parent = new SysMenu { Pid = 0, Name = $"sgx_p_{tag}", Title = "探针父", Type = 1, OrderNo = 100, Status = 0 };
        await menuRepo.InsertAsync(parent);
        _menuIds.Add(parent.Id);
        var child = new SysMenu { Pid = parent.Id, Name = $"sgx_c_{tag}", Title = "探针子", Type = 1, OrderNo = 100, Status = 0 };
        await menuRepo.InsertAsync(child);
        _menuIds.Add(child.Id);
        var ex3 = await Assert.ThrowsAsync<AppException>(() => menuSvc.DeleteAsync(parent.Id));
        Assert.Equal(ErrorCodes.MenuHasChildren, ex3.ErrorCode);
        Assert.NotNull(await menuRepo.GetFirstAsync(u => u.Id == parent.Id)); // 确实没被删

        // ④ 内置配置禁删 → config.builtin_undeletable
        var builtinCfg = new SysConfig
        {
            Name = "探针内置配置",
            Code = $"sgx.cfg.{tag}",
            Value = "1",
            GroupCode = "sys",
            SysFlag = true,
            Remark = string.Empty
        };
        await cfgRepo.InsertAsync(builtinCfg);
        _configIds.Add(builtinCfg.Id);
        var ex4 = await Assert.ThrowsAsync<AppException>(() => cfgSvc.DeleteAsync(builtinCfg.Id));
        Assert.Equal(ErrorCodes.ConfigBuiltinUndeletable, ex4.ErrorCode);

        // 反向哨兵：无子节点时应真的删掉（证明 ③ 的因由＝存在子节点，而非 Delete 必抛）
        await menuSvc.DeleteAsync(child.Id);
        Assert.Null(await menuRepo.GetFirstAsync(u => u.Id == child.Id));
        // 口径（本拍实测，别写错）：服务层 `DeleteAsync`＝**软删**（UPDATE IsDelete=true，行仍在表内），
        // 而本测试的清理 `Db.Deleteable`＝物理删，但**删不到软删行**——框架全局过滤器会给 DELETE 自动带
        // `IsDelete=false`（对照证据：未删的父探针行确实消失了，软删的子行两次运行都还在，count 不降）。
        // ⇒ 每跑一次留 1 行软删垃圾：**不影响正确性**（业务查询与 `WHERE IsDelete=false` 的唯一索引都看不见它），
        //    根治＝夹具隔离专项（per-collection schema），不在本拍用裸 Ado 强删（会撞 ADO 事务静态守卫的口径）。
        // 因此这里**保留 id** 不清（清了也删不掉），只保证不谎报"已物理清干净"。
    }

    [Fact]
    public async Task 配置写侧闸_非超管被拒且超管放行()
    {
        // ㉖ 平台级配置写侧闸（`SysConfigService.EnsurePlatformAdmin`）自身的正反两向：
        // ① 已认证但非超管（AccountType=1）→ 必抛 common.forbidden（闸真的在拦）；
        // ② 反向哨兵：同一操作换超管身份 → 不得抛 Forbidden（可成功），证明闸不是"永远拒"。
        // 操作选 AddAsync：成功时落的是普通行（非软删），清理可物理删干净，不留垃圾。
        var tag = Guid.NewGuid().ToString("N")[..8];
        var code = $"sgx.gate.{tag}";

        // ① 非超管：闸在写库前抛 Forbidden，且库中确实没有这一行
        await using (var nonAdmin = await DbScope.CreateAsync(TestActorAccessor.Actor(userId: 2L, accountType: "1")))
        {
            if (!nonAdmin.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            using var diScope = nonAdmin.Provider.CreateScope();
            var cfgSvc = diScope.ServiceProvider.GetRequiredService<SysConfigService>();
            var cfgRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysConfig>>();

            var ex = await Assert.ThrowsAsync<AppException>(() =>
                cfgSvc.AddAsync(new AddConfigInput("闸探针", code, "1", "sys")));
            Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);

            Assert.Null(await cfgRepo.GetFirstAsync(u => u.Code == code)); // 闸拦在落库之前
        }

        // ② 超管：同一操作放行（写入成功即证反向；id 入清理清单，finally 物理删）
        await using (var admin = await DbScope.CreateAsync(TestActorAccessor.SuperAdmin()))
        {
            if (!admin.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            using var diScope = admin.Provider.CreateScope();
            var cfgSvc = diScope.ServiceProvider.GetRequiredService<SysConfigService>();
            var id = await cfgSvc.AddAsync(new AddConfigInput("闸探针", code, "1", "sys"));
            _configIds.Add(id);
            Assert.True(id > 0);
        }
    }

    [Fact]
    public async Task 岗位归属_非创建者越权_精确断言()
    {
        // 夹具身份＝**已认证 + 非超管(AccountType=1) + UserId=2**；`CheckOwnership` 对匿名放行，
        // 所以必须造这份身份才进得了那条分支
        var actor = TestActorAccessor.Actor(userId: 2L, accountType: "1");
        await using var scope = await DbScope.CreateAsync(actor);
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var posSvc = sp.GetRequiredService<SysPosService>();
        var posRepo = sp.GetRequiredService<ISqlSugarRepository<SysPos>>();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var probe = new SysPos { Name = $"sgx_pos_{tag}", Code = $"sgx_pos_{tag}", Sort = 1, Status = 0, Remark = string.Empty };
        await posRepo.InsertAsync(probe);
        _posIds.Add(probe.Id);
        // 创建者显式改写成别人（插入时审计可能把创建者写成当前操作者 UserId=2 ⇒ 会放行，必须改写才能进分支）。
        // 用 Ado 直写而非 `Updateable.SetColumns`：后者在本实体上被 SqlSugar 判为"更新主键"直接抛（本拍实测）；
        // 列名实读＝"CreateUserId"(Pascal 带引号) 与 id(小写主键)。
        await SetPosCreatorAsync(scope.Db, probe.Id, 1L);

        var ex = await Assert.ThrowsAsync<AppException>(() => posSvc.DeleteAsync(probe.Id));
        Assert.Equal(ErrorCodes.PosCreatorOnly, ex.ErrorCode);

        // 反向哨兵：同一身份下若**就是创建者**，归属校验必须放行（证明 ① 的因由＝非创建者，而非删除必抛）。
        // 放行后会继续走到"岗位下有用户"校验——本探针无人关联 ⇒ 应真的删掉。
        await SetPosCreatorAsync(scope.Db, probe.Id, 2L);
        await posSvc.DeleteAsync(probe.Id);
        Assert.Null(await posRepo.GetFirstAsync(u => u.Id == probe.Id));
        // 同上：服务层软删，软删行删不到（全局过滤器），不谎报已清干净
    }

    /// <summary>直写探针行的创建者（绕开 SqlSugar 在本实体上对 SetColumns 的主键判定）。</summary>
    private static async Task SetPosCreatorAsync(ISqlSugarClient db, long id, long creatorId) =>
        await db.Ado.ExecuteCommandAsync($"update sys_pos set \"CreateUserId\"={creatorId} where id={id}");

    /// <summary>最小宿主：真实 PG ＋ 内存缓存；accessor 可注入（null＝未认证基线）。</summary>
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
            services.AddAutoServices(typeof(SysMenuService).Assembly);

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
