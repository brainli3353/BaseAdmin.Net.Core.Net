using AdminCore.Core.Models;
using AdminCore.Core.Services.ColumnCustom;
using AdminCore.Core.Services.ColumnCustom.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 用户表格列配置 API(批 A 4.12):本人列设置存取,路由 /api/sysColumnCustom。
    /// 自助端点不贴按钮键(登录门禁 + 身份取自 Claim,仅操作本人数据——同 updatePassword 口径);
    /// 无独立菜单页(设置组件消费,由业务视图侧调用,#14 豁免留痕)。
    /// </summary>
    [ApiController]
    [Route("api/sysColumnCustom")]
    public class SysColumnCustomController : ControllerBase
    {
        private readonly SysColumnCustomService _columnCustomService;

        public SysColumnCustomController(SysColumnCustomService columnCustomService)
        {
            _columnCustomService = columnCustomService;
        }

        /// <summary>GET /api/sysColumnCustom/detail?gridId= → ColumnCustomOutput?（本人该表列配置,无配置 null）。</summary>
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] GetColumnCustomInput input)
        {
            var result = await _columnCustomService.GetDetailAsync(input);
            return Ok(ApiResult<ColumnCustomOutput?>.Success(result));
        }

        /// <summary>POST /api/sysColumnCustom/store:body 四类字典 → 空成功（业务键 upsert,整体覆盖）。</summary>
        [HttpPost("store")]
        public async Task<IActionResult> Store([FromBody] StoreColumnCustomInput input)
        {
            await _columnCustomService.StoreAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysColumnCustom/reset:body {gridId} → 空成功（物理删本人该表配置）。</summary>
        [HttpPost("reset")]
        public async Task<IActionResult> Reset([FromBody] GetColumnCustomInput input)
        {
            await _columnCustomService.ResetAsync(input);
            return Ok(ApiResult.Success());
        }
    }
}
