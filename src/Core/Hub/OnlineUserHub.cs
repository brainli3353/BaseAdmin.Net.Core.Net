using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Security;
using AdminCore.Core.Services.OnlineUser.Dto;
using Microsoft.AspNetCore.SignalR;

namespace AdminCore.Core.Hub
{
    /// <summary>
    /// 在线用户集线器(批 F 1.8/5.13)。装配:Web.Entry Program.cs `AddSignalR()`＋
    /// `app.MapHub&lt;OnlineUserHub&gt;("/hubs/onlineUser")`(非 /api 前缀,不经 PermissionMiddleware 门禁;
    /// negotiate 请求走 UseAuthentication 默认 scheme=JwtBearer,未带 token 即 401)。
    /// 在线表:Hash 键 `CacheConst.KeyUserOnline`,field=ConnectionId,值=OnlineUserInfo;
    /// TTL 24h 滑动续期(每次注册/注销刷新)——崩溃断连(无 OnDisconnected)的僵尸行自动清理(参照加固)。
    /// 参照 AdminNetPro OnlineUserHub 移植:去 Furion(AddNewtonsoftJsonProtocol→原生 System.Text.Json 默认协议)、
    /// 去 LoginMode/Device(本仓无多端)。
    /// </summary>
    public class OnlineUserHub : Hub<IOnlineUserHub>
    {
        private readonly ICacheService _cache;
        private readonly IHubContext<OnlineUserHub, IOnlineUserHub> _hubContext;

        public OnlineUserHub(ICacheService cache, IHubContext<OnlineUserHub, IOnlineUserHub> hubContext)
        {
            _cache = cache;
            _hubContext = hubContext;
        }

        /// <summary>连接:取已认证 Claim 注册在线表＋入租户分组＋广播最新列表;未认证不注册(不中断,参照口径)。</summary>
        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var claims = Context.User?.Claims;
            if (httpContext is null || claims is null)
            {
                return;
            }

            var userId = long.TryParse(claims.FirstOrDefault(c => c.Type == JwtClaimNames.UserId)?.Value, out var uid) ? uid : -1;
            var account = claims.FirstOrDefault(c => c.Type == JwtClaimNames.Account)?.Value;
            if (userId < 0 || string.IsNullOrWhiteSpace(account))
            {
                return;
            }

            var tenantId = long.TryParse(claims.FirstOrDefault(c => c.Type == JwtClaimNames.TenantId)?.Value, out var tid)
                ? tid
                : SqlSugarConst.DefaultTenantId;
            var realName = claims.FirstOrDefault(c => c.Type == JwtClaimNames.RealName)?.Value;
            var userAgent = httpContext.Request.Headers["User-Agent"].FirstOrDefault();

            var info = new OnlineUserInfo
            {
                ConnectionId = Context.ConnectionId,
                UserId = userId,
                TenantId = tenantId,
                Account = account,
                RealName = realName,
                Time = DateTime.Now,
                Ip = ResolveClientIp(httpContext),
                Browser = UserAgentHelper.ParseBrowser(userAgent),
                Os = UserAgentHelper.ParseOs(userAgent)
            };

            await _cache.HashSetAsync(CacheConst.KeyUserOnline, Context.ConnectionId, info);
            await _cache.SetExpireAsync(CacheConst.KeyUserOnline, CacheConst.OnlineUserTtl); // 滑动续期

            // 以租户 Id 分组(参照口径),组内广播在线列表
            var groupName = $"{CacheConst.GroupOnline}{tenantId}";
            await _hubContext.Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            await BroadcastUserListAsync(tenantId, info.RealName, online: true);
        }

        /// <summary>断开:注销在线表＋广播最新列表。</summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (string.IsNullOrEmpty(Context.ConnectionId))
            {
                return;
            }

            var info = await _cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, Context.ConnectionId);
            if (info is null)
            {
                return;
            }

            await _cache.HashDeleteAsync(CacheConst.KeyUserOnline, Context.ConnectionId);
            await _cache.SetExpireAsync(CacheConst.KeyUserOnline, CacheConst.OnlineUserTtl);
            await BroadcastUserListAsync(info.TenantId, info.RealName, online: false);
        }

        /// <summary>租户分组广播全量在线列表(参照口径:连接/断开时全量推送,客户端直接覆盖刷新)。</summary>
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

        /// <summary>客户端 IP:X-Forwarded-For 首段 → RemoteIpAddress(参照口径)。</summary>
        private static string? ResolveClientIp(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            var forwarded = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Split(',')[0].Trim();
            }

            return httpContext.Connection.RemoteIpAddress?.ToString();
        }
    }
}
