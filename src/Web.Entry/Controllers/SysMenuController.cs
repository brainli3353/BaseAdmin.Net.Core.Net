using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Services.Menu.Dto;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 菜单管理 API(契约 §7):树查询 / 新建 / 更新 / 删除(有子节点禁删),路由 /api/sysMenu/*。
    /// 全部端点标注 [MenuPerm] 受校验(批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透)。
    /// </summary>
    [ApiController]
    [Route("api/sysMenu")]
    public class SysMenuController : ControllerBase
    {
        private readonly SysMenuService _menuService;
        private readonly UserManager _userManager; // 批 D 1.14:当前用户身份经门面收敛
        private readonly ILocalizationService _localization; // 批 AR-5:错误码文案按请求语言渲染

        public SysMenuController(SysMenuService menuService, UserManager userManager, ILocalizationService localization)
        {
            _menuService = menuService;
            _userManager = userManager;
            _localization = localization;
        }

        /// <summary>
        /// GET /api/sysMenu/page → 菜单全树(List&lt;MenuOutput&gt;)。
        /// WHY 树查询端点路由名为 page:契约 §8 种子权限键是 sysMenu/page(Permission 与路由必须一致),
        /// 故树查询端点落 page 路由,保证按钮权限端到端可用。
        /// </summary>
        [MenuPerm("sysMenu/page")]
        [HttpGet("page")]
        public async Task<IActionResult> Page()
        {
            var tree = await _menuService.TreeAsync();
            return Ok(ApiResult<List<MenuOutput>>.Success(tree));
        }

        /// <summary>
        /// GET /api/sysMenu/user → 当前登录用户菜单树(批 A 2.3:超管全量,普通用户按角色裁剪,均剔按钮;
        /// 前端导航渲染用)。全登录用户可用——用户查自己的菜单不贴具体权限键(登录门禁 + 按角色裁剪承担),
        /// 与 page(管理端全量树,需 sysMenu/page 按钮)区分。
        /// </summary>
        [HttpGet("user")]
        public async Task<IActionResult> UserMenu()
        {
            // 当前用户身份经 UserManager 门面(批 D 1.14;防伪造;UserId/AccountType 由登录签发,见 JwtClaimNames)
            if (_userManager.UserId is not long userId || _userManager.AccountType is not int accountType)
            {
                // Token 有效但缺用户标识:签发缺陷 → 401(与 MenuPermFilter 第 3 步同口径)
                return Unauthorized(ApiResult.Fail(_localization.Get(ErrorCodes.CommonUserIdentityMissing), 401, ErrorCodes.CommonUserIdentityMissing));
            }
            var tree = await _menuService.GetLoginMenuTreeAsync(userId, accountType);
            return Ok(ApiResult<List<MenuOutput>>.Success(tree));
        }

        /// <summary>POST /api/sysMenu/add:body {pid, name, title, type, permission?, orderNo?, icon?} → 新节点 Id。</summary>
        [MenuPerm("sysMenu/add")]
        [HttpPost("add")]
        public async Task<IActionResult> Add([FromBody] AddMenuInput input)
        {
            var id = await _menuService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysMenu/update:body {id, pid, name, title, type, permission?, orderNo?, icon?} → 空成功。</summary>
        [MenuPerm("sysMenu/update")]
        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateMenuInput input)
        {
            await _menuService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysMenu/delete?id=:软删(有子节点禁删;级联清角色授权引用)→ 空成功。</summary>
        [MenuPerm("sysMenu/delete")]
        [UnitOfWork]
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            await _menuService.DeleteAsync(id);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysMenu/setStatus?id=&amp;status=(0 启用/1 停用;批 CH G28):停用节点+整子树退出登录菜单树;管理端全量树与 API 权限集不受影响。</summary>
        [MenuPerm("sysMenu/setStatus")]
        [HttpPost("setStatus")]
        public async Task<IActionResult> SetStatus([FromQuery] long id, [FromQuery] int status)
        {
            await _menuService.SetStatusAsync(id, status);
            return Ok(ApiResult.Success());
        }
    }
}
