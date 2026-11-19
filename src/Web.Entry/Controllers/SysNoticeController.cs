using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Notice.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 通知公告 API(批 G 4.7),路由 /api/sysNotice/*。
    /// 管理面 5 端点(page/add/update/delete/public)全 [MenuPerm];
    /// 自读面 5 端点(setRead/pageReceived/unReadList/last/detail)豁免 [MenuPerm](登录即可——裁决③,参照动态 API 无独立权限码)。
    /// </summary>
    [ApiController]
    [Route("api/sysNotice")]
    public class SysNoticeController : ControllerBase
    {
        private readonly SysNoticeService _noticeService;

        public SysNoticeController(SysNoticeService noticeService)
        {
            _noticeService = noticeService;
        }

        /// <summary>GET /api/sysNotice/page?title=&amp;type=&amp;pageIndex=&amp;pageSize= → PageResult&lt;NoticeOutput&gt;(非超管仅本人发布)。</summary>
        [MenuPerm("sysNotice/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageNoticeInput input)
        {
            var result = await _noticeService.PageAsync(input);
            return Ok(ApiResult<PageResult<NoticeOutput>>.Success(result));
        }

        /// <summary>POST /api/sysNotice/add:body {title, content, type} → 新通知 Id(恒草稿)。</summary>
        [Idempotent]
        [MenuPerm("sysNotice/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddNoticeInput input)
        {
            var id = await _noticeService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysNotice/update:body {id, title?, content?, type?}(部分更新,仅发布人本人)→ 空成功。</summary>
        [MenuPerm("sysNotice/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateNoticeInput input)
        {
            await _noticeService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysNotice/delete?id=:软删通知＋级联物理删接收行(仅发布人,已发布禁删)→ 空成功。</summary>
        [MenuPerm("sysNotice/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _noticeService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysNotice/public:body {id, userIds?}(空=全租户用户;重发幂等)→ 发布并广播在线接收人。</summary>
        [MenuPerm("sysNotice/public")]
        [HttpPost("public")]
        public async Task<IActionResult> Public([FromBody] PublicNoticeInput input)
        {
            await _noticeService.PublicAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysNotice/setRead?id=:设置本人该通知已读(自读面,登录即可)→ 空成功。</summary>
        [HttpPost("setRead")]
        public async Task<IActionResult> SetRead([FromQuery] long id)
        {
            await _noticeService.SetReadAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>GET /api/sysNotice/pageReceived?title=&amp;type=&amp;pageIndex=&amp;pageSize= → PageResult&lt;NoticeReceivedOutput&gt;(自读面)。</summary>
        [HttpGet("pageReceived")]
        public async Task<IActionResult> PageReceived([FromQuery] PageNoticeInput input)
        {
            var result = await _noticeService.PageReceivedAsync(input);
            return Ok(ApiResult<PageResult<NoticeReceivedOutput>>.Success(result));
        }

        /// <summary>GET /api/sysNotice/unReadList → List&lt;NoticeOutput&gt;(本人未读,自读面)。</summary>
        [HttpGet("unReadList")]
        public async Task<IActionResult> UnReadList()
        {
            var list = await _noticeService.GetUnReadListAsync();
            return Ok(ApiResult<List<NoticeOutput>>.Success(list));
        }

        /// <summary>GET /api/sysNotice/last → NoticeOutput?（最新一条,自读面)。</summary>
        [HttpGet("last")]
        public async Task<IActionResult> Last()
        {
            var notice = await _noticeService.GetLastAsync();
            return Ok(ApiResult<NoticeOutput?>.Success(notice));
        }

        /// <summary>GET /api/sysNotice/detail?id= → NoticeOutput?(自读面)。</summary>
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var notice = await _noticeService.GetDetailAsync(id);
            return Ok(ApiResult<NoticeOutput?>.Success(notice));
        }
    }
}
