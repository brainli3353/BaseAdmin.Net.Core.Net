using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.Services.Export;
using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 用户管理 API(契约 §7 + 批 A 1.11/2.1 密码安全):分页(账号模糊)/ 新建 / 更新 / 删除 / 分配角色
    /// / 改密(自助)/ 重置密码(管理员)/ 解锁登录(管理员),路由 /api/sysUser/*。
    /// 权限口径(批 A 2.10 覆盖,task_022 默认放行模型:未标注端点对已登录用户放行;标注端点须持有对应按钮权限,
    /// 否则 403;超管放行;/api 未认证 401 由 PermissionMiddleware 统一门禁):
    /// page/add/update/delete/grantRole/resetPwd/unlockLogin/export 标注 [MenuPerm];
    /// updatePassword 自助端点不贴按钮键(登录门禁 + 身份取自 Claim,改本人密码无需按钮授权)。
    /// </summary>
    [ApiController]
    [Route("api/sysUser")]
    public class SysUserController : ControllerBase
    {
        private readonly SysUserService _userService;
        private readonly UserManager _userManager; // 批 D 1.14:当前用户身份经门面收敛

        public SysUserController(SysUserService userService, UserManager userManager)
        {
            _userService = userService;
            _userManager = userManager;
        }

        /// <summary>GET /api/sysUser/export?account= → 导出用户列表（xlsx 文件流）。</summary>
        [HttpGet("export")]
        [MenuPerm("sysUser/export")]
        public async Task<IActionResult> Export([FromQuery] PageUserInput input, [FromServices] ExportService exportService)
        {
            // 导出取全量匹配行:分页上限放大到 10000(通用后台导出硬上限,防单请求拉爆内存),
            // 查询/机构数据范围过滤复用 PageAsync 同一口径(数据权限与列表页一致,不存在越权导出)。
            var result = await _userService.PageAsync(input with { PageIndex = 1, PageSize = 10000 });
            var stream = await exportService.ExportAsync(result.List, ExportFormat.Xlsx, "用户列表");
            return File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"用户列表_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
        }

        /// <summary>GET /api/sysUser/page?account=&amp;pageIndex=&amp;pageSize= → PageResult&lt;UserOutput&gt;。</summary>
        /// <remarks>[MenuPerm] 标注示例(task_022):值为 sys_menu 表按钮行(Id=10010)的 Permission "sysUser/page";
        /// 未授该按钮的用户 → 403,持按钮或超管 → 200。</remarks>
        [HttpGet("page")]
        [MenuPerm("sysUser/page")]
        public async Task<IActionResult> Page([FromQuery] PageUserInput input)
        {
            var result = await _userService.PageAsync(input);
            return Ok(ApiResult<PageResult<UserOutput>>.Success(result));
        }

        /// <summary>POST /api/sysUser/add:body {account, password, realName, status?, roleIds?} → 新用户 Id。</summary>
        [UnitOfWork]
        [HttpPost("add")]
        [MenuPerm("sysUser/add")]
        public async Task<IActionResult> Add([FromBody] AddUserInput input)
        {
            var id = await _userService.AddAsync(input);
            return Ok(ApiResult<long>.Success(id));
        }

        /// <summary>POST /api/sysUser/update:body {id, account, realName, status?} → 空成功。</summary>
        [HttpPost("update")]
        [MenuPerm("sysUser/update")]
        public async Task<IActionResult> Update([FromBody] UpdateUserInput input)
        {
            await _userService.UpdateAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysUser/delete?id=:软删(超管/本人禁删)→ 空成功。</summary>
        [UnitOfWork]
        [HttpPost("delete")]
        [MenuPerm("sysUser/delete")]
        public async Task<IActionResult> Delete([FromQuery] long id)
        {
            // 当前登录用户 Id 经 UserManager 门面(批 D 1.14;防伪造他人身份删自己)
            var currentUserId = _userManager.UserId!.Value;
            await _userService.DeleteAsync(id, currentUserId);
            return Ok(ApiResult.Success());
        }

        /// <summary>POST /api/sysUser/grantRole:body {userId, roleIds} 全量替换该用户角色 → 空成功。</summary>
        [UnitOfWork]
        [HttpPost("grantRole")]
        [MenuPerm("sysUser/grantRole")]
        public async Task<IActionResult> GrantRole([FromBody] GrantRoleInput input)
        {
            await _userService.GrantRoleAsync(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// GET /api/sysUser/roleIds?userId= → List&lt;long&gt;(批 BG):回读该用户现有角色 Id 集,
        /// 供分配角色对话框回显(GrantRole 全量替换口径下无回显=误清风险)。
        /// 复用按钮键 sysUser/grantRole(读是写的前置,不新增菜单种子;约定 #14 同批闭环,菜单种子以复用留痕)。
        /// </summary>
        [HttpGet("roleIds")]
        [MenuPerm("sysUser/grantRole")]
        public async Task<IActionResult> RoleIds([FromQuery] long userId)
        {
            var ids = await _userService.GetUserRoleIdsAsync(userId);
            return Ok(ApiResult<List<long>>.Success(ids));
        }

        /// <summary>
        /// POST /api/sysUser/updatePassword:body {oldPassword, newPassword} 改本人密码(批 A 1.11)→ 空成功。
        /// 自助端点:不贴 [MenuPerm](登录门禁 + UserId 取自 Claim,改本人密码无需按钮授权);
        /// 改密成功触发 TokenVersion++ 强制下线闭环(旧 Token 即刻作废)。
        /// </summary>
        [UnitOfWork]
        [HttpPost("updatePassword")]
        public async Task<IActionResult> UpdatePassword([FromBody] UpdatePasswordInput input)
        {
            // 当前用户 Id 经 UserManager 门面(批 D 1.14;防改他人密码)
            var userId = _userManager.UserId!.Value;
            await _userService.UpdatePassword(userId, input);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// POST /api/sysUser/resetPwd:body {userId, newPassword} 管理员重置他人密码(批 A 1.11)→ 空成功。
        /// 重置成功触发 TokenVersion++ 强制下线闭环 + 清空该账号密码错误计数(解锁锁定窗口)。
        /// </summary>
        [UnitOfWork]
        [HttpPost("resetPwd")]
        [MenuPerm("sysUser/resetPwd")]
        public async Task<IActionResult> ResetPwd([FromBody] ResetPwdInput input)
        {
            await _userService.ResetPwd(input);
            return Ok(ApiResult.Success());
        }

        /// <summary>
        /// POST /api/sysUser/unlockLogin:body {account} 解锁被锁定的登录账号(批 A 2.1)→ 空成功。
        /// 清空该账号密码错误计数 + OTP 失败计数(30min 锁定窗口即刻解除);不触发 TokenVersion++。
        /// </summary>
        [HttpPost("unlockLogin")]
        [MenuPerm("sysUser/unlockLogin")]
        public async Task<IActionResult> UnlockLogin([FromBody] UnlockLoginInput input)
        {
            await _userService.UnlockLogin(input);
            return Ok(ApiResult.Success());
        }
    }
}
