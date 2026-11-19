using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 菜单按钮权限集服务契约(task_022 契约 2;契约在 Core、实现在 Business/Services/Menu、装配在 Entry——
    /// 与 008 已退役的 IUserApiPermissionProvider 同模式:Web.Core/Core 不得反向引用 Business 实体)。
    /// </summary>
    public interface IMenuPermService
    {
        /// <summary>
        /// 解析用户按钮权限集(用户 → 角色 → 角色授权 → 菜单 Type=3 按钮 Permission 集合;带缓存与空集负缓存)。
        /// </summary>
        Task<IReadOnlyCollection<string>> GetPermissionsAsync(long userId);

        /// <summary>失效单用户权限缓存(用户角色变更/用户删除)。</summary>
        Task InvalidateUserAsync(long userId);

        /// <summary>失效某角色下全部用户权限缓存(角色授权变更/角色删除)。</summary>
        Task InvalidateRoleAsync(long roleId);

        /// <summary>失效持有某菜单的全部用户权限缓存(菜单权限标识变更/菜单删除)。</summary>
        Task InvalidateMenuAsync(long menuId);
    }

    /// <summary>
    /// 菜单按钮权限特性(task_022 契约 1):标注在 Controller Action 上,声明该接口要求持有指定菜单按钮权限。
    /// 值为 sys_menu 表 Permission 字段值(与菜单按钮种子行一致,如 sysUser/page;一处配置两端生效:
    /// 前端渲染与后端 403 校验共用同一份菜单数据)。
    /// </summary>
    /// <remarks>
    /// 判定模型(契约):标注接口 → 当前用户按钮权限集包含标注值 → 放行,否则 403;
    /// 未标注接口 → 默认放行(向后兼容)。/api 未认证 401 全局门禁由 PermissionMiddleware 统一承担,
    /// 本特性不参与 401 裁决(仅防御性兜底,防管线顺序变更)。
    /// 本文件特性与 Filter 同批(契约交付物单文件):Filter 为全局 ActionFilter(Entry 经 MvcOptions AddService 装配,
    /// 按请求作用域解析,与 OpLogFilter 同先例),运行时读取端点元数据中的本特性决定放行/403。
    /// 超管(Claim AccountType==999)放行——对齐 008 超管穿透语义(超管权限不依赖菜单授权链)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class MenuPermAttribute : Attribute
    {
        /// <summary>菜单按钮权限标识(sys_menu.Permission 字段值;比较时忽略大小写)。</summary>
        public string Permission { get; }

        public MenuPermAttribute(string permission)
        {
            Permission = permission;
        }
    }

    /// <summary>
    /// 按钮权限 ActionFilter(task_022 契约 1):对 [MenuPerm] 标注端点校验当前用户按钮权限集。
    /// </summary>
    /// <remarks>
    /// 判定链(仅标注端点;未标注端点第一步即放行):
    /// 1) 未认证 → 401(正常由 PermissionMiddleware 先行拦截,此处为管线顺序变更的防御性兜底);
    /// 2) 超管(AccountType==999)→ 放行;
    /// 3) Token 有效但缺用户标识 → 401(签发缺陷,口径与中间件一致:"认不出人"不是"没权限");
    /// 4) 权限集包含标注值 → 放行,否则 403。
    /// 403/401 响应体用匿名对象与 Web.Core ApiResult.Fail 同构(Core 不得引用 Web.Core,层级纪律;
    /// 经全局 JSON 选项 camelCase+中文不转义序列化,输出结构与控制器输出完全一致)。
    /// </remarks>
    [Service(ServiceType = typeof(MenuPermFilter), Lifetime = ServiceLifetime.Transient)]
    public sealed class MenuPermFilter : IAsyncActionFilter
    {
        private readonly IMenuPermService _permService;
        private readonly UserManager _userManager; // 批 D 1.14:身份判定经门面收敛(原 User.FindFirst 直读)

        public MenuPermFilter(IMenuPermService permService, UserManager userManager)
        {
            _permService = permService;
            _userManager = userManager;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            // 未标注 [MenuPerm] 的接口默认放行(契约:向后兼容)——含全部非 API MVC 页面端点。
            // WHY 手动遍历而非 GetMetadata<T>() 扩展方法:该扩展在 Core 工程(仅 FrameworkReference)下
            // 解析不可达(实机构建探针 CS1061),直接类型匹配遍历等价且零依赖歧义
            MenuPermAttribute? attr = null;
            foreach (var metadata in context.ActionDescriptor.EndpointMetadata)
            {
                if (metadata is MenuPermAttribute a)
                {
                    attr = a;
                    break;
                }
            }
            if (attr is null)
            {
                await next();
                return;
            }

            var user = context.HttpContext.User;

            // 1) 未认证 → 401(防御性兜底:正常路径 PermissionMiddleware 已先行拦截)
            if (user.Identity?.IsAuthenticated != true)
            {
                context.Result = new ObjectResult(new { code = 1, msg = "未登录或 Token 已失效" })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
                return;
            }

            // 2) 超管放行(008 超管穿透语义;判定经 UserManager 单点)
            if (_userManager.IsSuperAdmin)
            {
                await next();
                return;
            }

            // 3) Token 有效但缺用户标识:签发缺陷 → 401(不是"没权限",是"认不出人")
            if (_userManager.UserId is not long userId)
            {
                context.Result = new ObjectResult(new { code = 1, msg = "Token 缺少用户标识" })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
                return;
            }

            // 4) 按钮权限集校验:包含标注值 → 放行,否则 403
            var permissions = await _permService.GetPermissionsAsync(userId);
            if (permissions.Contains(attr.Permission, StringComparer.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            context.Result = new ObjectResult(new { code = 1, msg = $"无权限:{attr.Permission}" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
