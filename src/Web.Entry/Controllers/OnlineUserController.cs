using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Services.OnlineUser.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 在线用户管理(批 F 1.8/5.13):列表(当前租户,超管全量)/单连接强踢。
    /// 在线表实时变动经 SignalR Hub(`/hubs/onlineUser`)的 `onlineUserList` 广播(能力面已就位);
    /// 首方前端当前无 SignalR 订阅——onlineUser 页面走本控制器 list(挂载拉取＋手动刷新),实时订阅缺口见 G31。
    /// </summary>
    [ApiController]
    [Route("api/onlineUser")]
    public class OnlineUserController : Controller
    {
        private readonly SysOnlineUserService _service;

        public OnlineUserController(SysOnlineUserService service)
        {
            _service = service;
        }

        /// <summary>在线用户列表。</summary>
        [MenuPerm("onlineUser/list")]
        [HttpGet("list")]
        public async Task<IActionResult> List()
        {
            return Ok(ApiResult<List<OnlineUserInfo>>.Success(await _service.GetOnlineUserListAsync()));
        }

        /// <summary>单连接强制下线(推送 forceOffline＋服务端注销双保险)。</summary>
        [MenuPerm("onlineUser/forceOffline")]
        [HttpPost("forceOffline")]
        public async Task<IActionResult> ForceOffline([FromQuery] string connectionId)
        {
            await _service.ForceOfflineAsync(connectionId);
            return Ok(ApiResult.Success());
        }
    }
}
