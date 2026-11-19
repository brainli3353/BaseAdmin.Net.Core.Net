using System.Linq;
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Attributes;
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
    /// 批 CH(G28 菜单 Status 显隐)修复验证——参照 Admin.NET SysMenuService.SetStatus 口径:
    /// ① 管理端 TreeAsync 不过滤(需看见停用节点才能再启用)+ MenuOutput 回带 Status;
    /// ② 登录菜单树:停用节点及其**整棵子树**消失(参照可查询层过滤 Status ⇒ 父行出集、子行孤儿被
    ///    ToTreeAsync 丢弃的等效语义);超管/普通用户两口径同过滤;
    /// ③ SetStatus 部分更新只动 Status 列;非法 status 拒(menu.status_invalid, code=400);不存在 id 拒(menu.not_found);
    /// ④ 再启用后登录树恢复;
    /// ⑤ 按钮(Type=3)停用不影响按钮权限集(API 403 校验面与参照同口径:仅菜单树过滤)。
    /// 探针:99_000_000_008_xx 段(批 CH 专用),固定 Id 幂等重建,finally 物理清理。
    /// </summary>
    public class BatchCHMenuStatusTest
    {
        // 开发库连接串(与 Batch BI/L 同源,dev only;Timeout=10 让不可达场景快速失败而非挂死)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        private const int NormalAccountType = 777;

        // 探针固定 Id(99_000_000_008_xx 段 = 批 CH)
        private const long ProbeDir = 99_000_000_008_01;    // 目录(停用子树宿主:dir→menu→btn)
        private const long ProbeMenu = 99_000_000_008_02;   // 菜单(dir 子)
        private const long ProbeGrand = 99_000_000_008_03;  // 菜单(menu 子,孙层)
        private const long ProbeBtn = 99_000_000_008_04;    // 按钮(menu 子,permission=ch_probe_btn/page)
        private const long ProbeSibling = 99_000_000_008_05; // 菜单(dir 的兄弟,启用对照)
        private const long ProbeTop = 99_000_000_008_06;    // 顶级目录(整树停用探针,子=ProbeLeaf)
        private const long ProbeLeaf = 99_000_000_008_07;   // 菜单(ProbeTop 子,整树消失探针)
        private const long ProbeRole = 99_000_000_008_50;   // 角色(授权探针)
        private const long ProbeUser = 99_000_000_008_60;   // 普通用户(AccountType=777)

        /// <summary>① 管理端 TreeAsync 不过滤:停用节点仍在(前端需可见才能再启用)+ MenuOutput 回带 Status。</summary>
        [Fact]
        public async Task 管理树不过滤_停用节点仍在且回带Status()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            try
            {
                await svc.SetStatusAsync(ProbeDir, 1);

                var tree = await svc.TreeAsync();
                var dir = FindNode(tree, ProbeDir);
                Assert.NotNull(dir);
                Assert.Equal(1, dir!.Status);
                // 停用节点的子仍在管理树(管理端可见性=全量)
                Assert.NotNull(FindNode(tree, ProbeMenu));
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>② 登录树(超管口径):停用顶级节点+整棵子树消失;兄弟子树不受影响。</summary>
        [Fact]
        public async Task 登录树超管_停用整子树消失且兄弟不受影响()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            try
            {
                // 基线:启用态,顶级+叶子均在超管登录树
                var baseTree = await svc.GetLoginMenuTreeAsync(0, 999);
                Assert.NotNull(FindNode(baseTree, ProbeTop));
                Assert.NotNull(FindNode(baseTree, ProbeLeaf));

                await svc.SetStatusAsync(ProbeTop, 1);

                var tree = await svc.GetLoginMenuTreeAsync(0, 999);
                Assert.True(FindNode(tree, ProbeTop) is null, "停用顶级应消失");
                Assert.True(FindNode(tree, ProbeLeaf) is null, "停用顶级的子应整树消失(不浮动)");

                // 兄弟子树对照:ProbeDir(启用)及其子 menu/grand 不受影响
                Assert.NotNull(FindNode(tree, ProbeDir));
                Assert.NotNull(FindNode(tree, ProbeMenu));
                Assert.NotNull(FindNode(tree, ProbeGrand));
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>③ 登录树(普通用户口径):角色授权中停用的菜单+子树消失;孤儿子兜底(父未授权浮动顶级)基线保持。</summary>
        [Fact]
        public async Task 登录树普通用户_停用子树消失且孤儿子兜底保持()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            try
            {
                // 基线:授权集=ProbeMenu+ProbeGrand(父 ProbeDir 未授权 ⇒ ProbeMenu 孤儿子浮动顶级;ProbeGrand 父在集内)
                var baseTree = await svc.GetLoginMenuTreeAsync(ProbeUser, NormalAccountType);
                Assert.True(baseTree.Any(n => n.Id == ProbeMenu), "授权菜单(父未授权)应孤儿子兜底挂顶级");
                Assert.NotNull(FindNode(baseTree, ProbeGrand));

                // 停用 ProbeGrand(启用父 ProbeMenu 在集内)⇒ 消失
                await svc.SetStatusAsync(ProbeGrand, 1);
                var tree1 = await svc.GetLoginMenuTreeAsync(ProbeUser, NormalAccountType);
                Assert.True(FindNode(tree1, ProbeMenu) is not null, "父仍可见");
                Assert.True(FindNode(tree1, ProbeGrand) is null, "停用的子应消失");

                // 停用 ProbeMenu(集内节点,且其父 ProbeDir 未授权)⇒ 自身+子(已停用)全消失
                await svc.SetStatusAsync(ProbeMenu, 1);
                var tree2 = await svc.GetLoginMenuTreeAsync(ProbeUser, NormalAccountType);
                Assert.True(FindNode(tree2, ProbeMenu) is null, "停用节点应消失");
                Assert.True(FindNode(tree2, ProbeGrand) is null, "子树不浮动");
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>④ SetStatus 部分更新:只动 Status(Name/Title/OrderNo 保持);非法 status 拒;不存在 id 拒。</summary>
        [Fact]
        public async Task SetStatus_部分更新仅动Status且非法值与不存在id拒绝()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();
            var db = scope.Db;

            try
            {
                var before = await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu);
                await svc.SetStatusAsync(ProbeMenu, 1);
                await svc.SetStatusAsync(ProbeMenu, 0);

                var after = await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu);
                Assert.Equal(0, after.Status);
                Assert.True(before.Name == after.Name, "部分更新不应动 Name");
                Assert.True(before.Title == after.Title, "部分更新不应动 Title");
                Assert.True(before.OrderNo == after.OrderNo, "部分更新不应动 OrderNo");
                Assert.True(before.Pid == after.Pid, "部分更新不应动 Pid");

                // 非法 status(2)⇒ 拒绝,且 Status 不变
                var ex = await Assert.ThrowsAsync<AppException>(() => svc.SetStatusAsync(ProbeMenu, 2));
                Assert.Equal(ErrorCodes.MenuStatusInvalid, ex.ErrorCode);
                Assert.Equal(400, ex.Code);
                var unchanged = await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu);
                Assert.True(unchanged.Status == 0, "拒绝后 Status 不应变化");

                // 不存在 id(合法 long)⇒ menu.not_found
                var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.SetStatusAsync(99_000_000_008_99, 0));
                Assert.Equal(ErrorCodes.MenuNotFound, ex2.ErrorCode);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>⑤ 再启用后登录树恢复(停用可逆)。</summary>
        [Fact]
        public async Task 再启用_登录树恢复()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();

            try
            {
                await svc.SetStatusAsync(ProbeDir, 1);
                Assert.Null(FindNode(await svc.GetLoginMenuTreeAsync(0, 999), ProbeMenu));

                await svc.SetStatusAsync(ProbeDir, 0);
                var tree = await svc.GetLoginMenuTreeAsync(0, 999);
                Assert.NotNull(FindNode(tree, ProbeDir));
                Assert.NotNull(FindNode(tree, ProbeMenu));
                Assert.NotNull(FindNode(tree, ProbeGrand));
                Assert.Equal(0, FindNode(tree, ProbeDir)!.Status);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>⑥ 按钮(Type=3)停用不影响按钮权限集(参照口径:仅菜单树过滤,API 403 校验面不变)。</summary>
        [Fact]
        public async Task 按钮停用_按钮权限集不变()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbesAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMenuService>();
            var permService = unit.ServiceProvider.GetRequiredService<IMenuPermService>();

            try
            {
                // 基线:ProbeBtn 已授权 ⇒ 权限集含其 Permission
                var before = await permService.GetPermissionsAsync(ProbeUser);
                Assert.True(before.Contains("ch_probe_btn/page"), "授权按钮应进权限集");

                await svc.SetStatusAsync(ProbeBtn, 1);

                // 按钮权限缓存变更即失效(SetStatus 不动缓存 ⇒ 先失效再取,排除缓存假绿)
                await permService.InvalidateUserAsync(ProbeUser);
                var after = await permService.GetPermissionsAsync(ProbeUser);
                Assert.True(after.Contains("ch_probe_btn/page"), "按钮停用不应移出权限集(与参照同口径)");
            }
            finally
            {
                await CleanupAsync(scope);
                // 权限缓存面兜底失效(探针用户已删,防残留负缓存/正缓存串扰他用例)
                using var unit2 = NewUnit(scope);
                await unit2.ServiceProvider.GetRequiredService<IMenuPermService>().InvalidateUserAsync(ProbeUser);
            }
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针夹具:菜单树(dir→menu→{grand,btn},sibling;top→leaf;orphan 孤儿子)+ 角色/用户/授权(幂等重建)。</summary>
        private static async Task EnsureProbesAsync(DbScope scope)
        {
            var db = scope.Db;
            var existingMenuIds = new HashSet<long>(
                await db.Queryable<SysMenu>().Where(u => u.Id >= 99_000_000_008_00 && u.Id <= 99_000_000_008_99)
                    .Select(u => u.Id).ToListAsync());

            var menus = new List<SysMenu>
            {
                new() { Id = ProbeDir, Pid = 0, Name = "ch_probe_dir", Title = "CH探针目录", Type = 1, OrderNo = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeMenu, Pid = ProbeDir, Name = "ch_probe_menu", Title = "CH探针菜单", Type = 2, OrderNo = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeGrand, Pid = ProbeMenu, Name = "ch_probe_grand", Title = "CH探针孙菜单", Type = 2, OrderNo = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeBtn, Pid = ProbeMenu, Name = "ch_probe_btn", Title = "CH探针按钮", Type = 3, Permission = "ch_probe_btn/page", OrderNo = 1, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeSibling, Pid = ProbeDir, Name = "ch_probe_sibling", Title = "CH探针兄弟", Type = 2, OrderNo = 1, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeTop, Pid = 0, Name = "ch_probe_top", Title = "CH探针顶级", Type = 1, OrderNo = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeLeaf, Pid = ProbeTop, Name = "ch_probe_leaf", Title = "CH探针叶子", Type = 2, OrderNo = 0, Status = 0, TenantId = SqlSugarConst.DefaultTenantId }
            };
            var toInsertMenus = menus.Where(m => !existingMenuIds.Contains(m.Id)).ToList();
            if (toInsertMenus.Count > 0)
            {
                await db.Insertable(toInsertMenus).ExecuteCommandAsync();
            }

            // 角色/用户/绑定(探针用户 Password 列 NOT NULL,永不登录;Status=0 启用)
            var roleExists = await db.Queryable<SysRole>().AnyAsync(u => u.Id == ProbeRole);
            if (!roleExists)
            {
                await db.Insertable(new SysRole
                {
                    Id = ProbeRole,
                    Name = "ch_probe_role",
                    Code = "ch_probe_role",
                    Remark = "批 CH 探针角色",
                    DataScope = 1,
                    TenantId = SqlSugarConst.DefaultTenantId
                }).ExecuteCommandAsync();
            }

            var userExists = await db.Queryable<SysUser>().AnyAsync(u => u.Id == ProbeUser);
            if (!userExists)
            {
                await db.Insertable(new SysUser
                {
                    Id = ProbeUser,
                    Account = "ch_probe_u",
                    Password = "probe-dummy-never-login",
                    RealName = "CH探针用户",
                    Status = 0,
                    AccountType = NormalAccountType,
                    TenantId = SqlSugarConst.DefaultTenantId
                }).ExecuteCommandAsync();
            }

            var bindingExists = await db.Queryable<SysUserRole>().AnyAsync(u => u.UserId == ProbeUser);
            if (!bindingExists)
            {
                await db.Insertable(new SysUserRole { Id = ProbeUser + 1, RoleId = ProbeRole, UserId = ProbeUser, TenantId = SqlSugarConst.DefaultTenantId })
                    .ExecuteCommandAsync();
            }

            // 授权集:ProbeMenu+ProbeGrand+ProbeBtn(父 ProbeDir/顶级 ProbeTop 未授权 ⇒ 孤儿子兜底基线)
            var granted = new List<long> { ProbeMenu, ProbeGrand, ProbeBtn };
            for (var i = 0; i < granted.Count; i++)
            {
                var menuId = granted[i];
                var rowId = ProbeRole + 100 + i;
                var exists = await db.Queryable<SysRoleMenu>().AnyAsync(u => u.Id == rowId);
                if (!exists)
                {
                    await db.Insertable(new SysRoleMenu { Id = rowId, RoleId = ProbeRole, MenuId = menuId, TenantId = SqlSugarConst.DefaultTenantId })
                        .ExecuteCommandAsync();
                }
            }
        }

        /// <summary>零污染清理(物理删,幂等):夹具菜单+角色+用户+中间表行。</summary>
        private static async Task CleanupAsync(DbScope scope)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            var db = scope.Db;
            await db.Deleteable<SysRoleMenu>().Where(u => u.RoleId == ProbeRole).ExecuteCommandAsync();
            await db.Deleteable<SysUserRole>().Where(u => u.UserId == ProbeUser).ExecuteCommandAsync();
            await db.Deleteable<SysUser>().Where(u => u.Id == ProbeUser).ExecuteCommandAsync();
            await db.Deleteable<SysRole>().Where(u => u.Id == ProbeRole).ExecuteCommandAsync();
            await db.Deleteable<SysMenu>().Where(u => u.Id >= 99_000_000_008_00 && u.Id <= 99_000_000_008_99).ExecuteCommandAsync();
        }

        /// <summary>树内查找(递归;按钮不剔——管理树含按钮,登录树服务已剔,两处均可直查)。</summary>
        private static MenuOutput? FindNode(List<MenuOutput> nodes, long id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id)
                {
                    return node;
                }

                var found = FindNode(node.Children, id);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集,
        /// 解析 [Service] 的 SysMenuService),InitTables 扫 Core 程序集增量建表(幂等;SysMenu 新增 Status 列经此落地)。
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

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 Batch BI 口径,不阻塞等待)。</summary>
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
