using AdminCore.Core.Attributes;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Services.RoleTable.Dto;
using AdminCore.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 角色管理 API(契约 §7):分页 / 新建 / 更新 / 删除(级联清理 + 内置禁删)/ 角色授权(全量替换)
    /// / 接口黑名单授权 + 查询(批 A 2.10 SysRoleApi 细粒度禁用),路由 /api/sysRole/*。
    /// 全部端点标注 [MenuPerm] 受校验(批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透)。
    /// </summary>
    [ApiController]
    [Route("api/sysRole")]
    public class SysRoleController : ControllerBase
    {
        private readonly SysRoleService _roleService;
        private readonly SysRoleTableService _roleTableService;

        public SysRoleController(SysRoleService roleService, SysRoleTableService roleTableService)
        {
            _roleService = roleService;
            _roleTableService = roleTableService;
        }

        /// <summary>GET /api/sysRole/page?pageIndex=&amp;pageSize= → PageResult&lt;RoleOutput&gt;。</summary>
        [MenuPerm("sysRole/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page([FromQuery] PageRoleInput input)
        {
            var result = await _roleService.PageAsync(input);
            return Ok(ApiResult<PageResult<RoleOutput>>.Success(result));
        }

        /// <summary>POST /api/sysRole/add:body {name, code, remark?} → 新角色 Id。</summary>
        [MenuPerm("sysRole/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddRoleInput input)
        {
            var id = await _roleService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysRole/update:body {id, name, remark?} → 空成功。</summary>
        [MenuPerm("sysRole/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateRoleInput input)
        {
            await _roleService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysRole/delete?id=:软删 + 级联清理(内置角色禁删)→ 空成功。</summary>
        [UnitOfWork]
        [MenuPerm("sysRole/delete")]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _roleService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysRole/grantMenu:body {roleId, menuIds} 全量替换角色授权(变更即失效该角色全员权限缓存)→ 空成功。</summary>
        [UnitOfWork]
        [MenuPerm("sysRole/grantMenu")]
        [HttpPost("grantMenu")]
        public async Task<IActionResult> GrantMenu([FromBody] GrantMenuInput input)
        {
            await _roleService.GrantMenuAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysRole/grantApi:body {roleId, apiList} 全量替换角色接口黑名单(变更即失效该角色全员权限缓存)→ 空成功。</summary>
        [UnitOfWork]
        [MenuPerm("sysRole/grantApi")]
        [HttpPost("grantApi")]
        public async Task<IActionResult> GrantApi([FromBody] GrantApiInput input)
        {
            await _roleService.GrantApiAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>GET /api/sysRole/api?roleId= → 该角色接口黑名单(权限标识列表;空集 = 未禁用任何接口)。</summary>
        [MenuPerm("sysRole/api")]
        [HttpGet("api")]
        public async Task<IActionResult> Api([FromQuery] long roleId)
        {
            var list = await _roleService.GetApiListAsync(roleId);
            return Ok(ApiResult<List<string>>.Success(list));
        }

        /// <summary>POST /api/sysRole/grantOrg:body {roleId, orgIds} 全量替换角色数据范围授权(越权防护 + 变更即失效该角色全员数据权限缓存)→ 空成功。</summary>
        [UnitOfWork]
        [MenuPerm("sysRole/grantOrg")]
        [HttpPost("grantOrg")]
        public async Task<IActionResult> GrantOrg([FromBody] GrantOrgInput input)
        {
            await _roleService.GrantOrgAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>GET /api/sysRole/org?roleId= → 该角色数据范围授权(机构 Id 列表;空集 = 未授自定义机构)。</summary>
        [MenuPerm("sysRole/org")]
        [HttpGet("org")]
        public async Task<IActionResult> Org([FromQuery] long roleId)
        {
            var list = await _roleService.GetOrgListAsync(roleId);
            return Ok(ApiResult<List<long>>.Success(list));
        }

        /// <summary>POST /api/sysRole/copy body={id} → 复制角色(批 H 2.2):新角色 Code/Name 追加 " - copy"＋三张关联表全量复制 → 新角色 Id。</summary>
        [MenuPerm("sysRole/copy")]
        [UnitOfWork]
        [HttpPost("copy")]
        public async Task<IActionResult> Copy([FromBody] CopyRoleInput input)
        {
            var newId = await _roleService.CopyRoleAsync(input.Id);
            return Ok(ApiResult<long>.Success(newId));
        }

        /// <summary>GET /api/sysRole/menu?roleId= → 该角色已授权菜单 Id 集合(批 H 2.4:前端菜单树勾选态回读)。</summary>
        [MenuPerm("sysRole/menu")]
        [HttpGet("menu")]
        public async Task<IActionResult> Menu([FromQuery] long roleId)
        {
            var list = await _roleService.GetMenuListAsync(roleId);
            return Ok(ApiResult<List<long>>.Success(list));
        }

        // ── 批 AH 2.9 角色字段权限(与 grantMenu/grantOrg 同域;强制点在响应出口,见 ColumnScopeFilter) ──

        /// <summary>POST /api/sysRole/grantTable:body {roleId, items:["表名:列名",...]}(**全量替换**,空数组=清空)→ 空成功。</summary>
        [MenuPerm("sysRole/grantTable")]
        [HttpPost("grantTable")]
        [AdminCore.Core.Attributes.UnitOfWork]
        public async Task<IActionResult> GrantTable([FromBody] RoleTableInput input)
        {
            await _roleTableService.GrantAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>GET /api/sysRole/table?roleId= → 该角色已授权列集合("表名:列名";与 grantTable 对偶)。</summary>
        [MenuPerm("sysRole/table")]
        [HttpGet("table")]
        public async Task<IActionResult> Table([FromQuery] long roleId)
        {
            var result = await _roleTableService.GetRoleTableAsync(roleId);
            return Ok(ApiResult<RoleTableOutput>.Success(result));
        }

        /// <summary>GET /api/sysRole/allTableColumn → 字段权限授权树(表→列;已排除 [SysTable] 与审计字段)。</summary>
        [MenuPerm("sysRole/allTableColumn")]
        [HttpGet("allTableColumn")]
        public async Task<IActionResult> AllTableColumn()
        {
            var result = await _roleTableService.GetAllTableColumnAsync();
            return Ok(ApiResult<List<TableColumnOutput>>.Success(result));
}

        /// <summary>GET /api/sysRole/tableScopes → 表级开关清单(批 AK:全表+受管状态+已授权角色数)。</summary>
        [MenuPerm("sysRole/tableScope")]
        [HttpGet("tableScopes")]
        public async Task<IActionResult> TableScopes()
        {
            var result = await _roleTableService.GetTableScopesAsync();
            return Ok(ApiResult<List<TableScopeOutput>>.Success(result));
        }

        /// <summary>POST /api/sysRole/tableScope:body {tableName, enabled} → 切换表级开关(立即生效,失效受管缓存)。</summary>
        [MenuPerm("sysRole/tableScope")]
        [HttpPost("tableScope")]
        public async Task<IActionResult> SetTableScope([FromBody] TableScopeInput input)
        {
            await _roleTableService.SetEnabledAsync(input.TableName, input.Enabled);
            return Ok(ApiResult.Success());
        }


    }}
