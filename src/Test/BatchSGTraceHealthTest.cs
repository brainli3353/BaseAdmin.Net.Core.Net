using System.Net;
using AdminCore.Core.Models;
using AdminCore.Web.Core.Auth;
using AdminCore.Web.Core.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace AdminCore.Test;

// 批 S-G B4a 零库测试:TraceId 贯通(中间件响应头)/慢请求判定(纯函数)/健康三态映射(纯函数)。
// 无 PG/Redis 依赖,可 --filter "FullyQualifiedName~BatchSGTraceHealthTest" 单跑;
// TraceIdMiddleware 走 TestServer 最小管线——DefaultHttpContext 无真实服务器,OnStarting 回调
// 须由宿主首写触发,内存宿主永不发生,故用真响应管线验证(等价生产端 Kestrel 执行点)。
public class BatchSGTraceHealthTest
{
    // ── TraceIdMiddleware(经 TestServer 真实响应管线触发 OnStarting;DefaultHttpContext 不触发宿主回调) ──

    [Fact]
    public async Task 中间件_正常TraceIdentifier_响应头同值()
    {
        var (resp, serverTraceId) = await RunThroughPipelineAsync(clearTraceIdentifier: false);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(string.IsNullOrEmpty(serverTraceId));
        Assert.Equal(serverTraceId, resp.Headers.GetValues("X-Trace-Id").Single());
    }

    [Fact]
    public async Task 中间件_空TraceIdentifier_回落非空Guid()
    {
        var (resp, _) = await RunThroughPipelineAsync(clearTraceIdentifier: true);
        var header = resp.Headers.GetValues("X-Trace-Id").Single();
        Assert.False(string.IsNullOrEmpty(header));
        Assert.True(Guid.TryParse(header, out _), $"回落值应为 Guid 格式,实际:{header}");
    }

    [Fact]
    public async Task 中间件_错误响应同样带头()
    {
        // 下游置 500(模拟异常统一出口的响应)→ 错误响应同样携带 X-Trace-Id
        var (resp, serverTraceId) = await RunThroughPipelineAsync(clearTraceIdentifier: false, statusCode: 500);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(serverTraceId, resp.Headers.GetValues("X-Trace-Id").Single());
    }

    /// <summary>最小管道:可选前置中间件清空 TraceIdentifier → TraceIdMiddleware → 终止端点。</summary>
    private static async Task<(HttpResponseMessage Response, string ServerTraceId)> RunThroughPipelineAsync(
        bool clearTraceIdentifier, int statusCode = 200)
    {
        var captured = string.Empty;
        using var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            if (clearTraceIdentifier)
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.TraceIdentifier = string.Empty;
                    await next();
                });
            }

            app.UseMiddleware<TraceIdMiddleware>();
            app.Run(ctx =>
            {
                captured = ctx.TraceIdentifier;
                ctx.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            });
        }));
        using var client = server.CreateClient();
        var resp = await client.GetAsync("/probe");
        return (resp, captured);
    }

    // ── IsSlowRequest(程序集特性 InternalsVisibleTo 直测 internal 面) ──

    [Theory]
    [InlineData(999, 1000, false)]  // 未达阈值
    [InlineData(1000, 1000, false)] // 等于阈值不算慢(严格大于才告警)
    [InlineData(1001, 1000, true)]  // 超过阈值算慢
    [InlineData(5000, 0, false)]    // 阈值 0 = 关闭
    [InlineData(5000, -1, false)]   // 负阈值 = 关闭
    public void IsSlowRequest_判定口径(int elapsedMs, int threshold, bool expected)
    {
        Assert.Equal(expected, OpLogFilter.IsSlowRequest(elapsedMs, threshold));
    }

    // ── HealthStatus.ToResult 三态映射 ──

    [Fact]
    public void 健康映射_双就绪_200ok()
    {
        var r = HealthStatus.ToResult(true, true, "redis");
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("ok", r.Status);
        Assert.Equal("ok", r.Db);
        Assert.Equal("redis", r.Cache);
    }

    [Fact]
    public void 健康映射_db失败_503降级()
    {
        var r = HealthStatus.ToResult(false, true, "redis");
        Assert.Equal(503, r.StatusCode);
        Assert.Equal("degraded", r.Status);
        Assert.Equal("fail", r.Db);
        Assert.Equal("ok", r.Cache); // brief 口径:降级档两依赖字段固定 ok|fail 枚举(缓存类型仅 200 档透传)
    }

    [Fact]
    public void 健康映射_cache失败_503且失败档不透传类型()
    {
        // cacheType 传入"疑似异常原文"的污染串:失败档必须丢弃、固定回 "fail"——
        // 机制化保证降级响应不泄露异常原文/连接串(单源语义见 HealthStatus remarks)。
        var r = HealthStatus.ToResult(true, false, "Exception: password=secret host=internal");
        Assert.Equal(503, r.StatusCode);
        Assert.Equal("degraded", r.Status);
        Assert.Equal("ok", r.Db);
        Assert.Equal("fail", r.Cache);
        Assert.DoesNotContain("password", r.Cache);
    }

    [Fact]
    public void 健康映射_双失败_503()
    {
        var r = HealthStatus.ToResult(false, false, "redis");
        Assert.Equal(503, r.StatusCode);
        Assert.Equal("degraded", r.Status);
        Assert.Equal("fail", r.Db);
        Assert.Equal("fail", r.Cache);
    }
}
