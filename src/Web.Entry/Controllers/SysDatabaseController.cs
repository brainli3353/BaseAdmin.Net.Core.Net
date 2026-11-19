using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Database;
using AdminCore.Core.Services.Database.Dto;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 数据库管理 API(3.8,平台裁剪·只读):表清单 / 列结构 / 索引,路由 /api/sysDatabase。
    /// 全端点标注 [MenuPerm](平台/超管职能的门禁;标注端点校验、超管穿透)。
    /// **危险操作(建库/删库/DDL/备份/还原)未开放**——范围裁决见 SysDatabaseService remarks。
    /// </summary>
    [ApiController]
    [Route("api/sysDatabase")]
    public class SysDatabaseController : ControllerBase
    {
        private readonly SysDatabaseService _databaseService;

        public SysDatabaseController(SysDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        /// <summary>GET /api/sysDatabase/tables?keyword= → List&lt;DbTableOutput&gt;(按占用大小倒序;keyword 按表名/注释模糊)。</summary>
        [MenuPerm("sysDatabase/tables")]
        [HttpGet("tables")]
        public async Task<IActionResult> Tables([FromQuery] string? keyword)
        {
            var result = await _databaseService.ListTablesAsync(keyword);
            return Ok(ApiResult<List<DbTableOutput>>.Success(result));
        }

        /// <summary>GET /api/sysDatabase/columns?table= → List&lt;DbColumnOutput&gt;(表不存在 → 业务异常)。</summary>
        [MenuPerm("sysDatabase/columns")]
        [HttpGet("columns")]
        public async Task<IActionResult> Columns([FromQuery] string table)
        {
            var result = await _databaseService.ListColumnsAsync(table);
            return Ok(ApiResult<List<DbColumnOutput>>.Success(result));
        }

        /// <summary>GET /api/sysDatabase/indexes?table= → List&lt;DbIndexOutput&gt;(表不存在 → 业务异常;无索引返回空表)。</summary>
        [MenuPerm("sysDatabase/indexes")]
        [HttpGet("indexes")]
        public async Task<IActionResult> Indexes([FromQuery] string table)
        {
            var result = await _databaseService.ListIndexesAsync(table);
            return Ok(ApiResult<List<DbIndexOutput>>.Success(result));
        }
    }
}