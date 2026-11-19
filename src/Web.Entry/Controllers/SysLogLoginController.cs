using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 登录日志 API(批 D 5.18 登录日志查询端点):分页查询,路由 /api/sysLogLogin/page。
    /// 标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透)。
    /// 写入侧在 AuthService(登录一/二阶段成败 + 登出,批 D 接线)。
    /// </summary>
    [ApiController]
    [Route("api/sysLogLogin")]
    public class SysLogLoginController : ControllerBase
    {
        private readonly SysLogLoginService _logLoginService;

        public SysLogLoginController(SysLogLoginService logLoginService)
        {
            _logLoginService = logLoginService;
        }

        /// <summary>GET /api/sysLogLogin/page?account=&amp;startTime=&amp;endTime=&amp;status=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysLogLoginOutput&gt;。</summary>
        [MenuPerm("sysLogLogin/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSysLogLoginInput input)
        {
            var result = await _logLoginService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysLogLoginOutput>>.Success(result));
        }
    }
}
