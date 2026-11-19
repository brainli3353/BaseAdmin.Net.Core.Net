using AdminCore.Core.Cache;
using AdminCore.Core.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 幂等/防重复提交特性(批 D 1.13):标注在 Controller 端点上,声明窗口期(Seconds)内同一调用者
    /// 对该端点的重复请求直接拒绝(429)。典型场景:表单双提交(双击提交按钮)。
    /// </summary>
    /// <remarks>
    /// 键粒度(批 D 定案):{用户身份}:{控制器}:{动作}——不含参数 hash。
    /// WHY 不含参数:防的是「同一次用户操作的重放」(双击),此时参数恒同;按参数 hash 反而让
    /// 「同表单改一个字段再提交」绕过窗口。匿名调用者以 IP 代身份。
    /// 锁原语复用 ICacheService.IncrementAsync(与登录错误计数/任务锁同模式,约定 #11):
    /// 首个调用自增得 1 = 持锁放行;窗口内后续调用 &gt;1 = 重复提交 → AppException(429)。
    /// 窗口自首次提交起固定(IncrementAsync TTL 只写一次语义),不随重复提交续期。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class IdempotentAttribute : Attribute
    {
        /// <summary>窗口秒数(缺省 3 秒——双击间隔量级;业务可按端点风险调大)。</summary>
        public int Seconds { get; }

        /// <summary>拒绝消息(缺省「请勿重复提交」)。</summary>
        public string Message { get; }

        public IdempotentAttribute(int seconds = 3, string? message = null)
        {
            Seconds = seconds <= 0 ? 3 : seconds;
            Message = string.IsNullOrWhiteSpace(message) ? "请勿重复提交" : message!;
        }
    }

    /// <summary>
    /// 幂等全局 ActionFilter(批 D 1.13):端点标注 [Idempotent] → 按「身份:控制器:动作」键自增,
    /// 窗口内第二次起抛 AppException(429,「请勿重复提交」)。未标注端点直通零开销。
    /// </summary>
    /// <remarks>
    /// 身份来源:已认证取 UserId Claim(经 JwtClaimNames 单点);匿名取远端 IP(登录等匿名端点可用)。
    /// 注册:Program.cs MvcOptions Filters——排 MenuPermFilter 之内层(无权限 403 短路不消费幂等窗口)、
    /// UnitOfWorkFilter 之外层(重复拒绝不开事务)。
    /// </remarks>
    [Service(ServiceType = typeof(IdempotentFilter), Lifetime = ServiceLifetime.Transient)]
    public sealed class IdempotentFilter : IAsyncActionFilter
    {
        private readonly ICacheService _cache;

        public IdempotentFilter(ICacheService cache)
        {
            _cache = cache;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            IdempotentAttribute? attr = null;
            foreach (var metadata in context.ActionDescriptor.EndpointMetadata)
            {
                if (metadata is IdempotentAttribute a)
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

            // 身份:已认证取 UserId Claim,匿名取远端 IP(键内不可含 ':' 歧义——UserId 为纯数字、IP 为字面量,天然无歧义)
            var user = context.HttpContext.User;
            var identity = user.Identity?.IsAuthenticated == true
                && long.TryParse(user.FindFirst(AdminCore.Core.Security.JwtClaimNames.UserId)?.Value, out var userId)
                ? userId.ToString()
                : (context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            // 端点标识取 RouteValues(controller/action,特性路由下恒有值)——ActionDescriptor 基类成员,不依赖子类
            var controller = context.ActionDescriptor.RouteValues["controller"]?.ToString() ?? "unknown";
            var action = context.ActionDescriptor.RouteValues["action"]?.ToString() ?? "unknown";
            var key = $"{CacheConst.KeyIdempotent}{identity}:{controller}:{action}";
            var count = await _cache.IncrementAsync(key, TimeSpan.FromSeconds(attr.Seconds));
            if (count > 1)
            {
                throw new AppException(attr.Message, 429);
            }

            await next();
        }
    }
}
