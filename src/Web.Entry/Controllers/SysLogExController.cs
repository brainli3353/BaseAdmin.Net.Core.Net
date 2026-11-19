using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 异常日志 API(5.1 异常档查询侧,立项 M4 日志面板数据源):分页 + 详情,路由 /api/sysLogEx。
    /// 标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透;同 SysLogOpController 口径)。
    /// 写入侧不在本控制器(ExceptionMiddleware 发布 ex_log 事件,事件总线异步落库)。
    /// </summary>
    [ApiController]
    [Route("api/sysLogEx")]
    public class SysLogExController : ControllerBase
    {
        private readonly SysLogExService _logExService;

        public SysLogExController(SysLogExService logExService)
        {
            _logExService = logExService;
        }

        /// <summary>GET /api/sysLogEx/page?account=&amp;controllerName=&amp;remoteIp=&amp;startTime=&amp;endTime=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysLogExOutput&gt;(不含大字段)。</summary>
        [MenuPerm("sysLogEx/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSysLogExInput input)
        {
            var result = await _logExService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysLogExOutput>>.Success(result));
        }

        /// <summary>GET /api/sysLogEx/detail?id= → SysLogExDetailOutput(全字段,含异常全文/请求参数/返回结果)。</summary>
        [MenuPerm("sysLogEx/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _logExService.GetDetailAsync(id);
            return Ok(ApiResult<SysLogExDetailOutput>.Success(result));
        }
    }
}
