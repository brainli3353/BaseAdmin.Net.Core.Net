using AdminCore.Core;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Extensions;
using AdminCore.Core.Hub;
using AdminCore.Core.Services.Config;
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
/// 批 AU「跨实例强踢」验证件:被测主体 =
/// ① 发布侧 <see cref="SysOnlineUserService.ForceOfflineAsync"/>(本地直推＋删 Hash＋**追加跨实例广播**);
/// ② 订阅侧 <see cref="OnlineForceOfflineSubscriber"/>(收到他实例事件 ⇒ 推本机连接;自源跳过;空连接不抛)。
/// </summary>
/// <remarks>
/// WHY 用 fake 而非真实 EventBus/真连接:要验的是**编排与载荷**(事件名/来源/连接号/调用次数),
/// 真总线的"本地投递＋白名单跨实例"属批 AC 已验证面;真连接投递由 F8 双实例 e2e 承担(见缺口清单历史行)。
/// 装置复用批 F(同款 DI 容器:AddSqlSugar/AddProjectOptions/AddCache/AddAutoServices/AddSignalR),
/// 只在最末**覆盖注册** fake IEventPublisher 与 fake IHubContext(MS.DI 后注册者胜)⇒ 断言可观察。
/// ① 依赖真实 PG(容器解析 SysConfigService 需仓储);PG 不可达时 Assert.Fail 降级声明,禁止冒充通过。
/// </remarks>
public class BatchAUOnlinePushTest
{
    // ── ① 发布侧:本地直推一次 + 删 Hash + 广播一次(事件名/来源/连接号正确) ──────────────────
    [Fact]
    public async Task 发布侧_强踢_本地直推与跨实例广播各一次()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var unit = scope.Provider.CreateScope();
        var hub = (FakeHub)scope.Provider.GetRequiredService<IHubContext<OnlineUserHub, IOnlineUserHub>>();
        var pub = (FakePublisher)scope.Provider.GetRequiredService<IEventPublisher>();
        var cache = unit.ServiceProvider.GetRequiredService<ICacheService>();
        var svc = unit.ServiceProvider.GetRequiredService<SysOnlineUserService>();

        const string cid = "au-conn-1";
        await cache.HashSetAsync(CacheConst.KeyUserOnline, cid, new OnlineUserInfo
        {
            ConnectionId = cid, UserId = 9_000_000_000_001, Account = "au_probe", RealName = "AU 探针", TenantId = SqlSugarConst.DefaultTenantId
        });

        await svc.ForceOfflineAsync(cid);

