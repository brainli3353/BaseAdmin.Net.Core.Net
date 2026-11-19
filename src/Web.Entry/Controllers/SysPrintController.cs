using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Print;
using AdminCore.Core.Services.Print.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 打印模板 API(批 AF 4.6):分页 / 详情 / 新增 / 更新 / 复制 / 删除,路由 /api/sysPrint。
    /// 全端点标注 [MenuPerm](权限键与前端按钮一一对应,含独立的 sysPrint/copy——
    /// 参照仓复制复用 add 键,本仓给独立键以免"有新增权即可复制"的越权面)。
    /// **本仓后端只存模板不渲染**(见 SysPrintService remarks):无预览/导出/打印端点。
    /// </summary>
    [ApiController]
    [Route("api/sysPrint")]
    public class SysPrintController : ControllerBase
    {
        private readonly SysPrintService _printService;

        public SysPrintController(SysPrintService printService)
        {
            _printService = printService;
        }

        /// <summary>GET /api/sysPrint/page?name=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysPrintOutput&gt;(不含 Template 大字段)。</summary>
        [MenuPerm("sysPrint/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageSysPrintInput input)
        {
            var result = await _printService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysPrintOutput>>.Success(result));
        }

        /// <summary>GET /api/sysPrint/detail?id= → SysPrintDetailOutput(含 Template 全文)。</summary>
        [MenuPerm("sysPrint/detail")]
        [HttpGet("detail")]
        public async Task<IActionResult> Detail([FromQuery] long id)
        {
            var result = await _printService.GetDetailAsync(id);
            return Ok(ApiResult<SysPrintDetailOutput>.Success(result));
        }

        /// <summary>POST /api/sysPrint/add:body {name, template, printType?, status?, orderNo?, remark?} → 新模板 Id。</summary>
        [MenuPerm("sysPrint/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddSysPrintInput input)
        {
            var id = await _printService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysPrint/update:body {id, name?, template?, printType?, status?, orderNo?, remark?}(部分更新)→ 空成功。</summary>
        [MenuPerm("sysPrint/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateSysPrintInput input)
        {
            await _printService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysPrint/copy?id= → 副本新 Id(名称自动 "原名 - copy(N)")。</summary>
        [MenuPerm("sysPrint/copy")]
        [HttpPost("copy")]
        public async Task<IActionResult> Copy([FromQuery] long id)
        {
            var newId = await _printService.CopyAsync(id);
            return Ok(ApiResult<long>.Success(newId));
        }

        /// <summary>POST /api/sysPrint/delete?id=:软删 → 空成功。</summary>
        [MenuPerm("sysPrint/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _printService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }
    }
}