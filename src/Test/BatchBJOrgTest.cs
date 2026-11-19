using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Reflection;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BJ(sysOrg 机构管理复审)修复验证——参照口径两件(Admin.NET D2002/D2001 分支 2):
    /// D1 同父同名判重——机构显示名同父重复=管理端歧义(Code 已由全表唯一覆盖,严于参照 per-Pid 口径);
    ///    Update 按**生效值**(null=保持现值)判并排除自身(改名回原名/仅改排序不算冲突);
    /// D2 跨级环检查——改挂自身后代 ⇒ GetChildIdListWithSelfById BFS 死循环(hang/DoS)
    ///    + TreeAsync 组树丢整支(环无根),参照 D2001 分支 2 同口径;自环(D2001 分支 1)已有。
    /// 探针:99_000_000_007_xx 段(批 BJ 专用),固定 Id 幂等重建,finally 物理清理;
    /// 探针直插(绕过服务校验)作夹具,校验路径经服务方法实跑;每用例独立 finally。
    /// </summary>
    public class BatchBJOrgTest
    {
        // 开发库连接串(与 BatchAD/H/AH/BI 同源,dev only;Timeout=10 让不可达场景快速失败而非挂死)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        // 探针固定 Id(99_000_000_007_xx 段 = 批 BJ)
        private const long PRoot = 99_000_000_007_01;   // 顶级根(环探针锚点)
        private const long PChild = 99_000_000_007_02;  // PRoot 之子(改挂/判重探针)
        private const long PGrand = 99_000_000_007_03;  // PChild 之子(PRoot 之孙,跨级环目标)
        private const long PSib = 99_000_000_007_04;    // PRoot 之子,与 PChild 同级(Name 判重宿主)

        /// <summary>D1:同父同名拒绝;Code 全表唯一(既有口径)保持;不同名对照放行。</summary>
        [Fact]
        public async Task 新增机构_同父同名拒绝且Code全表唯一()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeOrgsAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();

            long okNew = 0;
            try
            {
                // 同父同名(PSib 已占 "bj_probe_sib"@PRoot)⇒ 拒绝
                var ex1 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddOrgInput(PRoot, "bj_probe_sib", null)));
                Assert.Equal(ErrorCodes.OrgDuplicate, ex1.ErrorCode);

                // Code 全表唯一(既有口径,Code 已被 PRoot 占用)⇒ 拒绝(不同父也不放行)
                var ex2 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddOrgInput(0, "bj_probe_code_dup", "bj_probe_root")));
                Assert.Equal(ErrorCodes.OrgCodeExists, ex2.ErrorCode);

                // 对照:不同名的新子节点 ⇒ 放行(检查不过度拦截)
                okNew = await svc.AddAsync(new AddOrgInput(PRoot, "bj_probe_new_ok", null));
                Assert.NotEqual(0, okNew);
            }
            finally
            {
                await CleanupAsync(scope, okNew);
            }
        }

        /// <summary>D1(Update 分支):改名为同级已占名拒绝;改名回自身现名放行(排除自身)。</summary>
        [Fact]
        public async Task 更新机构_同父同名拒绝且排除自身()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeOrgsAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();

            try
            {
                // PChild 改名为 PSib 已占名(生效值:Name=bj_probe_sib,Pid 显式 PRoot)⇒ 拒绝
                var ex = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateOrgInput(PChild, PRoot, "bj_probe_sib", null, null, null, null)));
                Assert.Equal(ErrorCodes.OrgDuplicate, ex.ErrorCode);

                // 对照:PSib 改名回自身现名(Pid 不传=保持 PRoot)⇒ 放行(排除自身,不过度拦截)
                await svc.UpdateAsync(new UpdateOrgInput(PSib, null, "bj_probe_sib", null, null, null, null));
                var sibAfter = await scope.Db.Queryable<SysOrg>().FirstAsync(u => u.Id == PSib);
                Assert.Equal("bj_probe_sib", sibAfter.Name);
                Assert.Equal(PRoot, sibAfter.Pid);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>D2:改挂自身子/孙均拒绝(跨级环);自环既有口径保持;合法改挂对照放行。</summary>
        [Fact]
        public async Task 更新机构_改挂自身后代拒绝且合法改挂放行()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeOrgsAsync(scope);
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();

            try
            {
                // PRoot 改挂自身之子 PChild ⇒ 拒绝(跨级环)
                var ex1 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateOrgInput(PRoot, PChild, null, null, null, null, null)));
                Assert.Equal(ErrorCodes.OrgParentDescendant, ex1.ErrorCode);

                // PRoot 改挂自身之孙 PGrand ⇒ 拒绝(跨级不止一级)
                var ex2 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateOrgInput(PRoot, PGrand, null, null, null, null, null)));
                Assert.Equal(ErrorCodes.OrgParentDescendant, ex2.ErrorCode);

                // 自环(既有口径)⇒ 拒绝
                var ex3 = await Assert.ThrowsAsync<AppException>(() =>
                    svc.UpdateAsync(new UpdateOrgInput(PRoot, PRoot, null, null, null, null, null)));
                Assert.Equal(ErrorCodes.OrgParentSelf, ex3.ErrorCode);

                // 对照:PChild 合法改挂顶级 ⇒ 放行(树不闭环时检查不拦截)
                await svc.UpdateAsync(new UpdateOrgInput(PChild, 0, null, null, null, null, null));
                var childAfter = await scope.Db.Queryable<SysOrg>().FirstAsync(u => u.Id == PChild);
                Assert.Equal(0, childAfter.Pid);

                // 环拒绝后 PRoot 保持顶级(异常先于落库)
                var rootAfter = await scope.Db.Queryable<SysOrg>().FirstAsync(u => u.Id == PRoot);
                Assert.Equal(0, rootAfter.Pid);
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>
        /// D3:控制器 BizError 出口错误码口径——错误码口径回带 errCode＋msg 按语言渲染(非裸键);
        /// 存量口径(ErrorCode=null)行为不变。批 AQ 迁移后 SysOrgController/SysJobController 的局部 catch
        /// 旧实现用 ex.Message(=裸键)当 msg 且丢 errCode(真机 e2e 实锤),本用例钉死修复面。
        /// </summary>
        [Fact]
        public async Task 控制器BizError_错误码口径回带errCode且渲染文案()
        {
            // 批 BP D4:本用例经 DbScope 容器解析 ILocalizationService(BizError 出口渲染依赖容器注册面,
            // 与生产 Program.cs 装配同口径——DbScope 已含 AddAdminLocalization,不再依赖跨测试静态侧效)。
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();
            var controller = new AdminCore.Web.Entry.Controllers.SysOrgController(svc)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = unit.ServiceProvider }
                }
            };

            var method = typeof(AdminCore.Web.Entry.Controllers.SysOrgController)
                .GetMethod("BizError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            // 错误码口径:errCode 回带 + msg 渲染为中文文案(内置默认兜底,非裸键)
            var r1 = (Microsoft.AspNetCore.Mvc.ObjectResult)method!.Invoke(controller,
                new object?[] { AppException.Of(ErrorCodes.OrgDuplicate) })!;
            var a1 = (AdminCore.Core.Models.ApiResult)r1.Value!;
            Assert.Equal(ErrorCodes.OrgDuplicate, a1.ErrCode);
            Assert.NotEqual(ErrorCodes.OrgDuplicate, a1.Msg);
            Assert.Equal("同父下已存在同名机构", a1.Msg);
            Assert.Equal(400, r1.StatusCode);

            // 存量口径(ErrorCode=null):msg 原样,ErrCode=null(行为不变)
            var r2 = (Microsoft.AspNetCore.Mvc.ObjectResult)method.Invoke(controller,
                new object?[] { new AppException("存量口径消息") })!;
            var a2 = (AdminCore.Core.Models.ApiResult)r2.Value!;
            Assert.Null(a2.ErrCode);
            Assert.Equal("存量口径消息", a2.Msg);
            Assert.Equal(400, r2.StatusCode);
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        private static async Task EnsureProbeOrgsAsync(DbScope scope)
        {
            var db = scope.Db;
            var existing = new HashSet<long>(
                await db.Queryable<SysOrg>().Where(u => u.Id >= 99_000_000_007_00 && u.Id <= 99_000_000_007_99)
                    .Select(u => u.Id).ToListAsync());

            var probes = new List<SysOrg>
            {
                new() { Id = PRoot, Pid = 0, Name = "bj_probe_root", Code = "bj_probe_root", TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = PChild, Pid = PRoot, Name = "bj_probe_child", TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = PGrand, Pid = PChild, Name = "bj_probe_grand", TenantId = SqlSugarConst.DefaultTenantId },
                new() { Id = PSib, Pid = PRoot, Name = "bj_probe_sib", TenantId = SqlSugarConst.DefaultTenantId }
            };

            var toInsert = probes.Where(p => !existing.Contains(p.Id)).ToList();
            if (toInsert.Count > 0)
            {
                await db.Insertable(toInsert).ExecuteCommandAsync();
            }
        }

        /// <summary>零污染清理(物理删,幂等):夹具段 + 动态新行。探针无用户/角色引用,无中间表面。</summary>
        private static async Task CleanupAsync(DbScope scope, long dynamicId = 0)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            var ids = new List<long> { PRoot, PChild, PGrand, PSib };
            if (dynamicId > 0)
            {
                ids.Add(dynamicId);
            }

            await scope.Db.Deleteable<SysOrg>()
                .Where(u => ids.Contains(u.Id)).ExecuteCommandAsync();
        }

        /// <summary>
        /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集,
        /// 解析 [Service] 的 SysOrgService 及其依赖 UserManager),InitTables 扫 Core 程序集增量建表(幂等)。
        /// 与 BatchBI DbScope 同构(同仓内重复,避免测试基建跨批耦合——各批测试自包含)。
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
                // 批 BP D4:与 Program.cs 装配面同口径补本地化注册——批 AL 时代 LocalizationService 贴 [Service]
                // 经自动发现兜底,批 BP 拆双注册归一显式单例后此容器失去该注册,
                // 控制器 BizError 出口(经 RequestServices 解析 ILocalizationService 渲染)会回退裸键。
                services.AddAdminLocalization();
                services.AddAutoServices(typeof(SysOrgService).Assembly);

                scope.Provider = services.BuildServiceProvider();
                scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
                TestSchemaBootstrap.Ensure(scope.Db);
                scope.PgReachable = true;
                return scope;
            }

            public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchAH/BI 口径,不阻塞等待)。</summary>
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
