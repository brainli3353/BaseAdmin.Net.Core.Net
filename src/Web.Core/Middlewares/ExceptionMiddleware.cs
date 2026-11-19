using System.Text.Json;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminCore.Web.Core.Middlewares;

/// <summary>
/// 全局异常中间件：把请求管道中所有未处理异常统一转成 ApiResult JSON 响应（HTTP 200 + 业务码）。
/// - AppException → **错误码口径**(批 AQ)按请求语言渲染解释文案并回带 errCode;存量口径原样返回其消息。
///   两者均 Warning 日志、无堆栈(属预期分支，不进异常档)
/// - 其他未处理异常 → 记完整日志（含堆栈）＋发布 "ex_log" 事件异步落库异常档（5.1 SysLogEx），
///   响应体仅返回通用文案，绝不泄露堆栈
/// 注册位置：Program.cs 中 UseRouting 之前，覆盖所有请求。
/// </summary>
public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly IEventPublisher _publisher;
    private readonly IServiceProvider _serviceProvider;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IEventPublisher publisher, IServiceProvider serviceProvider)
    {
        _next = next;
        _logger = logger;
        _publisher = publisher;
        _serviceProvider = serviceProvider;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            // 批 AQ:错误码口径 → 按**请求语言**渲染解释文案(资源见 Lang.*.json);存量口径(ErrorCode=null)沿用原消息,行为不变
            var errCode = ex.ErrorCode;
            var message = string.IsNullOrEmpty(errCode) ? ex.Message : ResolveLocalized(context, errCode, ex.Args);

            // 业务异常属预期分支：只记 Warning（不记堆栈，不进异常档——5.17/5.1 口径）
            _logger.LogWarning("业务异常 Code={Code} ErrCode={ErrCode}: {Message}", ex.Code, errCode ?? "-", message);
            if (!context.Response.HasStarted)
            {
                // 响应头未发出才能改写；已发出则无补救空间，静默退出（防御分支）
                await WriteResultAsync(context, ApiResult.Fail(message, ex.Code, errCode));
            }
        }
        catch (BadRequestException ex)
        {
            // 批 AQ 修正:契约=真 HTTP 400(此前无专分支、落入通用 catch 变 500);
            // 批 AR-5:错误码口径 → 按请求语言渲染解释文案并回带 errCode;存量口径(ErrorCode=null)沿用原消息
            var errCode = ex.ErrorCode;
            var message = string.IsNullOrEmpty(errCode) ? ex.Message : ResolveLocalized(context, errCode, ex.Args, ex.Message);
            _logger.LogWarning("请求无效 ErrCode={ErrCode}: {Message}", errCode ?? "-", message);
            if (!context.Response.HasStarted)
            {
                await WriteResultAsync(context,
                    ApiResult.Fail(message, StatusCodes.Status400BadRequest, errCode),
                    null,
                    StatusCodes.Status400BadRequest);
            }
        }
        catch (Exception ex)
        {
            // 批 S-G B2b:DB 唯一约束冲突(23505)先于 500 兜底映射——并发插入重复唯一键是可预期业务态,
            // 按业务失败响应(HTTP 200 + code=1 + errCode=common.conflict,同全仓业务异常口径)并记 Warning,
            // 不进 500 文案/异常档(ex_log);未命中(其余一切未处理异常)走下方原逻辑,行为不变
            if (DbConflictMapper.TryMapUniqueViolation(ex, out var conflictErrCode))
            {
                // 文案走既有本地化出口(Lang 已配 common.conflict);fallback 留空回落 errCode 本身(不写死中文,批 AQ/AR 口径)
                _logger.LogWarning("唯一约束冲突: {Method} {Path} {Message}", context.Request.Method, context.Request.Path, ex.Message);
                if (!context.Response.HasStarted)
                {
                    await WriteResultAsync(context, ApiResult.Fail(ResolveLocalized(context, conflictErrCode, null), 1, conflictErrCode));
                }

                return;
            }

            // 未处理异常：完整堆栈只进日志（LogError 自动携带），响应体给通用文案
            _logger.LogError(ex, "未处理异常: {Method} {Path}", context.Request.Method, context.Request.Path);

            // 中间件由 root provider 构造(UseMiddleware):scoped 的 UserManager 经逐请求 scope 解析
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager>();

            // 异常档(5.1):发布 ex_log 事件,总线后台异步落库 SysLogEx(与操作档同范式,发布失败仅记日志绝不回流主响应)
            var log = new SysLogEx
            {
                ControllerName = context.Request.RouteValues["controller"]?.ToString(),
                ActionName = context.Request.RouteValues["action"]?.ToString(),
                HttpMethod = context.Request.Method,
                RequestUrl = context.Request.Path + context.Request.QueryString,
                RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
                // 不缓冲请求 body(参照口径亦不记 body;裁决见 docs/研究/g3_refer_research.md §9 ③)
                RequestParam = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty,
                ReturnResult = null, // WriteResultAsync 后补(见下)
                TraceId = context.TraceIdentifier,
                ThreadId = Environment.CurrentManagedThreadId,
                Account = userManager.Account,
                RealName = userManager.RealName,
                Exception = $"异常信息：{ex.Message} \n异常来源：{ex.Source} \n堆栈信息：{ex.StackTrace}",
                LogLevel = "Error"
            };

            if (!context.Response.HasStarted)
            {
                // 批 AQ:通用兜底文案同样走错误码(按请求语言渲染;errCode 供客户端识别);
                // 本地化不可用时回落原文案(最小宿主/单测直接构造中间件场景)
                await WriteResultAsync(context, ApiResult.Fail(ResolveLocalized(context, ErrorCodes.Busy, null, "系统繁忙，请稍后重试"), 500, ErrorCodes.Busy), log);
            }

            await PublishExLogAsync(log);
        }
    }

    /// <summary>
    /// 发布 ex_log 事件(5.1 异步化):入队即返回,不等待落库——实际落库由 ExLogSubscriber 在总线后台消费完成。
    /// 发布失败(极端背压/入队异常)仅记日志,绝不回流主响应(审计可容忍丢失,业务优先;同 OpLogFilter 口径)。
    /// </summary>
    private async Task PublishExLogAsync(SysLogEx log)
    {
        try
        {
            await _publisher.PublishAsync("ex_log", log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "异常日志事件发布失败 {Controller}/{Action}", log.ControllerName, log.ActionName);
        }
    }

    /// <summary>按请求语言渲染错误码文案(批 AQ):服务从**请求容器**解析(scoped 安全);命中不到则回落
    /// <paramref name="fallback"/>,再回落错误码本身(可检出漏配)。RequestServices 可能为空(最小宿主/单测直接构造)。</summary>
    private static string ResolveLocalized(HttpContext context, string errorCode, IReadOnlyDictionary<string, object?>? args, string? fallback = null)
    {
        var localization = context.RequestServices?.GetService<ILocalizationService>();
        var text = localization?.Get(errorCode, args);
        if (!string.IsNullOrEmpty(text) && !string.Equals(text, errorCode, StringComparison.Ordinal))
        {
            return text;
        }

        return fallback ?? text ?? errorCode;
    }

    /// <summary>写出统一 JSON 响应:默认 HTTP 200(业务异常口径),可指定状态码(批 AQ:BadRequestException 用真 400);
    /// application/json; charset=utf-8;异常档 ReturnResult 回填实际写出内容。</summary>
    private static Task WriteResultAsync(HttpContext context, ApiResult result, SysLogEx? log = null, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(result, ApiResult.Json);
        if (log is not null)
        {
            log.ReturnResult = json;
        }

        return context.Response.WriteAsync(json);
    }
}
