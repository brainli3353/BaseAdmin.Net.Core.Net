using AdminCore.Core;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Extensions;
using AdminCore.Core.Hub;
using AdminCore.Core.Services.Message;
using AdminCore.Core.Services.Message.Dto;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Services.OnlineUser.Dto;
using AdminCore.Core.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace AdminCore.Test;

/// <summary>
/// 批 AV「公告/站内消息跨实例推送」验证件:被测主体 =
/// ① 订阅侧 <see cref="OnlinePushSubscriber"/>(kind 分派:公告逐连接 / 消息广播 / 消息逐连接;自跳;脏载荷不抛);
/// ② 发布侧三处消费点(<see cref="SysOnlineUserService.PublicNoticeAsync"/>、
/// <see cref="SysMessageService.SendAllUserAsync"/>、<see cref="SysMessageService.SendUserAsync"/>)各自**广播一次**;
/// ③ 常量与内建清单(<see cref="EventBusEvents"/>)逐字钉住。
/// </summary>
/// <remarks>
/// WHY 用 fake:要验的是**编排与载荷**(类型标签/目标连接/广播标志/调用次数);
/// "跨实例真的送达"由双实例 e2e 承担(见缺口清单历史行)。装置沿用批 AU 口径:
/// PG 容器(AddSqlSugar/AddProjectOptions/AddCache/AddAutoServices/AddSignalR)＋**末位覆盖注册** fake。
/// 发布侧用例依赖真实 PG(容器解析 SysConfigService 需仓储),PG 不可达时 Assert.Fail 降级声明。
/// </remarks>
public class BatchAVOnlinePushTest
{
    // ── ① 订阅侧:公告逐连接 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 订阅侧_公告_逐连接投递()
    {
        var hub = new FakeHub();
        var sub = NewSubscriber(hub);

        var notice = NewNotice(101);
        await sub.HandleOnlinePushAsync(new OnlinePushPayload(
            "other-node:1:abc", OnlinePushKinds.Notice, new List<string> { "av-c1", "av-c2" },
            BroadcastAll: false, Notice: notice, Message: null));

        Assert.Equal(2, hub.NoticeCalls.Count);
        Assert.Contains(hub.NoticeCalls, c => c.Target == "av-c1" && c.Payload == "101");
        Assert.Contains(hub.NoticeCalls, c => c.Target == "av-c2" && c.Payload == "101");
        Assert.Empty(hub.MessageCalls);
    }

    // ── ② 订阅侧:消息广播(All) ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 订阅侧_消息广播_推全部连接()
    {
        var hub = new FakeHub();
        var sub = NewSubscriber(hub);

        await sub.HandleOnlinePushAsync(new OnlinePushPayload(
            "other-node:1:abc", OnlinePushKinds.Message, ConnectionIds: null,
            BroadcastAll: true, Notice: null, Message: "av-broadcast"));

        var call = Assert.Single(hub.MessageCalls);
        Assert.Equal("ALL", call.Target);
        Assert.Equal("av-broadcast", call.Payload);
        Assert.Empty(hub.NoticeCalls);
    }

    // ── ③ 订阅侧:消息逐连接 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 订阅侧_消息单播_逐连接投递()
    {
        var hub = new FakeHub();
        var sub = NewSubscriber(hub);

        await sub.HandleOnlinePushAsync(new OnlinePushPayload(
            "other-node:1:abc", OnlinePushKinds.Message, new List<string> { "av-conn-9" },
            BroadcastAll: false, Notice: null, Message: "av-direct"));

        var call = Assert.Single(hub.MessageCalls);
        Assert.Equal("av-conn-9", call.Target);
        Assert.Equal("av-direct", call.Payload);
    }

    // ── ④ 订阅侧:自源事件零投递(防"本地直推＋订阅重推"双推) ──────────────────────────────
    [Fact]
    public async Task 订阅侧_自源事件_零投递()
    {
        var hub = new FakeHub();
        var sub = NewSubscriber(hub);
        var self = InstanceIdentity.Current.InstanceId;

        await sub.HandleOnlinePushAsync(new OnlinePushPayload(
            self, OnlinePushKinds.Notice, new List<string> { "av-c1" }, false, NewNotice(1), null));
        await sub.HandleOnlinePushAsync(new OnlinePushPayload(
            self, OnlinePushKinds.Message, null, false, null, "x"));

        Assert.Empty(hub.NoticeCalls);
        Assert.Empty(hub.MessageCalls);
    }

