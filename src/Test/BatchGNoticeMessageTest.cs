using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Message;
using AdminCore.Core.Services.Message.Dto;
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
    /// 批 G(4.7 通知公告 / 4.8 站内消息)测试。
    /// 覆盖:通知全链路(建草稿＋发布人快照/发布置态＋接收记录/已读态/接收分页/非发布人禁改禁删/已发布禁删/
    /// 重发幂等/非超管仅本人分页)/消息纯推送语义(接收人全离线静默不落日志/入参校验)/
    /// MsgLogSubscriber 落库路径(直接驱动,隔离总线时序)。
    /// 边界披露(同批 F):SignalR 推送端(发布广播/单播)经 FakeHubContext 无连接 no-op 覆盖调用链,
    /// 真实 WebSocket 推送经批末手工端到端验证后呈报。
    /// </summary>
    public class BatchGNoticeMessageTest : IAsyncLifetime
    {
        // 探针用户(固定 Id,幂等重建;Account 带 dsh_g 前缀防与存量撞名)
        private const long UserA = 99_000_000_001_01; // 发布者(普通账号 777)
        private const long UserB = 99_000_000_001_02; // 接收者(普通账号 777)
        private const string AccountA = "dsh_g_notice_a";
        private const string AccountB = "dsh_g_notice_b";

        // ── 4.7 通知公告 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 通知_新建草稿与发布人快照()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeUsersAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();

            var id = await svc.AddAsync(new AddNoticeInput("批 G 草稿测试通知", "内容正文", 1));
            Assert.True(id > 0);

            var detail = await svc.GetDetailAsync(id);
            Assert.NotNull(detail);
            Assert.Equal("批 G 草稿测试通知", detail!.Title);
            Assert.Equal(0, detail.Status); // 草稿
            Assert.Equal((int)NoticeTypeEnum.Notice, detail.Type);
            Assert.Equal(UserA, detail.PublicUserId);
            Assert.Equal("探针甲", detail.PublicUserName); // 发布人姓名快照
            Assert.Null(detail.PublicTime); // 未发布

            // 清理
            await CleanupNoticeAsync(scope, id);
        }

        [Fact]
        public async Task 通知_发布置态与接收记录与已读闭环()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeUsersAsync(scope);

            // A 建并定向发布给 B
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            long noticeId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                noticeId = await svc.AddAsync(new AddNoticeInput("批 G 发布测试通知", "发布正文", 1));
                await svc.PublicAsync(new PublicNoticeInput(noticeId, new List<long> { UserB }));
            }

            var db = scope.Db;
            var notice = db.Queryable<SysNotice>().Where(u => u.Id == noticeId).First();
            Assert.Equal((int)NoticeStatusEnum.Public, (int)notice!.Status);
            Assert.NotNull(notice.PublicTime);

            var receiveRow = db.Queryable<SysNoticeUser>().First(u => u.NoticeId == noticeId && u.UserId == UserB);
            Assert.NotNull(receiveRow);
            Assert.Equal((int)NoticeUserStatusEnum.Unread, (int)receiveRow!.ReadStatus);

            // B 接收分页 + 未读 + 已读闭环
            scope.FakeAccessor.HttpContext = UserContext(UserB, AccountB, "777", "探针乙");
            using var unit2 = NewUnit(scope);
            var svc2 = unit2.ServiceProvider.GetRequiredService<SysNoticeService>();

            var received = await svc2.PageReceivedAsync(new PageNoticeInput(null, 0, 1, 10));
            Assert.Contains(received.List, r => r.Id == noticeId && r.Title == "批 G 发布测试通知");

            var unread = await svc2.GetUnReadListAsync();
            Assert.Contains(unread, n => n.Id == noticeId);

            await svc2.SetReadAsync(noticeId);
            unread = await svc2.GetUnReadListAsync();
            Assert.DoesNotContain(unread, n => n.Id == noticeId);

            var afterRead = db.Queryable<SysNoticeUser>().First(u => u.NoticeId == noticeId && u.UserId == UserB);
            Assert.Equal((int)NoticeUserStatusEnum.Read, (int)afterRead!.ReadStatus);
            Assert.NotNull(afterRead.ReadTime);

            await CleanupNoticeAsync(scope, noticeId);
        }

        [Fact]
        public async Task 通知_非发布人禁改禁删()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeUsersAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            long noticeId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                noticeId = await svc.AddAsync(new AddNoticeInput("批 G 越权测试", "x", 1));
            }

            // B 对 A 的通知改/删 → AppException(仅发布人本人)
            scope.FakeAccessor.HttpContext = UserContext(UserB, AccountB, "777", "探针乙");
            using var unit2 = NewUnit(scope);
            var svc2 = unit2.ServiceProvider.GetRequiredService<SysNoticeService>();
            await Assert.ThrowsAsync<AppException>(() => svc2.UpdateAsync(new UpdateNoticeInput(noticeId, "B 改标题", null, null)));
            await Assert.ThrowsAsync<AppException>(() => svc2.DeleteAsync(noticeId));

            // 不存在的 Id → 通用提示
            await Assert.ThrowsAsync<AppException>(() => svc2.UpdateAsync(new UpdateNoticeInput(999_999_999_999, "t", null, null)));

            await CleanupNoticeAsync(scope, noticeId);
        }

        [Fact]
        public async Task 通知_已发布禁删与重发幂等()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeUsersAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            long noticeId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                noticeId = await svc.AddAsync(new AddNoticeInput("批 G 禁删测试", "x", 1));
                await svc.PublicAsync(new PublicNoticeInput(noticeId, new List<long> { UserB }));

                // 已发布禁删
                await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(noticeId));

                // 重发(同接收人集)幂等:接收行数保持 1
                await svc.PublicAsync(new PublicNoticeInput(noticeId, new List<long> { UserB }));
                var count = scope.Db.Queryable<SysNoticeUser>().Count(u => u.NoticeId == noticeId);
                Assert.Equal(1, count);
            }

            await CleanupNoticeAsync(scope, noticeId);
        }

        [Fact]
        public async Task 通知_非超管分页仅本人发布()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeUsersAsync(scope);

            // A 与 B 各建一条草稿(测试不启用租户读过滤,分页域=全库;非超管仅本人=CreateUserId 过滤)
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            long aId = 0, bId = 0;
            using (var unit = NewUnit(scope))
            {
                var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
                aId = await svc.AddAsync(new AddNoticeInput("A 的草稿", "x", 1));
            }

            scope.FakeAccessor.HttpContext = UserContext(UserB, AccountB, "777", "探针乙");
            using (var unit2 = NewUnit(scope))
            {
                var svc2 = unit2.ServiceProvider.GetRequiredService<SysNoticeService>();
                bId = await svc2.AddAsync(new AddNoticeInput("B 的草稿", "x", 1));

                // B 的分页只含 B 的(不含 A 的)
                var page = await svc2.PageAsync(new PageNoticeInput(null, 0, 1, 50));
                Assert.Contains(page.List, n => n.Id == bId);
                Assert.DoesNotContain(page.List, n => n.Id == aId);
            }

            // 超管分页全量
            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "999", "探针甲");
            using var unit3 = NewUnit(scope);
            var svc3 = unit3.ServiceProvider.GetRequiredService<SysNoticeService>();
            var adminPage = await svc3.PageAsync(new PageNoticeInput(null, 0, 1, 50));
            Assert.Contains(adminPage.List, n => n.Id == aId);
            Assert.Contains(adminPage.List, n => n.Id == bId);

            await CleanupNoticeAsync(scope, aId);
            await CleanupNoticeAsync(scope, bId);
        }

        // ── 4.8 站内消息 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 消息_接收人全离线静默不落日志()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMessageService>();

            // 探针用户均无在线连接(缓存 Hash 无 field)→ 静默返回,不推送不落日志
            await svc.SendUserAsync(new MessageInput(0, "离线测试", "消息正文", UserA, new List<long> { UserB }));

            var count = scope.Db.Queryable<SysLogMsg>().Count(u => u.ReceiveUserId == UserB && u.Title == "离线测试");
            Assert.Equal(0, count);
        }

        [Fact]
        public async Task 消息_入参校验()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysMessageService>();

            await Assert.ThrowsAsync<AppException>(() => svc.SendUserAsync(new MessageInput(0, "t", "  ", UserA, new List<long> { UserB })));
            await Assert.ThrowsAsync<AppException>(() => svc.SendUserAsync(new MessageInput(0, "t", "m", UserA, new List<long>())));
            await Assert.ThrowsAsync<AppException>(() => svc.SendAllUserAsync(new MessageInput(0, "t", string.Empty, UserA, new List<long>())));
        }

        [Fact]
        public async Task 消息日志_订阅者落库()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            scope.FakeAccessor.HttpContext = UserContext(UserA, AccountA, "777", "探针甲");
            using var unit = NewUnit(scope);
            var subscriber = unit.ServiceProvider.GetRequiredService<MsgLogSubscriber>();

            var log = new SysLogMsg
            {
                MessageType = "Info",
                Title = "批 G 落库测试",
                Message = "日志正文",
                ReceiveUserId = UserB,
                ReceiveUserName = "探针乙",
                SendUserId = UserA,
                SendUserName = "探针甲",
                SendTime = DateTime.Now
            };
            await subscriber.HandleMsgLogAsync(log);
            Assert.True(log.Id > 0);

            var saved = scope.Db.Queryable<SysLogMsg>().First(u => u.Id == log.Id);
            Assert.NotNull(saved);
            Assert.Equal("批 G 落库测试", saved!.Title);
            Assert.Equal(UserB, saved.ReceiveUserId);
            Assert.Equal(SqlSugarConst.DefaultTenantId, saved.TenantId); // 写层 AOP 填租户

            await scope.Db.Deleteable<SysLogMsg>().Where(u => u.Id == log.Id).ExecuteCommandAsync();
        }

        // ── 基础设施 ─────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针用户幂等重建(固定 Id;RealName=探针甲/乙 供发布人姓名快照断言)。</summary>
        private static async Task EnsureProbeUsersAsync(DbScope scope)
        {
            var db = scope.Db;
            foreach (var (id, account, name) in new[] { (UserA, AccountA, "探针甲"), (UserB, AccountB, "探针乙") })
            {
                var exists = await db.Queryable<SysUser>().AnyAsync(u => u.Id == id);
                if (!exists)
                {
                    await db.Insertable(new SysUser
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
            }
        }

        /// <summary>清理探针通知(软删通知＋物理删接收行),幂等。</summary>
        private static async Task CleanupNoticeAsync(DbScope scope, long noticeId)
        {
            if (noticeId <= 0)
            {
                return;
            }

            var db = scope.Db;
            await db.Deleteable<SysNoticeUser>().Where(u => u.NoticeId == noticeId).ExecuteCommandAsync();
            await db.Deleteable<SysNotice>().Where(u => u.Id == noticeId).ExecuteCommandAsync();
        }

        // 探针公告标题前缀（多数用例用它）；"A 的草稿"/"B 的草稿" 两条例外用例没有该前缀，
        // 故清理条件再并上探针固定用户 Id（创建人），见 CleanupResidueAsync
        private const string ProbeTitlePrefix = "批 G";

        /// <summary>夹具实现：无准备动作（⚠ 接口成员名是 InitializeAsync，写成 ImplAsync 会 CS0535）。</summary>
        public Task InitializeAsync() => Task.CompletedTask;

        /// <summary>
        /// 夹具级兜底清理（会写库测试的清理句柄统一放夹具里）。
        /// **为什么必须有**：原清理 <see cref="CleanupNoticeAsync"/> 逐条排在各用例**末尾**、且要用例把 id 传进来
        /// ⇒ 中途断言失败/抛异常（本类大量 Assert.ThrowsAsync/分页断言）就整段跳过，
        /// 而 sys_notice（公告主表）与 sys_log_msg 都是**产品表**，探针行会永久残留。
        /// ⇒ 清理句柄放夹具里，用例体内不再依赖走到最后一行；按**探针自身特征**无条件识别，不靠入参 id。
        /// </summary>
        public async Task DisposeAsync()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { return; }
            await CleanupResidueAsync(scope);
        }

        private static async Task CleanupResidueAsync(DbScope scope)
        {
            var db = scope.Db;

            // ⚠ 必须 ClearFilter()：探针公告可能被用例软删（禁改禁删/已发布禁删等路径也会留下半态行），
            //   带软删全局过滤器就恰恰查不到要清的那些行。
            var ids = await db.Queryable<SysNotice>()
                .ClearFilter()
                .Where(n => n.Title.StartsWith(ProbeTitlePrefix)
                            || n.CreateUserId == UserA
                            || n.CreateUserId == UserB)
                .Select(n => n.Id)
                .ToListAsync();
            if (ids.Count > 0)
            {
                // 先删引用方（接收行）再删主表行；两步式而非手写 LIKE/子查询
                await db.Deleteable<SysNoticeUser>().Where(u => ids.Contains(u.NoticeId)).ExecuteCommandAsync();
                await db.Deleteable<SysNotice>().Where(n => ids.Contains(n.Id)).ExecuteCommandAsync();
            }

            // 消息日志：按探针固定收发方识别（这两个 Id 为本类独有探针），
            // 中断时"批 G 落库测试"那行原先只在用例末尾删 ⇒ 会永久残留。
            await db.Deleteable<SysLogMsg>()
                .Where(u => u.ReceiveUserId == UserB || u.SendUserId == UserA)
                .ExecuteCommandAsync();
        }

        /// <summary>构造模拟登录上下文(UserId/Account/AccountType/TenantId Claims,与生产 JwtClaimNames 同键)。</summary>
        private static HttpContext UserContext(long userId, string account, string accountType, string realName)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, account),
                new Claim(JwtClaimNames.RealName, realName),
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

        /// <summary>
        /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddSignalR + AddAutoServices(Core 程序集),
        /// 框架表增量建(InitTables 扫 Core 程序集 → sys_notice/sys_notice_user/sys_log_msg 缺表即建,幂等)。
        /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
        /// </summary>
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
                // 批 G:SignalR 注册 IHubContext(生产同装配);测试无连接时推送 no-op
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
    }
}
