using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// Http 出站日志 API(5.3 查询侧):分页 + 详情,路由 /api/sysLogHttp。
    /// 标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透;同 SysLogExController 口径)。
    /// 写入侧不在本控制器(HttpLoggingHandler 发布 http_log 事件,事件总线异步落库)。
    /// </summary>
    [ApiController]
    [Route("api/sysLogHttp")]
    public class SysLogHttpController : ControllerBase
    {
        private readonly SysLogHttpService _logHttpService;

        public SysLogHttpController(SysLogHttpService logHttpService)
        {
            _logHttpService = logHttpService;
        }

        /// <summary>GET /api/sysLogHttp/page?account=&amp;url=&amp;httpMethod=&amp;statusCode=&amp;startTime=&amp;endTime=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysLogHttpOutput&gt;(不含大字段)。</summary>
        [MenuPerm("sysLogHttp/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSysLogHttpInput input)
        {
            var result = await _logHttpService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysLogHttpOutput>>.Success(result));
        }

        /// <summary>GET /api/sysLogHttp/detail?id= → SysLogHttpDetailOutput(全字段,含请求头/请求体/响应体/异常)。</summary>
        [MenuPerm("sysLogHttp/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _logHttpService.GetDetailAsync(id);
            return Ok(ApiResult<SysLogHttpDetailOutput>.Success(result));
        }
    }
}