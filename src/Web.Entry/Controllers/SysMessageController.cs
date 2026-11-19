using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Message;
using AdminCore.Core.Services.Message.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 站内消息 API(批 G 4.8),路由 /api/sysMessage/*:发送入口仅服务端(裁决②,无 client→server 发送)。
    /// 两 send 端点全 [MenuPerm](消息发送属管理面操作)。
    /// </summary>
    [ApiController]
    [Route("api/sysMessage")]
    public class SysMessageController : ControllerBase
    {
        private readonly SysMessageService _messageService;

        public SysMessageController(SysMessageService messageService)
        {
            _messageService = messageService;
        }

        /// <summary>POST /api/sysMessage/sendUser:body {messageType, title, message, sendUserId, receiveUserIds} → 单播在线接收人＋逐接收人落消息日志。</summary>
        [MenuPerm("sysMessage/sendUser")]
        [HttpPost("sendUser")]
        public async Task<IActionResult> SendUser([FromBody] MessageInput input)
        {
            await _messageService.SendUserAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysMessage/sendAll:body 同 sendUser → 广播全部在线连接(不落日志,参照口径)。</summary>
        [MenuPerm("sysMessage/sendAll")]
        [HttpPost("sendAll")]
        public async Task<IActionResult> SendAll([FromBody] MessageInput input)
        {
            await _messageService.SendAllUserAsync(input);
            return Ok(ApiResult.Success());
        }
    }
}
