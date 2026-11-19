using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.CodeGen;
using AdminCore.Core.Services.CodeGen.Dto;
using AdminCore.Core.Services.Database.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 代码生成 API(批 AI 6.1):表清单 + 生成预览,路由 /api/sysCodeGen。
    /// **只返回文本,不写盘**——参照仓把生成物直接写进源码目录(Web 端点写服务器源码=高危面),
    /// 本仓只做"预览+复制"(见 CodeGenService remarks);全端点 [MenuPerm]。
    /// </summary>
    [ApiController]
    [Route("api/sysCodeGen")]
    public class SysCodeGenController : ControllerBase
    {
        private readonly CodeGenService _codeGenService;

        public SysCodeGenController(CodeGenService codeGenService)
        {
            _codeGenService = codeGenService;
        }

        /// <summary>GET /api/sysCodeGen/tables?keyword= → 可选表清单(复用数据库管理只读清单)。</summary>
        [MenuPerm("sysCodeGen/tables")]
        [HttpGet("tables")]
        public async Task<IActionResult> Tables([FromQuery] string? keyword)
        {
            var result = await _codeGenService.GetTableListAsync(keyword);
            return Ok(ApiResult<List<DbTableOutput>>.Success(result));
        }

        /// <summary>POST /api/sysCodeGen/preview:body {tableName, className?, moduleName?, removePrefix?, baseType?} → 三段生成文本 + warnings(不写盘)。</summary>
        [MenuPerm("sysCodeGen/preview")]
        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] CodeGenInput input)
        {
            var result = await _codeGenService.PreviewAsync(input);
            return Ok(ApiResult<CodeGenPreviewOutput>.Success(result));
        }
    }
}
