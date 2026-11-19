using AdminCore.Core.Attributes;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 批 S-G B4a:测试程序集可访问 internal 面(仅 OpLogFilter.IsSlowRequest 慢请求判定,零库单测用)
[assembly: InternalsVisibleTo("AdminCore.Test")]

namespace AdminCore.Web.Core.Auth
{
    /// <summary>
    /// 操作日志过滤器(契约 §6):全局 IAsyncActionFilter,记录控制器端点的 账号/动作/方法/IP/耗时/成败,
    /// 以「发布 op_log 事件」交事件总线异步落库 SysLogOp(落库由 OpLogSubscriber 在总线后台消费,按 [LogTable] 选库)。
    /// </summary>
    /// <remarks>
    /// 成败口径(契约"是否成功(异常状态码)"):动作抛异常(未处理)或非 2xx 响应 = 失败。
    /// WHY 必须看 executed.Exception 而不能只看 HTTP 状态码:本项目统一响应约定为 HTTP 200 + 业务码
    /// (异常中间件把异常也映射成 200),状态码单独不足以区分成败。
    /// 异步化(task_014):由原 fire-and-forget 直写仓储改为发布事件——响应「入队即返回」不等待落库,
    /// 落库在总线后台消费(失败重试≤2 后告警),彻底解耦请求链路与日志落库;发布失败仅记日志不回流主响应。
    /// 落库时机在请求结束后,审计 AOP 取不到用户上下文 → 本表 CreateUserId 允许为空(账号已单独记 Account 字段)。
    /// </remarks>
    [Service(ServiceType = typeof(OpLogFilter), Lifetime = ServiceLifetime.Transient)]
    public class OpLogFilter : IAsyncActionFilter
    {
        private readonly ILogger<OpLogFilter> _logger;
        private readonly IEventPublisher _publisher;

        private readonly UserManager _userManager; // 批 D 1.14:账号读取经门面收敛
        private readonly SysConfigService _configService; // 批 I 5.1:sys_oplog 开关(参照 DatabaseLoggingWriter 前置检查口径)

        public OpLogFilter(ILogger<OpLogFilter> logger, IEventPublisher publisher, UserManager userManager, SysConfigService configService)
        {
            _logger = logger;
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _userManager = userManager;
            _configService = configService;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var sw = Stopwatch.StartNew();
            var httpContext = context.HttpContext;
            Exception? exception = null;
            var statusCode = StatusCodes.Status200OK;
            IActionResult? actionResult = null;

            try
            {
                var executed = await next();
                actionResult = executed.Result;
                statusCode = httpContext.Response.StatusCode;
                // 异常已被内层过滤器处理(ExceptionHandled)→ 请求有正常响应,记成功
                if (executed.Exception is not null && !executed.ExceptionHandled)
                {
                    exception = executed.Exception;
                }
            }
            catch (Exception ex)
            {
                // 动作异常冒泡到过滤器:记录后原样上抛,交给全局异常中间件统一转 ApiResult(保持管道语义不变)
                exception = ex;
                statusCode = httpContext.Response.StatusCode;
                throw;
            }
            finally
            {
                sw.Stop();

                var controller = httpContext.Request.RouteValues["controller"]?.ToString();
                var action = httpContext.Request.RouteValues["action"]?.ToString();
                var log = new SysLogOp
                {
                    ControllerName = controller,
                    ActionName = action,
                    HttpMethod = httpContext.Request.Method,
                    RemoteIp = httpContext.Connection.RemoteIpAddress?.ToString(),
                    ElapsedMs = (int)sw.Elapsed.TotalMilliseconds,
                    // 批 D 1.14:账号经 UserManager 门面(匿名 → anonymous 口径不变)
                    Account = _userManager.Account ?? "anonymous",
                    // 批 S-G B4a:链路贯通——与响应头 X-Trace-Id/异常档 SysLogEx.TraceId 同源(截断 64 防超列宽)
                    TraceId = string.IsNullOrEmpty(httpContext.TraceIdentifier) ? null : Truncate(httpContext.TraceIdentifier!, 64),
                    Success = exception is null && statusCode is >= 200 and < 300,
                    // 批 AZ 参照口径(参照 DatabaseLoggingWriter 记方法参数值/返回对象值,不缓冲原始 body——裁决 g3 §9③ 的补全):
                    // 鉴权端点(密码/动态码)豁免参数采集;采集失败仅告警不阻断(审计可容忍丢失,业务优先)
                    // 安全出口止血:采集到的参数/返回结果入库前统一过 LogDataRedactor(敏感属性名/otpauth 遮蔽+长度截断),
                    // 修复 ReturnResult 完全不豁免、改密/重置密钥入参明文、SysMenu/Page 整包入日志三类泄露。
                    RequestParam = IsExemptAction(controller, action) ? null : LogDataRedactor.Redact(CaptureRequestParam(context)),
                    ReturnResult = LogDataRedactor.Redact(CaptureReturnResult(actionResult))
                };

                await PublishOpLogAsync(log);

                // 批 S-G B4a 慢请求告警:阈值 Logging:SlowRequestMs(缺省 1000;<=0 关闭),超阈值打一行 Warning
                // 便于检索(TraceId 可回关联响应头/异常档;此前只记 ElapsedMs 无主动告警)
                if (IsSlowRequest(log.ElapsedMs, ResolveSlowRequestThreshold(httpContext)))
                {
                    _logger.LogWarning(
                        "慢请求 TraceId={TraceId} Method={Method} Path={Path} ElapsedMs={ElapsedMs} Account={Account}",
                        log.TraceId, log.HttpMethod, httpContext.Request.Path, log.ElapsedMs, log.Account);
                }
            }
        }

