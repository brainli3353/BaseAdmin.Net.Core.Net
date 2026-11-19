using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 系统配置 API(批 D 4.1):分页 / 按 Code 读值 / 新建 / 更新 / 删除,路由 /api/sysConfig/*。
    /// 全部端点标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透)。
    /// add 贴 [Idempotent](批 D 1.13 首消费:窗口内重复提交 429,防双击建重名配置)。
    /// </summary>
    [ApiController]
    [Route("api/sysConfig")]
    public class SysConfigController : ControllerBase
    {
        private readonly SysConfigService _configService;

        public SysConfigController(SysConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>GET /api/sysConfig/page?name=&amp;code=&amp;groupCode=&amp;pageIndex=&amp;pageSize= → PageResult&lt;SysConfigOutput&gt;。</summary>
        [MenuPerm("sysConfig/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageConfigInput input)
        {
            var result = await _configService.PageAsync(input);
            return Ok(ApiResult<PageResult<SysConfigOutput>>.Success(result));
        }

        /// <summary>GET /api/sysConfig/get?code= → 配置值字符串(缓存读;不存在 → data=null)。</summary>
        [MenuPerm("sysConfig/get")]
        [HttpGet("get")]
        public async Task<IActionResult> Get([FromQuery] string code)
        {
            var value = await _configService.GetConfigValueByCodeAsync(code);
            return Ok(ApiResult<string?>.Success(value));
        }

        /// <summary>POST /api/sysConfig/add:body {name?, code, value, groupCode?, sysFlag?, remark?, enumOptions?} → 新配置 Id。窗口内重复提交 429。</summary>
        [Idempotent]
        [MenuPerm("sysConfig/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddConfigInput input)
        {
            var id = await _configService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysConfig/update:body {id, name?, value?, groupCode?, sysFlag?, remark?, enumOptions?}(部分更新)→ 空成功。</summary>
        [MenuPerm("sysConfig/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateConfigInput input)
        {
            await _configService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysConfig/delete?id=:软删(内置项禁删)→ 空成功。</summary>
        [MenuPerm("sysConfig/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _configService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }
    }
}