    // ── ⑤ 订阅侧:脏载荷不抛不投递(不进重试) ───────────────────────────────────────────────
    [Fact]
    public async Task 订阅侧_脏载荷_不抛不投递()
    {
        var hub = new FakeHub();
        var sub = NewSubscriber(hub);

        await sub.HandleOnlinePushAsync(null!);                                                   // 空载荷
        await sub.HandleOnlinePushAsync(new OnlinePushPayload("o", "", null, false, null, null));  // 无类型
        await sub.HandleOnlinePushAsync(new OnlinePushPayload("o", "weird-kind", null, false, null, null)); // 未知类型
        await sub.HandleOnlinePushAsync(new OnlinePushPayload("o", OnlinePushKinds.Notice, new List<string> { "c" }, false, null, null)); // 缺 Notice
        await sub.HandleOnlinePushAsync(new OnlinePushPayload("o", OnlinePushKinds.Message, new List<string> { "c" }, false, null, null)); // 缺 Message

        Assert.Empty(hub.NoticeCalls);
        Assert.Empty(hub.MessageCalls);
    }

    // ── ⑥ 常量与内建清单逐字(部署白名单键必须与代码一致) ───────────────────────────────────
    [Fact]
    public void 常量与内建清单_逐字()
    {
        Assert.Equal("online_push", EventBusEvents.OnlinePush);
        Assert.Equal("notice", OnlinePushKinds.Notice);
        Assert.Equal("message", OnlinePushKinds.Message);

        // 内建清单:守卫与文档同源 —— 三个事件都在,且标签可检索
        // (批 BC 加入第三个"sqlsugar_cache_invalidate";清单规模随新增内建事件同批更新,勿把规模钉死成历史值)
        Assert.Equal(3, EventBusEvents.CrossInstanceBuiltIns.Count);
        Assert.True(EventBusEvents.CrossInstanceBuiltIns.ContainsKey(EventBusEvents.OnlineForceOffline));
        Assert.True(EventBusEvents.CrossInstanceBuiltIns.ContainsKey(EventBusEvents.OnlinePush));
        Assert.True(EventBusEvents.CrossInstanceBuiltIns.ContainsKey(EventBusEvents.CacheInvalidate));
        Assert.Equal("强踢", EventBusEvents.CrossInstanceBuiltIns[EventBusEvents.OnlineForceOffline]);
        Assert.Equal("在线推送", EventBusEvents.CrossInstanceBuiltIns[EventBusEvents.OnlinePush]);
        Assert.Equal("二级缓存失效", EventBusEvents.CrossInstanceBuiltIns[EventBusEvents.CacheInvalidate]);
    }

    // ── ⑦ 发布侧:三处各自广播一次(kind/目标/广播标志正确) ─────────────────────────────────
    [Fact]
    public async Task 发布侧_三处各自广播一次()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var pub = (FakePublisher)scope.Provider.GetRequiredService<IEventPublisher>();
        var cache = unit.ServiceProvider.GetRequiredService<ICacheService>();
        var onlineSvc = unit.ServiceProvider.GetRequiredService<SysOnlineUserService>();
        var msgSvc = unit.ServiceProvider.GetRequiredService<SysMessageService>();

        const long uid = 9_000_000_000_002;
        const string cid = "av-conn-publish";
        await cache.HashSetAsync(CacheConst.KeyUserOnline, cid, new OnlineUserInfo
        {
            ConnectionId = cid, UserId = uid, Account = "av_probe", RealName = "AV 探针", TenantId = SqlSugarConst.DefaultTenantId
        });

        // ① 公告:本地逐连接 + 广播一次(kind=notice,目标含该连接)
        await onlineSvc.PublicNoticeAsync(NewNotice(202), new List<long> { uid });
        var noticeEvt = Assert.Single(pub.Published, e => e.EventName == EventBusEvents.OnlinePush);
        var noticePayload = Assert.IsType<OnlinePushPayload>(noticeEvt.Payload);
        Assert.Equal(OnlinePushKinds.Notice, noticePayload.Kind);
        Assert.Equal(InstanceIdentity.Current.InstanceId, noticePayload.SourceInstanceId);
        Assert.Contains(cid, noticePayload.ConnectionIds!);
        Assert.Equal(202, noticePayload.Notice!.Id);

        // ② 消息广播:BroadcastAll=true
        pub.Published.Clear();
        await msgSvc.SendAllUserAsync(new MessageInput(0, "av", "广播正文", uid, new List<long>()));
        var allEvt = Assert.Single(pub.Published, e => e.EventName == EventBusEvents.OnlinePush);
        var allPayload = Assert.IsType<OnlinePushPayload>(allEvt.Payload);
        Assert.Equal(OnlinePushKinds.Message, allPayload.Kind);
        Assert.True(allPayload.BroadcastAll);
        Assert.Null(allPayload.ConnectionIds);
        Assert.NotNull(allPayload.Message);

