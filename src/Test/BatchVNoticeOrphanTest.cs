using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Security;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Notice.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Security.Claims;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 V 回归:通知接收查询孤儿行防御(部署全量验证抓出的 pageReceived 500)。
    /// 场景:接收行(sys_notice_user)对应通知行被软删(存量/手工数据可产生孤儿)→
    /// LEFT JOIN 无匹配 → null 行进入结果集 → 非空枚举(NoticeTypeEnum)反序列化 500 / ToOutput(null) NRE。
    /// 修复口径:PageReceivedAsync/GetUnReadListAsync 查询加 n.Id != null 过滤孤儿行(通知已删 = 不可见,语义正确)。
    /// </summary>
    public class BatchVNoticeOrphanTest : IAsyncLifetime
    {
        // 探针用户(固定 Id,幂等重建;dsh_v 前缀防与存量撞名)
        private const long Publisher = 99_000_000_001_31;
        private const long Receiver = 99_000_000_001_30;
        private const string AccountPublisher = "dsh_v_notice_a";
        private const string AccountReceiver = "dsh_v_notice_r";

        // 探针公告标题前缀（夹具兜底清理按它无条件识别，见 DisposeAsync）
        private const string ProbeTitlePrefix = "批V孤儿探针";

        [Fact]
        public async Task 接收分页_孤儿接收行被过滤不抛异常()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeAsync(scope);

            // 发布者 A 建两条通知并定向发布给接收者 R
            scope.FakeAccessor.HttpContext = UserContext(Publisher, AccountPublisher, "777", "批V探针A");
            long keepId = 0, orphanId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                keepId = await svc.AddAsync(new AddNoticeInput("批V孤儿探针-保留", "c1", 1));
                orphanId = await svc.AddAsync(new AddNoticeInput("批V孤儿探针-孤儿化", "c2", 1));
                await svc.PublicAsync(new PublicNoticeInput(keepId, new List<long> { Receiver }));
                await svc.PublicAsync(new PublicNoticeInput(orphanId, new List<long> { Receiver }));
            }

            // 手工软删 orphanId(绕过服务层级联 → 接收行孤儿化;模拟存量/手工数据)
            await scope.Db.Updateable<SysNotice>()
                .SetColumns(n => n.IsDelete == true)
                .Where(n => n.Id == orphanId)
                .ExecuteCommandAsync();

            // 接收者 R 视角:分页不抛异常,且只含保留通知(孤儿行被过滤)
            scope.FakeAccessor.HttpContext = UserContext(Receiver, AccountReceiver, "777", "批V探针R");
            using var unit2 = NewUnit(scope);
            var svc2 = unit2.ServiceProvider.GetRequiredService<SysNoticeService>();
            var page = await svc2.PageReceivedAsync(new PageNoticeInput(null, 0, 1, 50));
            Assert.NotNull(page);
            Assert.DoesNotContain(page.List, x => x.Id == orphanId);
            Assert.Contains(page.List, x => x.Id == keepId);

            // 清理
            await CleanupAsync(scope, keepId, orphanId);
        }

        [Fact]
        public async Task 未读列表_孤儿接收行被过滤不抛异常()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeAsync(scope);

            scope.FakeAccessor.HttpContext = UserContext(Publisher, AccountPublisher, "777", "批V探针A");
            long keepId = 0, orphanId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                keepId = await svc.AddAsync(new AddNoticeInput("批V未读探针-保留", "c1", 1));
                orphanId = await svc.AddAsync(new AddNoticeInput("批V未读探针-孤儿化", "c2", 1));
                await svc.PublicAsync(new PublicNoticeInput(keepId, new List<long> { Receiver }));
                await svc.PublicAsync(new PublicNoticeInput(orphanId, new List<long> { Receiver }));
            }

            await scope.Db.Updateable<SysNotice>()
                .SetColumns(n => n.IsDelete == true)
                .Where(n => n.Id == orphanId)
                .ExecuteCommandAsync();

            scope.FakeAccessor.HttpContext = UserContext(Receiver, AccountReceiver, "777", "批V探针R");
            using var unit2 = NewUnit(scope);
            var svc2 = unit2.ServiceProvider.GetRequiredService<SysNoticeService>();
            var list = await svc2.GetUnReadListAsync();
            Assert.NotNull(list);
            Assert.DoesNotContain(list, x => x.Id == orphanId);
            Assert.Contains(list, x => x.Id == keepId);

            await CleanupAsync(scope, keepId, orphanId);
        }

        // ── 辅助 ─────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针用户幂等重建(固定 Id;Account 带 dsh_v 前缀防与存量撞名)。</summary>
        private static async Task EnsureProbeAsync(DbScope scope)
        {
            foreach (var (id, account, name) in new[]
            {
                (Publisher, AccountPublisher, "批V探针A"),
                (Receiver, AccountReceiver, "批V探针R")
            })
            {
                var exists = await scope.Db.Queryable<SysUser>().AnyAsync(u => u.Id == id);
                if (!exists)
                {
                    await scope.Db.Insertable(new SysUser
                    {
                        Id = id,
                        Account = account,
                        RealName = name,
                        Password = "probe-not-used",
                        Status = 0,
                        AccountType = 777,
                        TokenVersion = 1
                    }).ExecuteCommandAsync();
                }

                // 清孤儿接收行残留(用例幂等)
                await scope.Db.Deleteable<SysNoticeUser>()
                    .Where(u => u.UserId == Receiver)
                    .ExecuteCommandAsync();
            }
        }

        private static async Task CleanupAsync(DbScope scope, long keepId, long orphanId)
        {
            await scope.Db.Deleteable<SysNoticeUser>()
                .Where(u => u.UserId == Receiver && (u.NoticeId == keepId || u.NoticeId == orphanId))
                .ExecuteCommandAsync();
            await scope.Db.Deleteable<SysNotice>()
                .Where(n => n.Id == keepId || n.Id == orphanId)
                .ExecuteCommandAsync();
        }

        private static HttpContext UserContext(long userId, string account, string accountType, string realName)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.AccountType, accountType),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
        }

        /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计/租户填充读它)。</summary>
        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        /// <summary>DB 测试客户端(与 BatchG 同构:AddSqlSugar + AddCache + AddAutoServices + InitTables 幂等)。</summary>
        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

            public DbScope()
            {
                if (!IsPostgresReachable())
                {
                    return;
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
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysNotice).Assembly);
                services.AddSignalR();
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                TestSchemaBootstrap.Ensure(Db);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
            }

            private static string DbConnectionString =>
                TestPg.ConnectionString;

            private static bool IsPostgresReachable()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var result = client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2));
                    return result;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>夹具实现：本类清理在 <see cref="DisposeAsync"/> 里做，此处无准备动作。⚠ 接口成员名是 InitializeAsync——凭记忆写成 ImplAsync 会 CS0535（本仓编译门实测抓到）。</summary>
        public Task InitializeAsync() => Task.CompletedTask;

        /// <summary>
        /// 夹具级兜底清理（纵深期存量改造·t845 阶段 1 前置「给残留探针加清理句柄」）。
        /// **为什么必须有**：原清理 <see cref="CleanupAsync"/> 排在被测调用**之后** ⇒ 中途断言失败/抛异常即整段跳过，
        /// 而 <see cref="EnsureProbeAsync"/> 的入口自愈**只清 `sys_notice_user`、不清 `sys_notice` 主表行** ⇒
        /// 中断时公告主表行永久残留在产品表里（2026-10-27 实测：`sys_notice_user` 曾积 736 行孤儿即此族，
        /// 且因读面已过滤而无人报警）。⇒ 清理句柄放夹具里，用例体内不再依赖走到最后一行。
        /// 按**探针标题前缀**无条件清理（而非用入参 id）：既覆盖中断场景，也让以后新增用例自动受保护。
        /// </summary>
        public async Task DisposeAsync()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { return; }
            await CleanupResidueAsync(scope);
        }

        private static async Task CleanupResidueAsync(DbScope scope)
        {
            // ⚠ 必须 ClearFilter()：探针公告常被用例**软删**（孤儿场景正是靠软删制造的），
            //   若留着软删/租户全局过滤器，这里恰恰**查不到要清的那些行**。
            var ids = await scope.Db.Queryable<SysNotice>()
                .ClearFilter()
                .Where(n => n.Title!.StartsWith(ProbeTitlePrefix))
                .Select(n => n.Id)
                .ToListAsync();
            if (ids.Count == 0) { return; }
            // 先删引用方（接收行）再删主表行；两步式而非手写 LIKE/子查询——可读且不经方言差异
            await scope.Db.Deleteable<SysNoticeUser>().Where(u => ids.Contains(u.NoticeId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysNotice>().Where(n => ids.Contains(n.Id)).ExecuteCommandAsync();
        }
    }
}
