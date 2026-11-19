// 批 t847 租户隔离用例族·第二员:sys_notice_user(批 2 纳管)。
// 判据来源与形状照抄首员 src/Test/BatchT46TenantIsolationTest.cs(族内不另造机制):
//   前提＝不经租户过滤器的裸 SQL 断言"他租户 B 的行确实存在"(缺它负向会假绿);
//   正＝租户 A 非超管视角 → 该表的用户面列表只含 TenantId=A 的行;
//   负＝同视角按他租户 B 的行 id 直取 → 取不到(null / AppException 的 NotFound 码)。
//
// 前置查证结论(t846/t847 要求的"先查证再动手",2026-10-27 实读 src/Core + src/Web.Entry):
//   ① sys_notice_user 无「管理面」直读入口——SysNoticeController 的 5 个管理端点(page/add/update/delete/public)
//      作用对象都是 sys_notice 本体;本表**唯一**面向用户的读路径是自读面接收分页
//      SysNoticeService.PageReceivedAsync(端点 GET /api/sysNotice/pageReceived),以及
//      GetUnReadListAsync(未读列表)与 GetDetailAsync 内的收件人 IsAnyAsync 判定。
//      故本成员落在 PageReceivedAsync 这条真实读路径上(租户过滤器对 sys_notice_user 与其 LeftJoin 的
//      sys_notice 同时生效),并在注释里明确"入口性质＝自读面,不是管理面"——不把它冒充成管理面列表。
//   ② sys_user_otp **无任何**管理面/列表/详情读入口(全仓只有 AuthService 登录期按 UserId 直读
//      GetFirstAsync 与 SysUserService.DeleteAsync 的级联物理删),属"只被内部逻辑读写"⇒
//      按 brief 口径**不造成员**、不改生产代码:该表隔离性质无法在用例层证明(见批报告结论)。
//
// 探针纪律:通知标题唯一前缀 t47_iso_ + Guid;接收行按 (NoticeId, UserId) 归属本次探针;
//          IAsyncLifetime 兜底清理 + 用例内 finally 清理,结束时按前缀物理删除并断言残留=0。
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
using AdminCore.Core.Services.Notice.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Test
{
    public class BatchT47TenantIsolationTest : IAsyncLifetime
    {
        // 探针登记:用例失败后 DisposeAsync 仍执行(成功路径内联清理保留,幂等)
        private readonly List<Probe> _probes = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_probes.Count == 0)
            {
                return;
            }

            using var dbScope = new DbScope(_probes[0].ReaderId);
            if (!dbScope.PgReachable)
            {
                return;
            }

            foreach (var probe in _probes)
            {
                await CleanupAndAssertAsync(dbScope, probe);
            }
        }

        // 探针租户段(仅作过滤值/行归属值;独立号段,避开首员 99_000_000_046_001/002)
        private const long TenantA = 99_000_000_047_001L;
        private const long TenantB = 99_000_000_047_002L;

        // ─────────────────────────────────────────────────────────────
        // 前提(硬组成):不经租户过滤器,证明 TenantId=B 的接收行确实在库里
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_notice_user_租户隔离_裸查询证明他租户行确实存在()
        {
            var probe = NewProbe();
            using var dbScope = new DbScope(probe.ReaderId);
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var seeded = await SeedProbeAsync(dbScope, probe);
            try
            {
                // 裸 SQL 直查物理行(不经 SqlSugar QueryFilter:租户/软删过滤器均不参与)。
                // 这一条是判据的硬组成:正/负两侧都依赖"B 行真的存在",否则负向会假绿。
                var countB = await dbScope.Db.Ado.GetIntAsync(
                    "select count(*) from sys_notice_user where \"NoticeId\" = @nid and \"TenantId\" = @t and \"IsDelete\" = false",
                    new { nid = seeded.NoticeBId, t = TenantB });
                Assert.Equal(1, countB);

                // 同时钉住 A 行也在(两侧对照,避免只有 B 存在时正向断言误读)
                var countA = await dbScope.Db.Ado.GetIntAsync(
                    "select count(*) from sys_notice_user where \"NoticeId\" = @nid and \"TenantId\" = @t and \"IsDelete\" = false",
                    new { nid = seeded.NoticeAId, t = TenantA });
                Assert.Equal(1, countA);
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, probe);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 正:租户 A 视角 → 用户面接收分页(PageReceivedAsync)只含 TenantId=A 的探针行
        //     入口性质＝自读面(管理面 5 端点不直读本表,见文件头 ①)
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_notice_user_租户隔离_租户A视角接收分页只含本租户行()
        {
            var probe = NewProbe();
            using var dbScope = new DbScope(probe.ReaderId);
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var seeded = await SeedProbeAsync(dbScope, probe);
            try
            {
                using var diScope = dbScope.Provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var svc = sp.GetRequiredService<SysNoticeService>();
                var db = sp.GetRequiredService<ISqlSugarClient>();

                // 复现「租户 A 非超管」请求视角(PermissionMiddleware 同源装配:先清后设;finally 恢复)
                try
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                    db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == TenantA);

                    // 接收分页:同一 ReaderId 在 A/B 两租户各有接收行(前提已证存在),
                    // A 视角必须只回 A 的那一条;过滤失效时 Total 会变 2 ⇒ 断言直接翻红。
                    var page = await svc.PageReceivedAsync(new PageNoticeInput(probe.Marker));
                    var only = Assert.Single(page.List);
                    Assert.Equal(seeded.NoticeAId, only.Id);
                }
                finally
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                }
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, probe);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 负:同视角按他租户 B 的行 id 直取 → 取不到(仓储直取 null;服务面 by-id 入口抛 NotFound)
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_notice_user_租户隔离_按他租户B行id直取取不到()
        {
            var probe = NewProbe();
            using var dbScope = new DbScope(probe.ReaderId);
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var seeded = await SeedProbeAsync(dbScope, probe);
            Assert.True(seeded.RowBId > 0);
            try
            {
                using var diScope = dbScope.Provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var svc = sp.GetRequiredService<SysNoticeService>();
                var repo = sp.GetRequiredService<ISqlSugarRepository<SysNoticeUser>>();
                var db = sp.GetRequiredService<ISqlSugarClient>();

                try
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                    db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == TenantA);

                    // ① 仓储按 B 行物理 id 直取 → null(租户过滤器把 B 行挡在 A 视角之外)
                    var byId = await repo.GetFirstAsync(u => u.Id == seeded.RowBId);
                    Assert.Null(byId);

                    // ② 服务面 by-id 入口:GetDetailAsync 的收件人判定走 sys_notice_user
                    //    (IsAnyAsync(NoticeId==id && UserId==本人));A 视角按 B 的通知 id 直取必须 NotFound,
                    //    若过滤失效则命中的 B 接收行会让它静默返回 null(不抛)⇒ 本断言翻红。
                    var ex = await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(seeded.NoticeBId));
                    Assert.Equal(ErrorCodes.NoticeNotFound, ex.ErrorCode);
                }
                finally
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                }
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, probe);
            }
        }

        // ── 辅助 ─────────────────────────────────────────────────────────

        private Probe NewProbe()
        {
            // ReaderId 每次新取(号段内随机):接收分页按 UserId + Title 双圈,避免历史残留混入 Total
            var probe = new Probe(
                $"t47_iso_{Guid.NewGuid():N}",
                99_000_000_047_000L + Random.Shared.NextInt64(1, 1_000_000));
            _probes.Add(probe);
            return probe;
        }

        /// <summary>
        /// 造 A/B 两租户各一条通知 + 各一条接收行(同一 ReaderId、同一租户归属口径),
        /// 返回物理 id 供裸查询/直取使用。插入用单对象 Insertable(Ids 由 AOP 雪花填充,与生产写侧同路径)。
        /// </summary>
        private static async Task<(long RowAId, long RowBId, long NoticeAId, long NoticeBId)> SeedProbeAsync(
            DbScope scope, Probe probe)
        {
            var db = scope.Db;
            var noticeA = new SysNotice
            {
                Title = probe.Marker + "_na",
                Content = "t47 租户隔离探针",
                Status = NoticeStatusEnum.Public,
                PublicUserId = probe.ReaderId,
                PublicTime = DateTime.Now,
                TenantId = TenantA
            };
            var noticeB = new SysNotice
            {
                Title = probe.Marker + "_nb",
                Content = "t47 租户隔离探针",
                Status = NoticeStatusEnum.Public,
                PublicUserId = probe.ReaderId,
                PublicTime = DateTime.Now,
                TenantId = TenantB
            };
            await db.Insertable(noticeA).ExecuteCommandAsync();
            await db.Insertable(noticeB).ExecuteCommandAsync();

            var rowA = new SysNoticeUser
            {
                NoticeId = noticeA.Id,
                UserId = probe.ReaderId,
                ReadStatus = NoticeUserStatusEnum.Unread,
                TenantId = TenantA
            };
            var rowB = new SysNoticeUser
            {
                NoticeId = noticeB.Id,
                UserId = probe.ReaderId,
                ReadStatus = NoticeUserStatusEnum.Unread,
                TenantId = TenantB
            };
            await db.Insertable(rowA).ExecuteCommandAsync();
            await db.Insertable(rowB).ExecuteCommandAsync();

            return (rowA.Id, rowB.Id, noticeA.Id, noticeB.Id);
        }

        /// <summary>按唯一前缀/ReaderId 物理删除探针行,并断言行数归零(撞唯一索引事故史的收尾纪律)。</summary>
        private static async Task CleanupAndAssertAsync(DbScope scope, Probe probe)
        {
            var db = scope.Db;
            // 先删接收行(按通知标题前缀归属),再按 ReaderId 兜底清孤儿接收行,最后删通知行
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_notice_user where \"NoticeId\" in (select id from sys_notice where \"Title\" like @p)",
                new { p = probe.Marker + "%" });
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_notice_user where \"UserId\" = @u", new { u = probe.ReaderId });
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_notice where \"Title\" like @p", new { p = probe.Marker + "%" });

            var leftRows = await db.Ado.GetIntAsync(
                "select count(*) from sys_notice_user where \"UserId\" = @u", new { u = probe.ReaderId });
            Assert.Equal(0, leftRows);

            var leftNotices = await db.Ado.GetIntAsync(
                "select count(*) from sys_notice where \"Title\" like @p", new { p = probe.Marker + "%" });
            Assert.Equal(0, leftNotices);
        }

        /// <summary>本次探针身份:标题唯一前缀 + 接收人 Id(接收分页按 UserId 圈定,故每次新取)。</summary>
        private sealed record Probe(string Marker, long ReaderId);

        /// <summary>
        /// DB 作用域:与生产同一条 AddSqlSugar/AddCache/AddSignalR/AddAutoServices 装配(仓储/AOP/软删全生效),
        /// 形状照抄 BatchAFUpgradeNoticeTest(SysNoticeService 全依赖链)＋首员 BatchT46 的降级探针纪律。
        /// 挂伪造 IHttpContextAccessor:UserManager 以 Claim 实时取 UserId/租户,AOP 审计/租户写入同源。
        /// </summary>
        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

            public DbScope(long readerId)
            {
                if (!IsPostgresReachable())
                {
                    return;
                }

                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = TestPg.ConnectionString,
                        ["Yit:WorkerId"] = "1"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                // 注册须先于 AddSqlSugar(其内部 TryAdd):AOP 审计/租户填充与 UserManager 都读它
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddSignalR(); // 通知服务链路依赖(SysOnlineUserService 的 IHubContext)
                services.AddAutoServices(typeof(SysNotice).Assembly);

                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                FakeAccessor.HttpContext = ReaderContext(readerId);
                TestSchemaBootstrap.Ensure(Db);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
                (Db as IDisposable)?.Dispose();
            }

            /// <summary>模拟登录上下文(UserId/Account/AccountType/TenantId Claims,与生产 JwtClaimNames 同键)。</summary>
            private static HttpContext ReaderContext(long readerId)
            {
                var context = new DefaultHttpContext();
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(JwtClaimNames.UserId, readerId.ToString()),
                    new Claim(JwtClaimNames.Account, "t47_probe_reader"),
                    new Claim(JwtClaimNames.RealName, "探针通知读者"),
                    new Claim(JwtClaimNames.AccountType, "777"), // 777 = 普通用户(非超管),与首员的非超管口径一致
                    new Claim(JwtClaimNames.TenantId, TenantA.ToString())
                }, "test"));
                return context;
            }

            private static bool IsPostgresReachable()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    // 同步 Connect(回环地址上有监听即连上、无监听立刻 ECONNREFUSED),构造期不引入 sync-over-async
                    client.Connect(DbHost, DbPort);
                    return client.Connected;
                }
                catch
                {
                    return false;
                }
            }

            private const string DbHost = "127.0.0.1";
            private const int DbPort = 5432;
        }

        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}