        // ③ 消息单播:BroadcastAll=false,目标含该连接(且 msg_log 事件仍照旧发出——回归口径不破)
        pub.Published.Clear();
        await msgSvc.SendUserAsync(new MessageInput(0, "av", "单播正文", uid, new List<long> { uid }));
        var oneEvt = Assert.Single(pub.Published, e => e.EventName == EventBusEvents.OnlinePush);
        var onePayload = Assert.IsType<OnlinePushPayload>(oneEvt.Payload);
        Assert.Equal(OnlinePushKinds.Message, onePayload.Kind);
        Assert.False(onePayload.BroadcastAll);
        Assert.Contains(cid, onePayload.ConnectionIds!);
        Assert.Contains(pub.Published, e => e.EventName == "msg_log");
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    private static OnlinePushSubscriber NewSubscriber(FakeHub hub)
        => new(hub, NullLogger<OnlinePushSubscriber>.Instance);

    private static SysNotice NewNotice(long id) => new()
    {
        Id = id, Title = $"av-notice-{id}", Content = "正文", TenantId = SqlSugarConst.DefaultTenantId
    };

    private sealed class FakePublisher : IEventPublisher
    {
        public List<(string EventName, object? Payload)> Published { get; } = new();

        public Task PublishAsync<T>(string eventName, T payload)
        {
            lock (Published)
            {
                Published.Add((eventName, payload));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>记录型 Hub:公告/消息各记 (目标, 载荷标记);All 记目标 "ALL"。</summary>
    private sealed class FakeHub : IHubContext<OnlineUserHub, IOnlineUserHub>
    {
        public List<(string Target, string Payload)> NoticeCalls { get; } = new();

        public List<(string Target, string Payload)> MessageCalls { get; } = new();

        public IHubClients<IOnlineUserHub> Clients { get; }

        public IGroupManager Groups { get; } = new NoopGroupManager();

        public FakeHub() => Clients = new FakeClients(this);
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeClients : IHubClients<IOnlineUserHub>
    {
        private readonly FakeHub _hub;

        public FakeClients(FakeHub hub) => _hub = hub;

        public IOnlineUserHub All => new RecordingClient(_hub, "ALL");

        public IOnlineUserHub AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(_hub, "ALL-EXCEPT");

        public IOnlineUserHub Client(string connectionId) => new RecordingClient(_hub, connectionId);

        public IOnlineUserHub Clients(IReadOnlyList<string> connectionIds) => new RecordingClient(_hub, "CLIENTS");

        public IOnlineUserHub Group(string groupName) => new RecordingClient(_hub, "GROUP");

        public IOnlineUserHub GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(_hub, "GROUP-EXCEPT");

        public IOnlineUserHub Groups(IReadOnlyList<string> groupNames) => new RecordingClient(_hub, "GROUPS");

        public IOnlineUserHub User(string userId) => new RecordingClient(_hub, "USER");

        public IOnlineUserHub Users(IReadOnlyList<string> userIds) => new RecordingClient(_hub, "USERS");
    }

    private sealed class RecordingClient : IOnlineUserHub
    {
        private readonly FakeHub _hub;
        private readonly string _target;

        public RecordingClient(FakeHub hub, string target)
        {
            _hub = hub;
            _target = target;
        }

        public Task PublicNotice(SysNotice notice)
        {
            lock (_hub.NoticeCalls)
            {
                _hub.NoticeCalls.Add((_target, notice.Id.ToString()));
            }

            return Task.CompletedTask;
        }

        public Task ReceiveMessage(object context)
        {
            lock (_hub.MessageCalls)
            {
                _hub.MessageCalls.Add((_target, context?.ToString() ?? string.Empty));
            }

            return Task.CompletedTask;
        }

        public Task OnlineUserList(OnlineUserListMessage message) => Task.CompletedTask;

        public Task ForceOffline(string message) => Task.CompletedTask;
    }

    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

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
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysUser).Assembly);
            services.AddSignalR();
            services.AddSingleton<IEventPublisher>(new FakePublisher());
            services.AddSingleton<IHubContext<OnlineUserHub, IOnlineUserHub>>(new FakeHub());

            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;
        }

        public void Dispose() => _provider?.Dispose();

        private static string DbConnectionString =>
            TestPg.ConnectionString;

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                return client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}