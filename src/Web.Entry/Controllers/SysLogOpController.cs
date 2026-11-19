using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 操作日志 API(批 D 5.1 日志闭环查询侧,立项 M4 日志面板数据源):分页查询,路由 /api/sysLogOp/page。
    /// 标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透)。
    /// 写入侧不在本控制器(OpLogFilter 事件总线异步落库)。
    /// </summary>
    [ApiController]
    [Route("api/sysLogOp")]
    public class SysLogOpController : ControllerBase
    {
        private readonly SysLogOpService _logOpService;

        public SysLogOpController(SysLogOpService logOpService)
        {
            _logOpService = logOpService;
        }

        /// <summary>GET /api/sysLogOp/page?account=&amp;controllerName=&amp;success=&amp;startTime=&amp;endTime=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysLogOpOutput&gt;(不含大字段)。</summary>
        [MenuPerm("sysLogOp/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSysLogOpInput input)
        {
            var result = await _logOpService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysLogOpOutput>>.Success(result));
        }

        /// <summary>GET /api/sysLogOp/detail?id= → SysLogOpDetailOutput(全字段,含请求参数/返回结果两大字段;批 AZ 参照口径)。</summary>
        [MenuPerm("sysLogOp/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _logOpService.GetDetailAsync(id);
            return Ok(ApiResult<SysLogOpDetailOutput>.Success(result));
        }
    }
}
