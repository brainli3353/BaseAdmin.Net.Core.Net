using Microsoft.AspNetCore.Http;

namespace AdminCore.Web.Core.Middlewares;

/// <summary>
/// 链路贯通中间件(批 S-G B4a):把 <see cref="HttpContext.TraceIdentifier"/> 写进响应头 X-Trace-Id,
/// 使 500 排障可跨「响应头 ↔ 操作日志(SysLogOp.TraceId) ↔ 异常档(SysLogEx.TraceId)」三面用同一 id 串联
/// ——此前 TraceId 只进异常档,操作日志/响应头均无此 id,只能按时间+路径猜。
/// </summary>
/// <remarks>
/// 注册位置:Program.cs 管线**最前**(早于 ExceptionMiddleware)——OnStarting 回调在响应首字节发出前触发,
/// 覆盖**所有**响应(正常/异常统一出口/404 无 body,只要响应启动就会带头,错误响应同样覆盖)。
/// TraceIdentifier 为空(最小宿主/自构造 HttpContext 等场景)时回落新 Guid,保证头值恒非空;
/// 下游已显式设置 X-Trace-Id 时不覆盖。
/// </remarks>
public class TraceIdMiddleware
{
    private const string HeaderName = "X-Trace-Id";

    private readonly RequestDelegate _next;

    public TraceIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = context.TraceIdentifier;
        if (string.IsNullOrEmpty(traceId))
        {
            traceId = Guid.NewGuid().ToString();
        }

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = traceId;
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
