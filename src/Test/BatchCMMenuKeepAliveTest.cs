using System.Linq;
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
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
    /// 批 CM 菜单级「是否缓存」(sys_menu.IsKeepAlive)验证:
    /// ① 新建缺省=1(缓存,与加列前"叶子菜单一律缓存"行为一致);显式 0=不缓存;
    /// ② 更新为部分更新:缺省 null 不改动(与 OrderNo 同口径);显式 0/1 生效;
    /// ③ 管理树 TreeAsync 与登录树 GetLoginMenuTreeAsync 均回带 IsKeepAlive(前端据此落 meta.keepAlive)。
    /// 探针:99_000_000_011_xx 段(批 CM 专用),固定 Id 幂等重建,finally 物理清理。
    /// </summary>
    public class BatchCMMenuKeepAliveTest
    {
        // 开发库连接串(与 Batch CH 同源,dev only;Timeout=10 让不可达场景快速失败而非挂死)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        // 探针固定 Id(99_000_000_011_xx 段 = 批 CM)
        private const long ProbeDir = 99_000_000_011_01;   // 目录(承载子菜单)
        private const long ProbeMenu = 99_000_000_011_02;  // 菜单(缺省缓存/更新探针)
        private const long ProbeBtn = 99_000_000_011_03;   // 按钮(类型无关,缓存字段不应影响权限)

        /// <summary>① 新建缺省=1(缓存);显式 0=不缓存。</summary>
        [Fact]
        public async Task 新建_缺省缓存1_显式0不缓存()
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

            long defaulted = 0;
            long noCache = 0;
            try
            {
                defaulted = await svc.AddAsync(new AddMenuInput(ProbeDir, "cm_probe_default", "CM探针缺省", 2, null, null, null));
                var row1 = await db.Queryable<SysMenu>().FirstAsync(u => u.Id == defaulted);
                Assert.Equal(1, row1.IsKeepAlive);

                noCache = await svc.AddAsync(new AddMenuInput(ProbeDir, "cm_probe_nocache", "CM探针不缓存", 2, null, null, null, 0));
                var row2 = await db.Queryable<SysMenu>().FirstAsync(u => u.Id == noCache);
                Assert.Equal(0, row2.IsKeepAlive);
            }
            finally
            {
                if (defaulted != 0)
                {
                    await db.Deleteable<SysMenu>().Where(u => u.Id == defaulted).ExecuteCommandAsync();
                }

                if (noCache != 0)
                {
                    await db.Deleteable<SysMenu>().Where(u => u.Id == noCache).ExecuteCommandAsync();
                }

                await CleanupAsync(scope);
            }
        }

        /// <summary>② 更新为部分更新:缺省 null 不改;显式 0/1 生效。</summary>
        [Fact]
        public async Task 更新_缺省不改动_显式值生效()
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
                // 基线=1;不带 isKeepAlive(尾部缺省 null)更新 ⇒ 保持 1
                await svc.UpdateAsync(new UpdateMenuInput(ProbeMenu, ProbeDir, "cm_probe_menu", "CM探针菜单", 2, null, null, null));
                Assert.Equal(1, (await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu)).IsKeepAlive);

                // 显式 0 ⇒ 落 0
                await svc.UpdateAsync(new UpdateMenuInput(ProbeMenu, ProbeDir, "cm_probe_menu", "CM探针菜单", 2, null, null, null, 0));
                Assert.Equal(0, (await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu)).IsKeepAlive);

                // 再显式 1 ⇒ 恢复
                await svc.UpdateAsync(new UpdateMenuInput(ProbeMenu, ProbeDir, "cm_probe_menu", "CM探针菜单", 2, null, null, null, 1));
                Assert.Equal(1, (await db.Queryable<SysMenu>().FirstAsync(u => u.Id == ProbeMenu)).IsKeepAlive);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>③ 管理树与登录树均回带 IsKeepAlive(前端 meta.keepAlive 数据源)。</summary>
        [Fact]
        public async Task 管理树与登录树_回带IsKeepAlive()
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
                await db.Updateable<SysMenu>()
                    .SetColumns(u => u.IsKeepAlive == 0)
                    .Where(u => u.Id == ProbeMenu)
                    .ExecuteCommandAsync();

                var adminTree = await svc.TreeAsync();
                Assert.Equal(0, FindNode(adminTree, ProbeMenu)!.IsKeepAlive);
                Assert.Equal(1, FindNode(adminTree, ProbeBtn)!.IsKeepAlive);

                var loginTree = await svc.GetLoginMenuTreeAsync(0, 999);
                Assert.Equal(0, FindNode(loginTree, ProbeMenu)!.IsKeepAlive);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针夹具:dir→{menu,btn}(幂等重建;菜单缺省缓存=1)。</summary>
        private static async Task EnsureProbesAsync(DbScope scope)
        {
            var db = scope.Db;
            var existingIds = new HashSet<long>(
                await db.Queryable<SysMenu>().Where(u => u.Id >= 99_000_000_011_00 && u.Id <= 99_000_000_011_99)
                    .Select(u => u.Id).ToListAsync());

            var menus = new List<SysMenu>
            {
                new() { Id = ProbeDir, Pid = 0, Name = "cm_probe_dir", Title = "CM探针目录", Type = 1, OrderNo = 0, Status = 0, IsKeepAlive = 1, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeMenu, Pid = ProbeDir, Name = "cm_probe_menu", Title = "CM探针菜单", Type = 2, OrderNo = 0, Status = 0, IsKeepAlive = 1, TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = ProbeBtn, Pid = ProbeMenu, Name = "cm_probe_btn", Title = "CM探针按钮", Type = 3, Permission = "cm_probe_btn/page", OrderNo = 1, Status = 0, IsKeepAlive = 1, TenantId = SqlSugarConst.DefaultTenantId }
            };
            var toInsert = menus.Where(m => !existingIds.Contains(m.Id)).ToList();
            if (toInsert.Count > 0)
            {
                await db.Insertable(toInsert).ExecuteCommandAsync();
            }
        }

        /// <summary>零污染清理(物理删,幂等)。</summary>
        private static async Task CleanupAsync(DbScope scope)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            await scope.Db.Deleteable<SysMenu>()
                .Where(u => u.Id >= 99_000_000_011_00 && u.Id <= 99_000_000_011_99)
                .ExecuteCommandAsync();
        }

        /// <summary>树内查找(递归;管理树含按钮,登录树服务已剔,两处均可直查)。</summary>
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
        /// 解析 [Service] 的 SysMenuService),InitTables 扫 Core 程序集增量建表(幂等;IsKeepAlive 列经此落地)。
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

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 Batch CH 口径,不阻塞等待)。</summary>
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
