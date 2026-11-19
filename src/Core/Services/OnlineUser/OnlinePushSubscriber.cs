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
    /// 跨实例在线推送订阅者(批 AV F3):收到他实例广播的推送意图 ⇒ 向**本机**客户端投递
    /// (<c>PublicNotice</c> 公告 / <c>ReceiveMessage</c> 站内消息)。
    /// </summary>
    /// <remarks>
    /// WHY 与强踢(批 AU)同构:<c>IHubContext.Clients.*</c> 只在本实例的连接集合里投递,连接不在此处即静默 no-op
    /// ⇒ "一次广播、各实例各推一次、只有归属实例真正送达",不需要"谁持有哪个连接"的注册表(零新状态、零新依赖)。
    ///
    /// 自源跳过:发布方已在本机直推过(公告/消息的本地路径原样保留),而总线**本地投递**会把同一事件再喂给本实例
    /// 订阅者 ⇒ 必须按 <see cref="OnlinePushPayload.SourceInstanceId"/> 自跳,否则客户端收到两份。
    ///
    /// 失败语义:未知 kind/缺必填字段 = **脏载荷**,记告警后返回(不进重试——重试也不会变好);
    /// 投递异常只记日志不抛出(订阅执行器按批 S 重试策略处理;推送失败不改变"消息/公告已落库"的事实)。
    ///
    /// 注册:<c>[Service(ServiceType = typeof(OnlinePushSubscriber))]</c> 显式锁定具体类——
    /// 总线按具体类型从作用域解析订阅者(同批 G/S/AU 口径)。
    /// </remarks>
    [Service(ServiceType = typeof(OnlinePushSubscriber))]
    public class OnlinePushSubscriber : IEventSubscriber
    {
        private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _hubContext;
        private readonly ILogger<OnlinePushSubscriber> _logger;

        public OnlinePushSubscriber(
            IHubContext<OnlineUserHub, IOnlineUserHub> hubContext,
            ILogger<OnlinePushSubscriber> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 处理跨实例在线推送:按 <see cref="OnlinePushPayload.Kind"/> 分派(公告逐连接 / 消息广播或逐连接)。
        /// </summary>
        /// <param name="payload">推送载荷(来源实例＋类型＋目标＋内容)。</param>
        [EventSubscribe(EventBusEvents.OnlinePush)]
        public async Task HandleOnlinePushAsync(OnlinePushPayload payload)
        {
            if (payload is null || string.IsNullOrWhiteSpace(payload.Kind))
            {
                _logger.LogWarning("跨实例在线推送收到空载荷/无类型标签,已跳过(脏载荷不进重试)");
                return;
            }

            if (string.Equals(payload.SourceInstanceId, InstanceIdentity.Current.InstanceId, StringComparison.Ordinal))
            {
                return; // 本实例自己发的事件:本地路径已直推(见 remarks)
            }

            try
            {
                switch (payload.Kind)
                {
                    case OnlinePushKinds.Notice:
                        await PushNoticeAsync(payload);
                        break;

                    case OnlinePushKinds.Message:
                        await PushMessageAsync(payload);
                        break;

                    default:
                        _logger.LogWarning(
                            "跨实例在线推送收到未知类型标签 {Kind}(本实例版本可能落后于发布方,已跳过)",
                            payload.Kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                // 投递失败只记日志:推送是"通知"语义,不改变公告/消息已落库的事实(同批 AU 口径)
                _logger.LogWarning(ex, "跨实例在线推送投递失败(kind={Kind};连接不在本机时为正常 no-op)", payload.Kind);
            }
        }

        // 公告:逐连接推 PublicNotice(连接不在本机 = no-op)
        private async Task PushNoticeAsync(OnlinePushPayload payload)
        {
            if (payload.Notice is null)
            {
                _logger.LogWarning("跨实例在线推送 kind=notice 但缺 Notice 载荷,已跳过");
                return;
            }

            var ids = payload.ConnectionIds ?? new List<string>();
            foreach (var connectionId in ids)
            {
                if (string.IsNullOrWhiteSpace(connectionId))
                {
                    continue;
                }

                await _hubContext.Clients.Client(connectionId).PublicNotice(payload.Notice);
            }

            _logger.LogInformation(
                "跨实例公告推送已投递到本实例的连接集合(来源 {Source};目标连接 {Count} 个,非本机为 no-op)",
                payload.SourceInstanceId, ids.Count);
        }

        // 站内消息:广播(All)或逐连接
        private async Task PushMessageAsync(OnlinePushPayload payload)
        {
            if (payload.Message is null)
            {
                _logger.LogWarning("跨实例在线推送 kind=message 但缺 Message 载荷,已跳过");
                return;
            }

            if (payload.BroadcastAll)
            {
                await _hubContext.Clients.All.ReceiveMessage(payload.Message);
                _logger.LogInformation("跨实例站内消息(广播)已投递到本实例全部连接(来源 {Source})", payload.SourceInstanceId);
                return;
            }

            var ids = payload.ConnectionIds ?? new List<string>();
            foreach (var connectionId in ids)
            {
                if (string.IsNullOrWhiteSpace(connectionId))
                {
                    continue;
                }

                await _hubContext.Clients.Client(connectionId).ReceiveMessage(payload.Message);
            }

            _logger.LogInformation(
                "跨实例站内消息(单播)已投递到本实例的连接集合(来源 {Source};目标连接 {Count} 个,非本机为 no-op)",
                payload.SourceInstanceId, ids.Count);
        }
    }
}