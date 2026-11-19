using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.EventBus;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Message.Dto;
using AdminCore.Core.Services.OnlineUser.Dto;
using AdminCore.Core.Utils;
using Microsoft.AspNetCore.SignalR;
using AdminCore.Core.Hub;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Services.Message
{
    /// <summary>
    /// 站内消息服务(批 G 4.8;参照 AdminNetPro SysMessageService 移植,去 Furion):
    /// 纯推送模型(裁决①)——发送即推送在线接收人＋经事件总线异步落消息日志([LogTable] 日志库),
    /// 无独立消息实体,离线接收人既不推送也不落日志(消息不存储不补发;需存储语义走 4.7 通知公告)。
    /// 参照的 SendUser 载荷口径:推送载荷=SysLogMsg 全量(含发送/接收双方快照)而非原始 input——客户端拿到即完整渲染所需。
    /// </summary>
    [Service]
    public class SysMessageService
    {
        private readonly ICacheService _cache;
        private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _hubContext;
        private readonly IEventPublisher _eventPublisher;
        private readonly ILogger<SysMessageService> _logger;

        public SysMessageService(
            ICacheService cache,
            IHubContext<OnlineUserHub, IOnlineUserHub> hubContext,
            IEventPublisher eventPublisher,
            ILogger<SysMessageService> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 发送给所有人:向所有在线连接广播 ReceiveMessage(载荷=原始 input 快照,无日志——参照口径,广播不落日志)。
        /// </summary>
        public async Task SendAllUserAsync(MessageInput input)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Message))
            {
                throw AppException.Of(ErrorCodes.MessageContentRequired);
            }

            await _hubContext.Clients.All.ReceiveMessage(input);

            // 批 AV:跨实例广播(他实例各推各自的 Clients.All ⇒ 全集群在线连接都收到)
            try
            {
                await _eventPublisher.PublishAsync(EventBusEvents.OnlinePush, new OnlinePushPayload(
                    InstanceIdentity.Current.InstanceId,
                    OnlinePushKinds.Message,
                    ConnectionIds: null,
                    BroadcastAll: true,
                    Notice: null,
                    Message: input));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "跨实例站内消息(广播)失败(本实例已完成;他实例在线用户可能收不到)");
            }
        }

        /// <summary>
        /// 发送给某人:取在线表快照发送人/接收人 → 逐个在线接收人单播 ReceiveMessage(载荷=消息日志全量)
        /// → 每接收人一条 SysLogMsg 经事件总线异步落库(事件名 "msg_log");接收人离线 → 不推送不落日志(参照口径)。
        /// </summary>
        public async Task SendUserAsync(MessageInput input)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Message))
            {
                throw AppException.Of(ErrorCodes.MessageContentRequired);
            }

            if (input.ReceiveUserIds is null || input.ReceiveUserIds.Count == 0)
            {
                throw AppException.Of(ErrorCodes.MessageReceiversRequired);
            }

            var all = await _cache.HashGetAllAsync<OnlineUserInfo>(CacheConst.KeyUserOnline);
            var sender = all.FirstOrDefault(u => u.UserId == input.SendUserId);
            var receivers = all.Where(u => input.ReceiveUserIds.Contains(u.UserId)).ToList();
            if (receivers.Count == 0)
            {
                return; // 接收人均离线:不推送不落日志(参照口径,幂等静默)
            }

            var messageType = input.MessageType is >= 0 and <= 3
                ? ((MessageTypeEnum)input.MessageType).ToString()
                : MessageTypeEnum.Info.ToString();

            // 在线表按连接粒度:同一接收人多设备(多连接)各收一次单播,
            // 但消息日志按接收人(userId)去重落一行——快照取该用户首个在线连接的行
            var firstRowByUser = receivers
                .GroupBy(u => u.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var (userId, snapshot) in firstRowByUser)
            {
                var logMsg = new SysLogMsg
                {
                    MessageType = messageType,
                    Title = input.Title,
                    Message = input.Message,
                    ReceiveUserId = userId,
                    ReceiveUserName = snapshot.RealName,
                    ReceiveIp = snapshot.Ip,
                    ReceiveBrowser = snapshot.Browser,
                    ReceiveOs = snapshot.Os,
                    SendUserId = sender?.UserId ?? input.SendUserId,
                    SendUserName = sender?.RealName,
                    SendIp = sender?.Ip,
                    SendBrowser = sender?.Browser,
                    SendOs = sender?.Os,
                    SendTime = DateTime.Now
                };

                // 该用户全部在线连接逐条单播(多设备都收到)
                foreach (var receiver in receivers.Where(u => u.UserId == userId))
                {
                    // 推送失败(连接已断)静默 no-op,日志仍记录发送事实
                    await _hubContext.Clients.Client(receiver.ConnectionId).ReceiveMessage(logMsg);
                }

                // 批 AV:跨实例广播该用户的全部在线连接(本实例上面已直推;订阅者按来源自跳,不会重推)
                try
                {
                    await _eventPublisher.PublishAsync(EventBusEvents.OnlinePush, new OnlinePushPayload(
                        InstanceIdentity.Current.InstanceId,
                        OnlinePushKinds.Message,
                        receivers.Where(u => u.UserId == userId)
                            .Where(u => !string.IsNullOrWhiteSpace(u.ConnectionId))
                            .Select(u => u.ConnectionId)
                            .ToList(),
                        BroadcastAll: false,
                        Notice: null,
                        Message: logMsg));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "跨实例站内消息(单播)失败(本实例已完成;他实例上的该用户可能收不到)接收人 {UserId}", userId);
                }

                await _eventPublisher.PublishAsync("msg_log", logMsg);
            }
        }
    }
}
