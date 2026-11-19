using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Hub;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.OnlineUser.Dto;
using AdminCore.Core.Security;
using AdminCore.Core.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Services.OnlineUser
{
    /// <summary>
    /// 在线用户服务(批 F 1.8/5.13):在线列表(当前租户;超管全量)/单连接强踢/按用户强踢/单登录踢旧。
    /// 强踢双保险:SignalR 推送 forceOffline(客户端主动断开)＋服务端同步删 Hash 字段(客户端未收到也不残留)。
    /// 参照 AdminNetPro SysOnlineUserService 移植:去 Furion 特性;SingleLogin 配置键沿用 `sys_single_login`
    /// (经 SysConfig 管理端配置,缺省 false=多端并存,零行为变化)。
    /// **批 AU**:强踢追加**跨实例广播**(本实例直推管本机连接、事件总线管他机连接)——解「集群下踢了不掉线」。
    /// </summary>
    [Service]
    public class SysOnlineUserService
    {
        // 单登录配置键(值 true/1 启用)——单点引公共 const(约定#11,消除与种子/测试的双源漂移)
        private const string SingleLoginConfigCode = AdminCore.Core.Services.Config.SysConfigService.SingleLoginEnabled;

        private readonly ICacheService _cache;
        private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _hubContext;
        private readonly UserManager _userManager;
        private readonly SysConfigService _configService;
        private readonly IEventPublisher _publisher;
        private readonly ILogger<SysOnlineUserService> _logger;

        public SysOnlineUserService(
            ICacheService cache,
            IHubContext<OnlineUserHub, IOnlineUserHub> hubContext,
            UserManager userManager,
            SysConfigService configService,
            IEventPublisher publisher,
            ILogger<SysOnlineUserService> logger)
        {
            _cache = cache;
            _hubContext = hubContext;
            _userManager = userManager;
            _configService = configService;
            _publisher = publisher;
            _logger = logger;
        }

        /// <summary>在线用户列表:当前租户(超管=全租户,口径同其他查询端点)。</summary>
        public async Task<List<OnlineUserInfo>> GetOnlineUserListAsync()
        {
            var all = await _cache.HashGetAllAsync<OnlineUserInfo>(CacheConst.KeyUserOnline);
            return _userManager.IsSuperAdmin
                ? all.ToList()
                : all.Where(u => u.TenantId == _userManager.TenantId).ToList();
        }

        /// <summary>
        /// 单连接强制下线:推送 forceOffline(客户端断开)＋服务端删 Hash 字段(双保险);
        /// 并向该连接所属租户分组广播最新列表(参照口径)。
        /// </summary>
        public async Task ForceOfflineAsync(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            var info = await _cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, connectionId);
            if (info is null)
            {
                return; // 已下线(幂等)
            }

            // 推送给该连接(连接不存在时 SignalR 静默 no-op,不抛异常)
            await _hubContext.Clients.Client(connectionId).ForceOffline("强制下线");
            await _cache.HashDeleteAsync(CacheConst.KeyUserOnline, connectionId);
            await _cache.SetExpireAsync(CacheConst.KeyUserOnline, CacheConst.OnlineUserTtl);
            await BroadcastUserListAsync(info.TenantId, info.RealName, online: false);

            // 批 AU(跨实例强踢):上面是**本实例**直推(管本机连接);下面把同一意图广播给他实例,
            // 由各自的 OnlineForceOfflineSubscriber 推它们那侧的连接(非归属实例为 no-op)。
            // 订阅者按 SourceInstanceId 自跳,故本实例不会因"本地投递"再推一次。
            try
            {
                await _publisher.PublishAsync(EventBusEvents.OnlineForceOffline,
                    new OnlineForceOfflinePayload(connectionId, InstanceIdentity.Current.InstanceId, "强制下线"));
            }
            catch (Exception ex)
            {
                // 广播失败只告警:服务端已判定下线(Hash 已删),推送只是"通知客户端断开";
                // 集群下该连接若在别的实例上,则只会等到下次重连失败——可检索日志见此处。
                _logger.LogWarning(ex, "跨实例强踢广播失败(本实例推送已完成;他实例上的该连接可能收不到下线提示)connectionId={ConnectionId}", connectionId);
            }
        }

        /// <summary>
        /// 按用户强踢全部连接(批 A 1.11 强踢闭环③ 接入点:OfflineAndExpireToken 停用/改密/重置时调用)。
        /// </summary>
        public async Task ForceOfflineByUserIdAsync(long userId)
        {
            var all = await _cache.HashGetAllAsync<OnlineUserInfo>(CacheConst.KeyUserOnline);
            var users = all.Where(u => u.UserId == userId).ToList();
            foreach (var user in users)
            {
                await ForceOfflineAsync(user.ConnectionId);
            }
        }

        /// <summary>
        /// 单登录(补 1.1 缺口「单设备踢人」+ 批 CN 服务端强吊销判定):配置 `sys_single_login` 启用时踢掉该用户既有全部连接,
        /// 并返回 true——供调用方 AuthService 据此递增 TokenVersion 作废旧 token(签发前调用);
        /// 缺省 false=多端并存(零行为变化),返回 false。
        /// </summary>
        public async Task<bool> SingleLoginAsync(long userId)
        {
            var value = await _configService.GetConfigValueByCodeAsync(SingleLoginConfigCode);
            if (string.IsNullOrWhiteSpace(value)
                || (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && value != "1"))
            {
                return false;
            }

            await ForceOfflineByUserIdAsync(userId);
            return true;
        }

        /// <summary>
        /// 广播通知公告给在线接收人(批 G 4.7;参照 SysOnlineUserService.PublicNotice 移植):
        /// 取在线表中 userIds 命中的连接逐个单播 PublicNotice;无人在线静默返回(离线不推送——通知公告已落库)。
        /// </summary>
        public async Task PublicNoticeAsync(SysNotice notice, List<long> userIds)
        {
            if (notice is null || userIds is null || userIds.Count == 0)
            {
                return;
            }

            var all = await _cache.HashGetAllAsync<OnlineUserInfo>(CacheConst.KeyUserOnline);
            var targets = all.Where(u => userIds.Contains(u.UserId)).ToList();
            foreach (var user in targets)
            {
                await _hubContext.Clients.Client(user.ConnectionId).PublicNotice(notice);
            }

            // 批 AV:跨实例广播(本实例上面已直推;他实例由 OnlinePushSubscriber 推各自连接,非本机 no-op)
            try
            {
                await _publisher.PublishAsync(EventBusEvents.OnlinePush, new OnlinePushPayload(
                    InstanceIdentity.Current.InstanceId,
                    OnlinePushKinds.Notice,
                    targets.Where(t => !string.IsNullOrWhiteSpace(t.ConnectionId)).Select(t => t.ConnectionId).ToList(),
                    BroadcastAll: false,
                    Notice: notice,
                    Message: null));
            }
            catch (Exception ex)
            {
                // 广播失败只告警:公告已落库、本实例在线接收人已收到;他实例在线用户会错过本次实时推送
                _logger.LogWarning(ex, "跨实例公告推送广播失败(本实例已完成;他实例在线用户可能收不到)公告 Id={NoticeId}", notice.Id);
            }
        }

        /// <summary>租户分组广播全量在线列表(与 Hub 同口径)。</summary>
        private async Task BroadcastUserListAsync(long tenantId, string? realName, bool online)
        {
            var all = await _cache.HashGetAllAsync<OnlineUserInfo>(CacheConst.KeyUserOnline);
            var message = new OnlineUserListMessage
            {
                RealName = realName,
                Online = online,
                UserList = all.Where(u => u.TenantId == tenantId).ToList()
            };
            await _hubContext.Clients.Group($"{CacheConst.GroupOnline}{tenantId}").OnlineUserList(message);
        }
    }
}
