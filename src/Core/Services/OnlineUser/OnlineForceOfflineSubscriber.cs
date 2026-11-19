using AdminCore.Core.Attributes;
using AdminCore.Core.EventBus;
using AdminCore.Core.Hub;
using AdminCore.Core.Services.OnlineUser.Dto;
using AdminCore.Core.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Services.OnlineUser
{
    /// <summary>
    /// 跨实例强制下线订阅者(批 AU F4):收到他实例广播的强踢事件 ⇒ 向**本机**客户端推 <c>forceOffline</c>。
    /// </summary>
    /// <remarks>
    /// WHY 这样能"只投递到归属实例":<c>IHubContext.Clients.Client(id)</c> 的语义是"在本实例的连接集合里找"——
    /// 连接不在本机时 SignalR 静默 no-op(不抛)。于是一次广播、各实例各推一次,只有持有该连接的实例真正送达,
    /// **不需要**"谁持有该连接"的额外注册与路由(零新状态、零新依赖)。
    ///
    /// 自源跳过(防双推):发布方在本地已经直推过(<c>SysOnlineUserService.ForceOfflineAsync</c>),而事件总线的
    /// **本地投递**会把同一事件再喂给本实例的订阅者 ⇒ 必须按 <see cref="OnlineForceOfflinePayload.SourceInstanceId"/>
    /// 与自己比对后跳过。跳过而不"视为异常":这就是设计的三态边界(见载荷 remarks)。
    ///
    /// 失败语义:推送异常只记日志不抛出——订阅执行器(批 S)会按重试策略处理;强踢的**服务端判定**(Redis Hash 删除)
    /// 已在发布方完成,推送失败不影响"该会话已下线"的事实,只影响客户端是否立刻收到提示。
    ///
    /// 注册:<c>[Service(ServiceType = typeof(OnlineForceOfflineSubscriber))]</c> 显式锁定具体类——
    /// 总线按**具体类型**从作用域解析订阅者,省略 ServiceType 会被自动匹配注册成接口类型反而解析不到(同批 G/S 口径)。
    /// </remarks>
    [Service(ServiceType = typeof(OnlineForceOfflineSubscriber))]
    public class OnlineForceOfflineSubscriber : IEventSubscriber
    {
        private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _hubContext;
        private readonly ILogger<OnlineForceOfflineSubscriber> _logger;

        public OnlineForceOfflineSubscriber(
            IHubContext<OnlineUserHub, IOnlineUserHub> hubContext,
            ILogger<OnlineForceOfflineSubscriber> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 处理跨实例强踢:非本实例持有该连接时 SignalR 自动 no-op(见 remarks)。
        /// </summary>
        /// <param name="payload">强踢载荷(connectionId＋来源实例＋文案)。</param>
        [EventSubscribe(EventBusEvents.OnlineForceOffline)]
        public async Task HandleOnlineForceOfflineAsync(OnlineForceOfflinePayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload?.ConnectionId))
            {
                return; // 空连接:无可投递(不抛——脏载荷不该让订阅执行器走重试)
            }

            var self = InstanceIdentity.Current.InstanceId;
            if (string.Equals(payload.SourceInstanceId, self, StringComparison.Ordinal))
            {
                // 本实例自己发的事件:本地已直推过 ⇒ 跳过(否则客户端收到两条通知)
                return;
            }

            await _hubContext.Clients.Client(payload.ConnectionId)
                .ForceOffline(string.IsNullOrWhiteSpace(payload.Reason) ? "强制下线" : payload.Reason!);

            _logger.LogInformation(
                "跨实例强踢已投递到本实例的连接集合(来源 {Source};connectionId={ConnectionId};连接不在本机则为 no-op)",
                payload.SourceInstanceId, payload.ConnectionId);
        }
    }
}