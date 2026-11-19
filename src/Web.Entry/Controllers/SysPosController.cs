using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.Services.Pos.Dto;
using AdminCore.Core.Models;
using AdminCore.Core.Models.Import;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 职位管理 API(task_020 契约 §3):/api/sysPos/*,薄层(绑定 → Service → ApiResult)。
    /// 全部端点标注 [MenuPerm] 受校验(批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透)——无 [Authorize]。
    /// WHY 动词对齐 CRUD 语义:GET page / POST add / PUT update / DELETE delete(参照 task_011 SysDict 同型 CRUD 控制器)。
    /// </summary>
    [ApiController]
    [Route("api/sysPos")]
    public class SysPosController : ControllerBase
    {
        private readonly SysPosService _posService;

        public SysPosController(SysPosService posService)
        {
            _posService = posService;
        }

        /// <summary>GET /api/sysPos/page?pageIndex=&amp;pageSize=&amp;name?&amp;code? → PageResult&lt;PosOutput&gt;(Name/Code 模糊)。</summary>
        [MenuPerm("sysPos/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PagePosInput input)
        {
            var result = await _posService.PageAsync(input);
            return Ok(ApiResult<PageResult<PosOutput>>.Success(result));
        }

        /// <summary>POST /api/sysPos/add:body {name, code, sort?, remark?, status?} → 新职位 Id;重复 Code → 业务码 400。</summary>
        [MenuPerm("sysPos/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddPosInput input)
        {
            var id = await _posService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>PUT /api/sysPos/update:body {id, name, code, sort?, remark?, status?} → 空成功;变更 Code 撞码 → 业务码 400。</summary>
        [MenuPerm("sysPos/update")]
        [HttpPut("update")]
        public async Task<IActionResult> Update([FromBody] UpdatePosInput input)
        {
            await _posService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>DELETE /api/sysPos/delete?id= → 空成功;存在关联用户(SysUser.PosId)时业务码 400 拒绝。</summary>
        [MenuPerm("sysPos/delete")]
        [HttpDelete("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _posService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// POST /api/sysPos/import(multipart/form-data:file 必填)→ ImportReport(行级错误 HTTP 200 + code=0 + data=report);
        /// 扩展名非 .xlsx/.csv / 空文件 / &gt;5000 行 → 业务码(AppException 新错误码)。
        /// 端点为**多行写** → 贴 [UnitOfWork](全回滚:校验有错直接回报告不写;写入中途异常整体回滚)。
        /// </summary>
        [MenuPerm("sysPos/import")]
        [HttpPost("import")]
        [UnitOfWork]
        public async Task<IActionResult> Import([FromForm] IFormFile file)
        {
            var report = await _posService.ImportAsync(file);
            return Ok(ApiResult<ImportReport>.Success(report));
        }
    }
}
