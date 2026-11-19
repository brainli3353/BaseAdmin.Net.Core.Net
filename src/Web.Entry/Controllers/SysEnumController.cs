using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Enum;
using AdminCore.Core.Utils.EnumIntrospection;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 系统枚举 API(批 Z 6.9):编译期枚举元数据只读内省,路由 /api/sysEnum。
    /// 全 [MenuPerm](超管穿透;诊断级元数据,未授权 403)。
    /// </summary>
    [ApiController]
    [Route("api/sysEnum")]
    public class SysEnumController : ControllerBase
    {
        private readonly SysEnumService _enumService;

        public SysEnumController(SysEnumService enumService)
        {
            _enumService = enumService;
        }

        /// <summary>GET /api/sysEnum/typeList → List&lt;EnumTypeOutput&gt;(全部枚举类型+值集合)。</summary>
        [MenuPerm("sysEnum/typeList")]
        [HttpGet("typeList")]
        public IActionResult TypeList()
        {
            return Ok(ApiResult<List<EnumTypeOutput>>.Success(_enumService.GetEnumTypeList()));
        }

        /// <summary>GET /api/sysEnum/data?enumName= → List&lt;EnumEntity&gt;(单枚举值集合)。</summary>
        [MenuPerm("sysEnum/data")]
        [HttpGet("data")]
        public IActionResult Data([FromQuery] EnumQueryInput input)
        {
            return Ok(ApiResult<List<EnumEntity>>.Success(_enumService.GetEnumDataList(input)));
        }

        /// <summary>GET /api/sysEnum/dataByField?entityName=&amp;fieldName= → List&lt;EnumEntity&gt;(实体字段枚举值集合)。</summary>
        [MenuPerm("sysEnum/dataByField")]
        [HttpGet("dataByField")]
        public IActionResult DataByField([FromQuery] EnumDataByFieldInput input)
        {
            return Ok(ApiResult<List<EnumEntity>>.Success(_enumService.GetEnumDataListByField(input)));
        }
    }
}