        // 本地直推(D2:保留本地直推,单实例语义与批 F 零回归)
        Assert.Contains(hub.ForceOfflineCalls, c => c.ConnectionId == cid);
        // 服务端判定:Hash 字段已删(客户端收不到也不残留)
        Assert.Null(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, cid));
        // 跨实例广播:事件名走常量单点 + 载荷三字段正确
        var evt = Assert.Single(pub.Published);
        Assert.Equal(EventBusEvents.OnlineForceOffline, evt.EventName);
        var payload = Assert.IsType<OnlineForceOfflinePayload>(evt.Payload);
        Assert.Equal(cid, payload.ConnectionId);
        Assert.Equal(InstanceIdentity.Current.InstanceId, payload.SourceInstanceId);
        Assert.Equal("强制下线", payload.Reason);
    }

    // ── ② 订阅侧:他实例的事件 ⇒ 推本机连接(连接号原样透传) ─────────────────────────────────
    [Fact]
    public async Task 订阅侧_他实例事件_向本机连接推送()
    {
        var hub = new FakeHub();
        var sub = new OnlineForceOfflineSubscriber(hub, NullLogger<OnlineForceOfflineSubscriber>.Instance);

        await sub.HandleOnlineForceOfflineAsync(new OnlineForceOfflinePayload("au-conn-remote", "other-node:1:abc", "强制下线"));

        var call = Assert.Single(hub.ForceOfflineCalls);
        Assert.Equal("au-conn-remote", call.ConnectionId);
        Assert.Equal("强制下线", call.Message);
    }

    // ── ③ 自源跳过:来源＝本实例 ⇒ 零推送(防"本地直推＋订阅重推"双推) ───────────────────────
    [Fact]
    public async Task 订阅侧_自源事件_跳过不推()
    {
        var hub = new FakeHub();
        var sub = new OnlineForceOfflineSubscriber(hub, NullLogger<OnlineForceOfflineSubscriber>.Instance);

        await sub.HandleOnlineForceOfflineAsync(
            new OnlineForceOfflinePayload("au-conn-self", InstanceIdentity.Current.InstanceId, "强制下线"));

        Assert.Empty(hub.ForceOfflineCalls);
    }

    // ── ④ 空/空白连接号:不推不抛(脏载荷不该走订阅重试) ─────────────────────────────────────
    [Fact]
    public async Task 订阅侧_空连接号_不推不抛()
    {
        var hub = new FakeHub();
        var sub = new OnlineForceOfflineSubscriber(hub, NullLogger<OnlineForceOfflineSubscriber>.Instance);

        await sub.HandleOnlineForceOfflineAsync(new OnlineForceOfflinePayload("", "other:1:abc", null));
        await sub.HandleOnlineForceOfflineAsync(new OnlineForceOfflinePayload("   ", "other:1:abc", null));

        Assert.Empty(hub.ForceOfflineCalls);
    }

    // ── ⑤ 事件名常量值逐字钉住(部署白名单键必须与代码一致,防静默失效) ─────────────────────
    [Fact]
    public void 事件名常量_与部署白名单同字面量()
    {
        Assert.Equal("online_force_offline", EventBusEvents.OnlineForceOffline);
    }

    // ── 装置 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>发布记录(捕获事件名与载荷对象)。</summary>
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

    /// <summary>强推记录(connectionId＋文案)。</summary>
    private sealed class FakeHub : IHubContext<OnlineUserHub, IOnlineUserHub>
    {
        public List<(string ConnectionId, string? Message)> ForceOfflineCalls { get; } = new();

        public IHubClients<IOnlineUserHub> Clients { get; }

        public IGroupManager Groups { get; } = new NoopGroupManager();

        public FakeHub() => Clients = new FakeClients(ForceOfflineCalls);
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>只关心 Client(id):返回绑定该 id 的记录型强类型客户端;其余入口返回无记录客户端(本批不用)。</summary>
    private sealed class FakeClients : IHubClients<IOnlineUserHub>
    {
        private readonly List<(string ConnectionId, string? Message)> _calls;

        public FakeClients(List<(string ConnectionId, string? Message)> calls) => _calls = calls;

        public IOnlineUserHub All => new RecordingClient(_calls, "ALL");

        public IOnlineUserHub AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(_calls, "ALL-EXCEPT");

        public IOnlineUserHub Client(string connectionId) => new RecordingClient(_calls, connectionId);

        public IOnlineUserHub Clients(IReadOnlyList<string> connectionIds) => new RecordingClient(_calls, "CLIENTS");

        public IOnlineUserHub Group(string groupName) => new RecordingClient(_calls, "GROUP");

        public IOnlineUserHub GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(_calls, "GROUP-EXCEPT");

        public IOnlineUserHub Groups(IReadOnlyList<string> groupNames) => new RecordingClient(_calls, "GROUPS");

        public IOnlineUserHub User(string userId) => new RecordingClient(_calls, "USER");

        public IOnlineUserHub Users(IReadOnlyList<string> userIds) => new RecordingClient(_calls, "USERS");
    }

    /// <summary>记录型强类型客户端:ForceOffline 记一笔;其余方法本批不使用,留空实现(不抛)。</summary>
    private sealed class RecordingClient : IOnlineUserHub
    {
        private readonly List<(string ConnectionId, string? Message)> _calls;
        private readonly string _target;

        public RecordingClient(List<(string ConnectionId, string? Message)> calls, string target)
        {
            _calls = calls;
            _target = target;
        }

        public Task ForceOffline(string message)
        {
            lock (_calls)
            {
                _calls.Add((_target, message));
            }

            return Task.CompletedTask;
        }

        public Task OnlineUserList(OnlineUserListMessage message) => Task.CompletedTask;

        public Task PublicNotice(SysNotice notice) => Task.CompletedTask;

        public Task ReceiveMessage(object context) => Task.CompletedTask;
    }

    /// <summary>DI 容器(批 F 同款)＋末位覆盖注册两个 fake ⇒ 服务解析到 fake,断言可观察。</summary>
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
            // 末位覆盖:被测编排要能观察到"推了谁"与"发了什么事件"
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