using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 差异日志 API(批 X 5.4):分页 + 详情,路由 /api/sysLogDiff。
    /// 标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透;同 SysLogExController 口径)。
    /// 写入侧不在本控制器(SqlSugar OnDiffLogEvent 自动落库,开关 DbSettings:EnableDiffLog)。
    /// </summary>
    [ApiController]
    [Route("api/sysLogDiff")]
    public class SysLogDiffController : ControllerBase
    {
        private readonly SysLogDiffService _logDiffService;

        public SysLogDiffController(SysLogDiffService logDiffService)
        {
            _logDiffService = logDiffService;
        }

        /// <summary>GET /api/sysLogDiff/page?startTime=&amp;endTime=&amp;pageIndex=&amp;pageSize= → PageResult&lt;PageLogDiffOutput&gt;(不含大字段)。</summary>
        [MenuPerm("sysLogDiff/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageLogDiffInput input)
        {
            var result = await _logDiffService.PageAsync(input);
            return Ok(ApiResult<PageResult<PageLogDiffOutput>>.Success(result));
        }

        /// <summary>GET /api/sysLogDiff/detail?id= → SysLogDiffDetailOutput(全字段,含操作前/后快照)。</summary>
        [MenuPerm("sysLogDiff/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _logDiffService.GetDetailAsync(id);
            return Ok(ApiResult<SysLogDiffDetailOutput>.Success(result));
        }
    }
}