        /// <summary>
        /// 慢请求阈值解析(批 S-G B4a):配置 Logging:SlowRequestMs,缺省 1000。
        /// WHY 请求期经 RequestServices 解析而非构造注入:不占构造签名——既有测试手 new 四参构造
        /// (BatchAZOpLogDetailTest/BatchILogExTest,白名单外禁改)不受影响;IConfiguration 为单例,解析零开销。
        /// RequestServices 为空(最小宿主/自构造 HttpContext)或键缺失时回落缺省 1000。
        /// </summary>
        private static int ResolveSlowRequestThreshold(HttpContext httpContext)
        {
            var config = httpContext.RequestServices?.GetService<IConfiguration>();
            return config?.GetValue("Logging:SlowRequestMs", 1000) ?? 1000;
        }

        /// <summary>
        /// 慢请求判定(批 S-G B4a,纯函数零依赖便于零库测试):严格大于阈值才算慢(等于阈值不告警);
        /// 阈值 &lt;=0 = 告警关闭。
        /// </summary>
        internal static bool IsSlowRequest(int elapsedMs, int threshold)
        {
            return threshold > 0 && elapsedMs > threshold;
        }

        /// <summary>字符串截断(链路 ID 等定长列防超列宽,批 S-G B4a)。</summary>
        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        /// <summary>
        /// 参数采集豁免(凭据纪律,全局公约 §5):登录两阶段请求含密码/动态码明文,不得入审计库。
        /// 路由层定位(实测 RouteValues:ControllerName=Auth、ActionName=Login|LoginOtp)。
        /// </summary>
        private static bool IsExemptAction(string? controller, string? action)
        {
            return string.Equals(controller, "Auth", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "LoginOtp", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 请求参数采集(参照口径:方法参数值 JSON——内存中已绑定的 ActionArguments,零 EnableBuffering):
        /// 不可序列化值(文件/流/上下文等)以占位标记替代;整体序列化失败返回 null(不阻断请求)。
        /// </summary>
        private static string? CaptureRequestParam(ActionExecutingContext context)
        {
            try
            {
                var args = new Dictionary<string, object?>();
                foreach (var kv in context.ActionArguments)
                {
                    args[kv.Key] = kv.Value is IFormFile file ? $"[file:{file.FileName}]"
                        : kv.Value is IFormFileCollection files ? $"[files:{files.Count}]"
                        : kv.Value is Stream ? "[stream]"
                        : kv.Value is HttpContext ? "[httpContext]"
                        : kv.Value is IServiceProvider ? "[serviceProvider]"
                        : kv.Value is IFormCollection ? "[formCollection]"
                        : kv.Value;
                }
                return args.Count > 0 ? JsonSerializer.Serialize(args, JsonOptions) : null;
            }
            catch (Exception)
            {
                return null; // 序列化失败(循环引用等)宁缺勿错,绝不阻断请求
            }
        }

        /// <summary>
        /// 返回结果采集(参照口径:返回对象值 JSON——ObjectResult.Value,即统一响应 ApiResult 载体;
        /// 动作异常/非 ObjectResult 结果无值可采,返回 null——失败请求全文见异常日志 ex 档)。
        /// </summary>
        private static string? CaptureReturnResult(IActionResult? actionResult)
        {
            try
            {
                if (actionResult is ObjectResult objectResult && objectResult.Value is not null)
                {
                    return JsonSerializer.Serialize(objectResult.Value, JsonOptions);
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 与全局输出格式同口径(Program.cs AddJsonOptions):camelCase + 非 ASCII 原样,日志内容贴近实际响应
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 发布 op_log 事件(task_014 异步化):入队即返回,不等待落库——实际落库由 OpLogSubscriber 在总线后台消费完成。
        /// 发布失败(极端背压/入队异常)仅记日志,绝不回流主响应(审计可容忍丢失,业务优先)。
        /// </summary>
        private async Task PublishOpLogAsync(SysLogOp log)
        {
            // sys_oplog 开关(批 I 5.1,参照 DatabaseLoggingWriter 落库前置检查口径):关→不发事件(零总线空跑)。
            // 缺失/解析失败=fail-open(记录,裁决 docs/研究/g3_refer_research.md §9 ⑦):审计面漏记是隐性缺陷,
            // 种子(SysConfigSeedData)保证正常路径 sys_oplog=True 时行为与参照一致。
            try
            {
                var value = await _configService.GetConfigValueByCodeAsync(SysConfigService.SysOpLog);
                if (bool.TryParse(value, out var enabled) && !enabled)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 开关读取失败(缓存/库瞬断)不阻断记录路径:按开处理(fail-open),异常日志已由本方法外层吞掉
            }

            try
            {
                await _publisher.PublishAsync("op_log", log);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "操作日志事件发布失败 {Controller}/{Action}", log.ControllerName, log.ActionName);
            }
        }
    }
}
