using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Services.Menu.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BI(menu 菜单管理复审)修复验证——参照口径三件(Admin.NET D4009/D4010/D4000):
    /// D1 Name 全局唯一——Name 是前端消费的路由标识(menu store 逐级拼 path 段 + Vue Router 路由名),
    ///    重名 = 命名路由重复 + 组件解析歧义;软删后同名重建允许(软删过滤自动生效);
    /// D2 父节点禁按钮——按钮是叶子,子挂按钮下时登录菜单树(剔按钮)把子静默浮动到顶级
    ///    (管理端嵌套 / 登录端浮动 = 语义断裂);
    /// D3 同级判重——非按钮 Title+Type 同父重复 / 按钮 Permission 同父重复(权限集语义下重复键冗余);
    ///    附正常子节点创建对照(检查不过度拦截)。
    /// 探针:99_000_000_006_xx 段(批 BI 专用),固定 Id 幂等重建,finally 物理清理;
    /// 探针直插(绕过服务校验)作夹具,校验路径经服务方法实跑。
    /// </summary>
    public class BatchBIMenuTest
    {
        // 开发库连接串(与 BatchAD/H/AH/BH 同源,dev only;Timeout=10 让不可达场景快速失败而非挂死)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        // 探针固定 Id(99_000_000_006_xx 段 = 批 BI)
        private const long ProbeA = 99_000_000_006_01;        // name=bi_probe_a(Name 唯一探针)
        private const long ProbeBtn = 99_000_000_006_02;      // 按钮(父节点禁按钮探针)
        private const long ProbeC = 99_000_000_006_03;        // 普通菜单(改挂/判重探针)
        private const long ProbeDir = 99_000_000_006_04;      // 目录(同级判重宿主)
        private const long ProbeDupTitle = 99_000_000_006_05; // 目录,与 ProbeDup 判重用例同 Title
        private const long ProbeBtnF = 99_000_000_006_06;    // 按钮,permission=bi_probe_dup/page
        private const long ProbeBtnG = 99_000_000_006_07;    // 按钮,与 ProbeBtnF 同 permission(夹具自含重复态)
        private const long ProbeDupMenu = 99_000_000_006_08; // 菜单(Type=2),Update 分支判重宿主(Title 须与 ProbeC 目标 Title 同 Type)

        /// <summary>D1:Name 全局唯一——新增撞名拒绝;软删后同名重建允许(软删过滤口径)。</summary>
        [Fact]
        public async Task 新增菜单_Name全局唯一且软删不阻塞()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeMenusAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            long readded = 0;
            try
            {
                // 撞名(不同 Pid/Title,Name 相同)⇒ 拒绝
                var ex = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddMenuInput(0, "bi_probe_a", "BI复验A", 2, null, null, null)));
                Assert.Equal(ErrorCodes.MenuNameExists, ex.ErrorCode);

                // 软删 ProbeA 后同名重建 ⇒ 允许(软删行被全局过滤,不阻塞)
                await svc.DeleteAsync(ProbeA);
                readded = await svc.AddAsync(new AddMenuInput(0, "bi_probe_a", "BI重建A", 2, null, null, null));
                Assert.NotEqual(0, readded);
                Assert.NotEqual(ProbeA, readded);
            }
            finally
            {
                await CleanupAsync(scope, readded);
            }
        }

        /// <summary>D2:父节点禁按钮——新增/改挂到按钮下均拒绝(按钮是叶子,登录树会静默浮动其子)。</summary>
        [Fact]
        public async Task 父节点禁按钮_新增与改挂均拒绝()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeMenusAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            try
            {
                // 新增挂到按钮下 ⇒ 拒绝
                var ex1 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddMenuInput(ProbeBtn, "bi_probe_under_btn", "BI按钮下", 2, null, null, null)));
                Assert.Equal(ErrorCodes.MenuParentIsButton, ex1.ErrorCode);

                // 改挂到按钮下 ⇒ 拒绝(ProbeC 不变——异常先于落库)
                var ex2 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateMenuInput(ProbeC, ProbeBtn, "bi_probe_c", "BI探针C", 2, null, null, null)));
                Assert.Equal(ErrorCodes.MenuParentIsButton, ex2.ErrorCode);

                var cAfter = await scope.Db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeC);
                Assert.Equal(0, cAfter.Pid);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>D3:同级判重——目录 Title 重复/按钮 Permission 重复/Update 撞同级均拒绝;正常子节点对照放行。</summary>
        [Fact]
        public async Task 同级判重_目录Title与按钮Permission且正常子放行()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeMenusAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            long okChild = 0;
            try
            {
                // 目录分支:同父同 Title 同 Type ⇒ 拒绝
                var ex1 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddMenuInput(ProbeDir, "bi_probe_dir_dup2", "BI探针子目录", 1, null, null, null)));
                Assert.Equal(ErrorCodes.MenuDuplicate, ex1.ErrorCode);

                // 按钮分支:同父同 Permission ⇒ 拒绝
                var ex2 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddMenuInput(ProbeDir, "bi_probe_btn_g2", "BI重复按钮2", 3, "bi_probe_dup/page", null, null)));
                Assert.Equal(ErrorCodes.MenuDuplicate, ex2.ErrorCode);

                // Update 分支:改 Title 撞同级(Type+Title+Pid 三同)⇒ 拒绝
                var ex3 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateMenuInput(ProbeC, ProbeDir, "bi_probe_c", "BI探针子菜单", 2, null, null, null)));
                Assert.Equal(ErrorCodes.MenuDuplicate, ex3.ErrorCode);

                // 对照:不同 Title 的同父子节点 ⇒ 放行(检查不过度拦截)
                okChild = await svc.AddAsync(new AddMenuInput(ProbeDir, "bi_probe_ok_child", "BI正常子节点", 2, null, null, null));
                Assert.NotEqual(0, okChild);
            }
            finally
            {
                await CleanupAsync(scope, okChild);
            }
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        private static async Task EnsureProbeMenusAsync(DbScope scope)
        {
            var db = scope.Db;
            var existing = new HashSet<long>(
                await db.Queryable<SysMenu>().Where(u => u.Id >= 99_000_000_006_00 && u.Id <= 99_000_000_006_99)
                    .Select(u => u.Id).ToListAsync());

            var probes = new List<SysMenu>
            {
                new() { Id = ProbeA, Pid = 0, Name = "bi_probe_a", Title = "BI探针A", Type = 2, OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeBtn, Pid = 0, Name = "bi_probe_btn", Title = "BI探针按钮", Type = 3, Permission = "bi_probe_btn/page", OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeC, Pid = 0, Name = "bi_probe_c", Title = "BI探针C", Type = 2, OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeDir, Pid = 0, Name = "bi_probe_dir", Title = "BI探针目录", Type = 1, OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeDupTitle, Pid = ProbeDir, Name = "bi_probe_dir_dup", Title = "BI探针子目录", Type = 1, OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeBtnF, Pid = ProbeDir, Name = "bi_probe_btn_f", Title = "BI探针按钮F", Type = 3, Permission = "bi_probe_dup/page", OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeBtnG, Pid = ProbeDir, Name = "bi_probe_btn_g", Title = "BI探针按钮G", Type = 3, Permission = "bi_probe_dup/page", OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeDupMenu, Pid = ProbeDir, Name = "bi_probe_menu_dup", Title = "BI探针子菜单", Type = 2, OrderNo = 0, TenantId = SqlSugarConst.DefaultTenantId }
            };

            var toInsert = probes.Where(p => !existing.Contains(p.Id)).ToList();
            if (toInsert.Count > 0)
            {
                await db.Insertable(toInsert).ExecuteCommandAsync();
            }
        }

        /// <summary>零污染清理(物理删,幂等):夹具段 + 动态新行。探针无角色引用,无中间表/缓存面。</summary>
        private static async Task CleanupAsync(DbScope scope, long dynamicId = 0)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            var ids = new List<long>
            {
                ProbeA, ProbeBtn, ProbeC, ProbeDir, ProbeDupTitle, ProbeBtnF, ProbeBtnG, ProbeDupMenu
            };
            if (dynamicId > 0)
            {
                ids.Add(dynamicId);
            }

            await scope.Db.Deleteable<SysMenu>()
                .Where(u => ids.Contains(u.Id)).ExecuteCommandAsync();
        }

        /// <summary>
        /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集,
        /// 解析 [Service] 的 SysMenuService),InitTables 扫 Core 程序集增量建表(幂等)。
        /// WHY 异步工厂而非同步构造:PG 探测是 I/O,同步构造只能 .Wait()/.Result 硬等(项目硬约定禁),
        /// 故探测改 await(TcpClient.ConnectAsync),容器经 CreateAsync 产出(与 BatchAH/BH 同口径)。
        /// </summary>
        private sealed class DbScope : IAsyncDisposable
        {
            public ISqlSugarClient Db { get; private set; } = null!;

            public ServiceProvider Provider { get; private set; } = null!;

            public bool PgReachable { get; private set; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

            public static async Task<DbScope> CreateAsync()
            {
                var scope = new DbScope();
                if (!await IsPostgresReachableAsync())
                {
                    // 降级:不建容器(用例侧 Assert.Fail);Provider 保持空壳以免调用方 NRE 掩盖真实原因
                    scope.Provider = new ServiceCollection().BuildServiceProvider();
                    return scope;
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
                services.AddSingleton<IHttpContextAccessor>(scope.FakeAccessor); // 须先于 AddSqlSugar(TryAdd 语义)
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config); // 无 ConnectionStrings:Redis → 内存实现(进程内,测试互不串扰)
                services.AddAutoServices(typeof(SysMenuService).Assembly);

                scope.Provider = services.BuildServiceProvider();
                scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
                TestSchemaBootstrap.Ensure(scope.Db);
                scope.PgReachable = true;
                return scope;
            }

            public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchAH 口径,不阻塞等待)。</summary>
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
}
