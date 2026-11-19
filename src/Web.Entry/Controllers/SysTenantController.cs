using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 租户管理 API(批 C 多租户业务模块 C1):分页 / 新建 / 更新 / 删除 / 启停,路由 /api/sysTenant/*。
    /// 全部端点标注 [MenuPerm] 受校验(标注端点校验、未标注默认放行,超管穿透)。租户管理为平台职能(超管)。
    /// </summary>
    [ApiController]
    [Route("api/sysTenant")]
    public class SysTenantController : ControllerBase
    {
        private readonly SysTenantService _tenantService;

        public SysTenantController(SysTenantService tenantService)
        {
            _tenantService = tenantService;
        }

        /// <summary>GET /api/sysTenant/page?pageIndex=&amp;pageSize=&amp;name= → PageResult&lt;TenantOutput&gt;。</summary>
        [MenuPerm("sysTenant/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageTenantInput input)
        {
            var result = await _tenantService.PageAsync(input);
            return Ok(ApiResult<PageResult<TenantOutput>>.Success(result));
        }

        /// <summary>POST /api/sysTenant/add:body {name, plan?, expirationTime?, orderNo?, remark?} → 新租户 Id(= TenantId 标识)。</summary>
        [MenuPerm("sysTenant/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddTenantInput input)
        {
            var id = await _tenantService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysTenant/update:body {id, name?, plan?, expirationTime?, orderNo?, remark?}(部分更新)→ 空成功。</summary>
        [MenuPerm("sysTenant/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateTenantInput input)
        {
            await _tenantService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysTenant/delete?id=:软删(默认租户禁删)→ 空成功。</summary>
        [MenuPerm("sysTenant/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _tenantService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysTenant/setStatus:body {id, status}(0=启用 1=停用;默认租户禁停)→ 空成功。停用即失效该租户全员 token 缓存。</summary>
        [MenuPerm("sysTenant/setStatus")]
        [HttpPost("setStatus")]
        public async Task<IActionResult> SetStatus([FromBody] SetTenantStatusInput input)
        {
            await _tenantService.SetStatusAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// POST /api/sysTenant/initDb?id=:初始化租户独立库(批 AB 库级隔离 C4)→ InitTenantDbOutput。
        /// 幂等:可重复调用(建表 CodeFirst 增量 / 种子 upsert);未配置连接串 → 业务异常(不静默落主库)。
        /// </summary>
        [MenuPerm("sysTenant/initDb")]
        [HttpPost("initDb")]
        public async Task<IActionResult> InitDb([FromQuery] long id)
        {
            var result = await _tenantService.InitDbAsync(id);
            return Ok(ApiResult<InitTenantDbOutput>.Success(result));
        }
    }
}
