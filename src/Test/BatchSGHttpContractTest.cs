// 批 S-G B4b:入站 HTTP 契约冒烟——全匿名,零手工 new Controller/反射路径,管线全真(路由/中间件/过滤器/序列化出口)。
// 判据口径均按"实读现网行为后固定"(批 O 6.11 模型验证出口 + PermissionMiddleware 门禁 + /health 探针),
// 断言注释标注对应机制落点;若后续行为面变更,本类红 = 契约变更信号,须同步更新注释口径而非直接放宽断言。
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AdminCore.Core.Exceptions;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AdminCore.Test;

public class BatchSGHttpContractTest : IClassFixture<AdminAppFactory>
{
    private readonly HttpClient _client;

    public BatchSGHttpContractTest(AdminAppFactory factory)
    {
        // 不自动跟随重定向:契约断言看本响应(UseHttpsRedirection 在 TestServer 无 https 端口,不重定向,仅告警)
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    /// <summary>
    /// 用例①:/health 就绪探针 ⇒ 200 且 body status=ok。实测口径(2026-10-26,同批 B4a 臂已把 /health 从静态应答
    /// 改造为依赖探活:DB=select 1/缓存=写读删往返,2 秒超时,依赖故障 ⇒ 503 + {"status":"degraded"}——原静态存活
    /// 语义迁至 /health/live 恒 200)。503 语义=依赖暂不就绪,而本宿主 PG 连的是 dev 共享库:并发臂跑全量回归时
    /// PG 会话瞬时打满(Resource temporarily unavailable,实测实锤)也会让探活瞬时不就绪——这不是持久故障。
    /// 故本用例在 30 秒窗口内轮询等待就绪,DB 正常即 200+ok;窗口耗尽仍降级才 Assert.Fail(启动降级/连不上库口径)。
    /// </summary>
    [Fact]
    public async Task 用例1_Health探针_返回200且statusOk()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string body = string.Empty;
        while (true)
        {
            using var response = await _client.GetAsync("/health");
            body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"/health 持续非 200(最终 {response.StatusCode},30s 窗口耗尽——启动降级或 dev 库持续不可达)body: {body}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// 用例②:匿名 GET /api/sysUser/page ⇒ 401 + errCode=common.unauthorized。
    /// 机制落点:PermissionMiddleware /api 全局门禁(未认证 → WriteResultAsync(401, ErrorCodes.Unauthorized)),
    /// 不触达控制器;errCode 用 ErrorCodes 常量(批 AQ:客户端编程判断面,不写死字符串)。
    /// </summary>
    [Fact]
    public async Task 用例2_匿名访问受保护分页_401并回带unauthorized错误码()
    {
        using var response = await _client.GetAsync("/api/sysUser/page");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ErrorCodes.Unauthorized, doc.RootElement.GetProperty("errCode").GetString());
    }

    /// <summary>用例③:匿名 POST /api/demo/validate ⇒ 401(S-G B1a「验证器演示端点收口为登录态」不回归;401 出口与用例②同型)。</summary>
    [Fact]
    public async Task 用例3_匿名POST演示校验端点_401()
    {
        using var content = new StringContent("""{"name":"probe","age":1}""", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/demo/validate", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ErrorCodes.Unauthorized, doc.RootElement.GetProperty("errCode").GetString());
    }

    /// <summary>
    /// 用例④:POST /api/auth/login 空 body/非法 JSON ⇒ 模型验证层出口(实读现网固定,2026-10-26):
    /// HTTP 200 + body code=400 + errCode=null。机制落点:[ApiController] 绑定失败 → InvalidModelStateResponseFactory
    /// (Program.cs 批 O 6.11)抛 AppException(msg, 400) → ExceptionMiddleware 业务异常分支 HTTP 200 + ApiResult.Fail;
    /// 与业务层失败(code=1 + errCode 稳定码)双口径区分——本断言即防两口径混淆的回潮。
    /// </summary>
    [Fact]
    public async Task 用例4_登录空体与非法JSON_模型验证层200_code400且errCode空()
    {
        // 变体 1:空 body(Content-Type 合法,体长 0 → "A non-empty request body is required")
        using (var empty = await _client.PostAsync(
                   "/api/auth/login", new StringContent(string.Empty, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var doc = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal(400, doc.RootElement.GetProperty("code").GetInt32());
            AssertNoErrCode(doc.RootElement);
        }

        // 变体 2:非法 JSON(反序列化异常进 ModelState → 同一 InvalidModelStateResponseFactory 出口)
        using (var bad = await _client.PostAsync(
                   "/api/auth/login", new StringContent("{bad json", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
            using var doc = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
            Assert.Equal(400, doc.RootElement.GetProperty("code").GetInt32());
            AssertNoErrCode(doc.RootElement);
        }
    }

    /// <summary>
    /// 用例⑤:不存在的 /api 路由 ⇒ 404(不 200/不 500)。实测口径(2026-10-26):匿名请求先撞 PermissionMiddleware
    /// /api 全局门禁(门禁先于路由 404 裁决)⇒ 401 + errCode=common.unauthorized;404 属已认证态语义
    /// (MapFallback 对 /api 前缀显式回 404,不吐 SPA HTML)。本断言按实测定死 401 口径:
    /// 匿名 401 = 门禁在场信号;若门禁被移除,匿名该路由将落 404,断言即红。
    /// </summary>
    [Fact]
    public async Task 用例5_不存在的Api路由_门禁先裁决401()
    {
        using var response = await _client.GetAsync("/api/__no_such_endpoint__");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ErrorCodes.Unauthorized, doc.RootElement.GetProperty("errCode").GetString());
    }

    /// <summary>errCode 缺席断言:序列化不落 null 字段或落 null 均算"无业务错误码"(兼容 AdminJsonOptions 的 null 策略)。</summary>
    private static void AssertNoErrCode(JsonElement root)
        => Assert.True(!root.TryGetProperty("errCode", out var errCode) || errCode.ValueKind == JsonValueKind.Null,
            $"模型验证层不应回带业务 errCode(业务层失败口径为 code=1 + errCode 稳定码),实测: {root.GetRawText()}");
}
