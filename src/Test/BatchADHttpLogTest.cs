using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Logging;
using AdminCore.Core.Option;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AD「Http 出站日志(5.3)」测试:被测主体 = <see cref="HttpLoggingHandler"/>(记录 + 发布 http_log)、
/// <see cref="HttpLogSubscriber"/>(事件落库 sys_log_http)、<see cref="SysLogHttpService"/>(分页/详情查询侧)。
/// </summary>
/// <remarks>
/// WHY 用例 1–7 用**本机 stub HTTP 服务端**(HttpListener + 随机空闲端口)而非替身 HttpMessageHandler:
/// 被测件挂在 HttpClient 真实管道上,状态码/响应体/耗时/连接失败都必须是**真网络行为**的产物——
/// 替身 handler 只能证明"记录了替身给的值",证明不了管道语义(拿被测机制的输入证明被测机制,同义反复)。
/// WHY 用例 8/9 需真实 PG:落库与分页过滤是 SQL 语义,内存替身覆盖不到;PG 不可达时 Assert.Fail 显式降级,
/// 禁止静默通过(同批 AB 口径)。
/// 用例间零污染:stub 服务端 using 停;PG 探针行 finally 物理删;端口随机取空闲口,避免与并行/他处占用冲突。
/// </remarks>
public class BatchADHttpLogTest
{
    private readonly ITestOutputHelper _output;

    public BatchADHttpLogTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchABTenantDbTest/BatchCTenantTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchABTenantDbTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // stub 服务端的固定响应体(23 字符:同时供"超长截断"用例当被测输入)
    private const string StubResponseJson = """{"ok":true,"from":"stub"}""";

    // ─────────────────────────────────────────────────────────────
    // 1. 开关开启:出站请求全要素被记录并发布一条 http_log
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关开启_记录出站请求全要素()
    {
        var publisher = new FakePublisher();
        using var stub = new StubServer(StubResponseJson);
        stub.Start();
        using var client = CreateClient(new HttpLogOptions { Enabled = true }, publisher);

        const string requestJson = """{"name":"批AD","n":1}""";
        using var response = await client.PostAsync(
            stub.BaseUrl.TrimEnd('/') + "/api/probe?x=1",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));

        // 业务侧不受影响:状态码与响应体原样可用(体已被 handler 读过一次,仍必须可再读)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StubResponseJson, await response.Content.ReadAsStringAsync());

        var log = SingleHttpLog(publisher);
        Assert.Equal("POST", log.HttpMethod);
        Assert.Contains(stub.BaseUrl.TrimEnd('/'), log.RequestUrl);
        Assert.Contains("/api/probe?x=1", log.RequestUrl);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.ElapsedMs);
        Assert.True(log.ElapsedMs >= 0, $"ElapsedMs 应为非负,实际 {log.ElapsedMs}");
        Assert.Contains("批AD", log.RequestBody);
        Assert.Contains("stub", log.ResponseBody);
        Assert.StartsWith("application/json", ParseHeaders(log.RequestHeaders!)["Content-Type"]);

        // 未发生异常时:HasException=false 且不留异常全文(库内不落假异常)
        Assert.False(log.HasException);
        Assert.Null(log.Exception);

        // 可选依赖降级口径:未注入 IHttpContextAccessor(后台任务发起的出站调用)→ 身份/TraceId 为空、不抛
        Assert.Null(log.Account);
        Assert.Null(log.TraceId);

        _output.WriteLine($"判定:POST {log.RequestUrl} → {log.StatusCode} {log.ElapsedMs}ms,req={log.RequestBody}");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 请求/响应体超长:按 MaxBodyLength 截断并追加 "(truncated)"
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 请求体超长_按MaxBodyLength截断()
    {
        var publisher = new FakePublisher();
        using var stub = new StubServer(StubResponseJson);
        stub.Start();
        using var client = CreateClient(new HttpLogOptions { Enabled = true, MaxBodyLength = 10 }, publisher);

        const string requestJson = """{"alpha":"0123456789abcdefghij"}""";
        Assert.True(requestJson.Length > 10, "前置:请求体必须长于 MaxBodyLength,否则用例退化为恒真");

        using var response = await client.PostAsync(
            stub.BaseUrl.TrimEnd('/') + "/api/probe",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = SingleHttpLog(publisher);

        // 截断口径:前 MaxBodyLength 个字符 + "(truncated)"(按字符截断,不切多字节字符)
        Assert.EndsWith("(truncated)", log.RequestBody);
        Assert.Equal(requestJson[..10] + "(truncated)", log.RequestBody);
        Assert.Equal(10 + "(truncated)".Length, log.RequestBody!.Length);

        // 响应体同口径(同一 ReadBodyAsync 单点)
        Assert.Equal(StubResponseJson[..10] + "(truncated)", log.ResponseBody);
        Assert.DoesNotContain("abcdefghij", log.RequestBody); // 超长部分确实被丢弃

        _output.WriteLine($"判定:RequestBody={log.RequestBody} / ResponseBody={log.ResponseBody}");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. MaxBodyLength<=0:不记录体(记实现的实际占位文案)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task MaxBodyLength非正_不记录体()
    {
        var publisher = new FakePublisher();
        using var stub = new StubServer(StubResponseJson);
        stub.Start();
        using var client = CreateClient(new HttpLogOptions { Enabled = true, MaxBodyLength = 0 }, publisher);

        const string requestJson = """{"secret":"body-should-not-be-recorded"}""";
        using var response = await client.PostAsync(
            stub.BaseUrl.TrimEnd('/') + "/api/probe",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = SingleHttpLog(publisher);

        // 占位文案取自实现(HttpLoggingHandler.ReadBodyAsync):"(未记录:MaxBodyLength<=0)"
        Assert.Equal("(未记录:MaxBodyLength<=0)", log.RequestBody);
        Assert.Equal("(未记录:MaxBodyLength<=0)", log.ResponseBody);

        // 关键安全性:开关置零后载荷**不得**进入日志
        Assert.DoesNotContain("body-should-not-be-recorded", log.RequestBody);
        Assert.DoesNotContain("stub", log.ResponseBody);

        // 其余要素仍记录(方法/URL/状态码/耗时没有被"不记体"影响)
        Assert.Equal("POST", log.HttpMethod);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.ElapsedMs);

        _output.WriteLine($"判定:RequestBody={log.RequestBody}(体未落日志)");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 敏感请求头:默认掩码集合 + 配置追加掩码头,值一律 "***",明文不出现在日志 JSON
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 敏感请求头_掩码不外泄()
    {
        var publisher = new FakePublisher();
        using var stub = new StubServer(StubResponseJson);
        stub.Start();
        // MaskHeaders 是**追加**项(Authorization/Cookie 属默认集合,配置不必重复声明)
        using var client = CreateClient(new HttpLogOptions { Enabled = true, MaskHeaders = ["X-Secret"] }, publisher);

        using var request = new HttpRequestMessage(HttpMethod.Get, stub.BaseUrl.TrimEnd('/') + "/api/probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.Add("Cookie", "a=b");
        request.Headers.Add("X-Trace", "abc");
        request.Headers.Add("X-Secret", "topsecret");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = SingleHttpLog(publisher);
        var headers = ParseHeaders(log.RequestHeaders!);

        // 默认掩码集合命中
        Assert.Equal("***", headers["Authorization"]);
        Assert.Equal("***", headers["Cookie"]);
        // 配置追加掩码头命中(大小写不敏感)
        Assert.Equal("***", headers["X-Secret"]);
        // 普通头原值保留(掩码不得误伤)
        Assert.Equal("abc", headers["X-Trace"]);

        // 原始 JSON 层面复核:凭据明文绝不出现在请求头字段
        Assert.DoesNotContain("secret-token", log.RequestHeaders);
        Assert.DoesNotContain("topsecret", log.RequestHeaders);
        Assert.DoesNotContain("a=b", log.RequestHeaders);

        _output.WriteLine($"判定:RequestHeaders={log.RequestHeaders}");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 开关关闭(缺省口径):零记录,请求照常成功
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关关闭_零记录()
    {
        var publisher = new FakePublisher();
        using var stub = new StubServer(StubResponseJson);
        stub.Start();

        // 缺省 Enabled=false(不显式赋值,钉住"框架默认零副作用")
        Assert.False(new HttpLogOptions().Enabled);
        using var client = CreateClient(new HttpLogOptions(), publisher);

        using var response = await client.PostAsync(
            stub.BaseUrl.TrimEnd('/') + "/api/probe",
            new StringContent("""{"a":1}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StubResponseJson, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, publisher.Count);

        _output.WriteLine("判定:Enabled=false → 发布 0 条,请求仍 200");
    }

    // ─────────────────────────────────────────────────────────────
    // 6. ExcludeHosts 主机后缀匹配:命中即不记录,请求照常成功
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 排除主机_不记录()
    {
        using var stub = new StubServer(StubResponseJson);
        stub.Start();
        var url = stub.BaseUrl.TrimEnd('/') + "/api/probe";

        // ① 精确主机名命中(127.0.0.1)
        var excluded = new FakePublisher();
        using (var client = CreateClient(new HttpLogOptions { Enabled = true, ExcludeHosts = ["127.0.0.1"] }, excluded))
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(StubResponseJson, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, excluded.Count);

        // ② 后缀匹配语义:"0.0.1" 是主机 "127.0.0.1" 的后缀 → 同样排除(证明是 EndsWith 而非相等)
        var suffixExcluded = new FakePublisher();
        using (var client = CreateClient(new HttpLogOptions { Enabled = true, ExcludeHosts = ["0.0.1"] }, suffixExcluded))
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(0, suffixExcluded.Count);

        // ③ 反向取证:非后缀("127.0.0.2")不排除 → 必须记录(否则②的"命中"等于"一律不记")
        var recorded = new FakePublisher();
        using (var client = CreateClient(new HttpLogOptions { Enabled = true, ExcludeHosts = ["127.0.0.2"] }, recorded))
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Single(recorded.Items);

        _output.WriteLine("判定:127.0.0.1/0.0.1 命中后缀 → 0 条;127.0.0.2 不命中 → 1 条");
    }

    // ─────────────────────────────────────────────────────────────
    // 7. 出站异常:记录(HasException + 异常全文 + StatusCode=null)后**原样重抛**
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 出站异常_记录后原样重抛()
    {
        // ① 真实网络失败:未监听端口(连接被拒)→ HttpClient 抛 HttpRequestException(或取消类)
        var publisher = new FakePublisher();
        using (var client = CreateClient(new HttpLogOptions { Enabled = true }, publisher))
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://127.0.0.1:1/api/none"));
            Assert.True(thrown is HttpRequestException or TaskCanceledException,
                $"出站失败应抛 HttpRequestException/TaskCanceledException,实际 {thrown.GetType().FullName}:{thrown.Message}");
        }

        var log = SingleHttpLog(publisher);
        Assert.True(log.HasException);
        Assert.False(string.IsNullOrWhiteSpace(log.Exception));
        Assert.Null(log.StatusCode);          // 未拿到响应 → 状态码为空
        Assert.NotNull(log.ElapsedMs);
        Assert.True(log.ElapsedMs >= 0);
        Assert.Contains("127.0.0.1:1", log.RequestUrl);

        // ② "原样"重抛的强证明:同一异常实例(未被包装/替换/吞掉)。
        //    真实端口失败时异常实例在建连内部产生、测试侧无法先行持有,故此处用会抛指定实例的内层 handler,
        //    验证 catch → 发布 → throw; 之后逃逸的最外层异常与内层抛出的**同一实例**。
        var boom = new InvalidOperationException("批AD 探针:出站异常原样重抛");
        var publisher2 = new FakePublisher();
        using (var client = CreateClient(new HttpLogOptions { Enabled = true }, publisher2, new ThrowingHandler(boom)))
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:9/api/boom"));
            Assert.Same(boom, caught);
        }

        var log2 = SingleHttpLog(publisher2);
        Assert.True(log2.HasException);
        Assert.Contains("批AD 探针", log2.Exception);
        Assert.Null(log2.StatusCode);

        _output.WriteLine($"判定:异常被记录(HasException=true/Exception 非空/StatusCode=null)且原样重抛;记录到的异常首段={log.Exception?.Split('\n')[0]}");
    }

    // ─────────────────────────────────────────────────────────────
    // 8. 订阅者落库(真实 PG):HttpLogSubscriber 消费载荷 → sys_log_http 出现该行
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 订阅者落库_真实PG()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        // WHY 经容器解析(而非 new):与事件总线消费口径一致(总线按具体类型从作用域解析订阅者)
        var subscriber = sp.GetRequiredService<HttpLogSubscriber>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysLogHttp>>();

        var tag = $"ad_http_pg_{Guid.NewGuid().ToString("N")[..8]}";
        var log = new SysLogHttp
        {
            HttpMethod = "POST",
            RequestUrl = "https://ad-probe.local/pg/sink?id=1",
            RequestHeaders = """{"Authorization":"***","X-Trace":"abc"}""",
            RequestBody = $"body-{tag}",
            StatusCode = 200,
            ResponseBody = $"resp-{tag}",
            ElapsedMs = 12,
            HasException = false,
            Account = tag,
            RealName = "探针批AD",
            TraceId = "ad-probe-trace"
        };

        try
        {
            await subscriber.HandleHttpLogAsync(log);
            Assert.True(log.Id > 0, "落库主键应由 AOP 自动填雪花");

            // 仓储口径回读:关键字段全等
            var row = await repo.GetFirstAsync(u => u.Id == log.Id);
            Assert.NotNull(row);
            Assert.Equal("POST", row!.HttpMethod);
            Assert.Equal(200, row.StatusCode);
            Assert.Equal(tag, row.Account);
            Assert.Equal($"body-{tag}", row.RequestBody);
            Assert.Equal($"resp-{tag}", row.ResponseBody);
            Assert.False(row.HasException);

            // 原生 SQL 独立复核(不经仓储映射):该行确实物理落在 sys_log_http
            var hit = await dbScope.Db.Ado.SqlQueryAsync<int>(
                "SELECT count(*)::int FROM \"sys_log_http\" WHERE id = @id",
                new SugarParameter("@id", log.Id));
            Assert.Equal(1, hit[0]);

            _output.WriteLine($"判定:HttpLogSubscriber 落库 id={log.Id} account={tag},原生 SQL 命中 {hit[0]} 行");
        }
        finally
        {
            // 物理清理探针行(软删会留行污染后续行数断言)
            await dbScope.Db.Deleteable<SysLogHttp>()
                .Where(u => u.Account == tag || u.Id == log.Id)
                .ExecuteCommandAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 9. 查询服务(真实 PG):分页过滤(Account 精确/Url 模糊/StatusCode 精确)+ 详情全字段
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 查询服务_分页过滤与详情()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysLogHttpService>();

        var tag = $"ad_http_svc_{Guid.NewGuid().ToString("N")[..8]}";
        const long idAlpha = 99_000_000_004_01L;
        const long idBeta = 99_000_000_004_02L;

        try
        {
            // 幂等前置:同 Id 残留先清(上一次异常退出可能留行)
            await dbScope.Db.Deleteable<SysLogHttp>()
                .Where(u => u.Id == idAlpha || u.Id == idBeta)
                .ExecuteCommandAsync();

            await dbScope.Db.Insertable(new SysLogHttp
            {
                Id = idAlpha,
                HttpMethod = "GET",
                RequestUrl = "https://ad-probe.local/orders/alpha",
                RequestHeaders = """{"X-Trace":"abc"}""",
                RequestBody = "req-alpha",
                ResponseBody = "resp-alpha",
                StatusCode = 200,
                ElapsedMs = 5,
                HasException = false,
                Account = tag,
                RealName = "探针批AD",
                TraceId = "trace-alpha"
            }).ExecuteCommandAsync();

            await dbScope.Db.Insertable(new SysLogHttp
            {
                Id = idBeta,
                HttpMethod = "POST",
                RequestUrl = "https://ad-probe.local/orders/beta",
                RequestBody = "req-beta",
                StatusCode = 500,
                ElapsedMs = 7,
                HasException = true,
                Exception = "boom-beta",
                Account = tag,
                TraceId = "trace-beta"
            }).ExecuteCommandAsync();

            // ① 无过滤:本探针账号 2 行,Id 倒序(最新在前)
            var pageAll = await svc.PageAsync(new PageSysLogHttpInput(tag, null, null, null, null, null));
            Assert.Equal(2L, pageAll.Total);
            Assert.Equal(2, pageAll.List.Count);
            Assert.Equal(idBeta, pageAll.List[0].Id);
            Assert.Equal(idAlpha, pageAll.List[1].Id);

            // ② Account 精确匹配(非模糊):近似但不等的账号 → 0 行
            var pageAccountMiss = await svc.PageAsync(new PageSysLogHttpInput(tag + "_miss", null, null, null, null, null));
            Assert.Equal(0L, pageAccountMiss.Total);
            Assert.Empty(pageAccountMiss.List);

            // ③ Url 模糊包含:"alpha" 只是 URL 片段 → 精确匹配必落空,命中即证模糊
            var pageUrl = await svc.PageAsync(new PageSysLogHttpInput(tag, "alpha", null, null, null, null));
            Assert.Equal(1L, pageUrl.Total);
            Assert.Equal(idAlpha, pageUrl.List[0].Id);

            // ④ StatusCode 精确匹配(叠 Account 以隔离库内其他用例残留行)
            var pageStatus = await svc.PageAsync(new PageSysLogHttpInput(tag, null, null, 500, null, null));
            Assert.Equal(1L, pageStatus.Total);
            Assert.Equal(idBeta, pageStatus.List[0].Id);
            Assert.True(pageStatus.List[0].HasException);

            // ⑤ 分页切片:PageSize=1 → 每页 1 行且 Total 不受分页影响
            var page1 = await svc.PageAsync(new PageSysLogHttpInput(tag, null, null, null, null, null, PageIndex: 1, PageSize: 1));
            Assert.Equal(2L, page1.Total);
            Assert.Single(page1.List);
            Assert.Equal(idBeta, page1.List[0].Id);

            // ⑥ 详情:全字段(列表不载的四大字段在此必须回读一致)
            var detail = await svc.GetDetailAsync(idAlpha);
            Assert.Equal(tag, detail.Account);
            Assert.Equal("GET", detail.HttpMethod);
            Assert.Equal("https://ad-probe.local/orders/alpha", detail.RequestUrl);
            Assert.Equal(200, detail.StatusCode);
            Assert.Equal("""{"X-Trace":"abc"}""", detail.RequestHeaders);
            Assert.Equal("req-alpha", detail.RequestBody);
            Assert.Equal("resp-alpha", detail.ResponseBody);
            Assert.False(detail.HasException);
            Assert.Null(detail.Exception);

            // ⑦ 详情:不存在的 Id → 业务异常(不返回空对象误导)
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(98_888_888_888_888L));
            Assert.Equal(ErrorCodes.LogHttpNotFound, ex.ErrorCode); // 批 AR-4 口径

            _output.WriteLine($"判定:Account 精确 2 行 / Url 模糊 1 行 / StatusCode 精确 1 行 / 详情全字段一致 / 不存在 Id 抛 {ex.Message}");
        }
        finally
        {
            await dbScope.Db.Deleteable<SysLogHttp>()
                .Where(u => u.Account == tag || u.Id == idAlpha || u.Id == idBeta)
                .ExecuteCommandAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:handler 链 / 断言取件 / stub 服务端 / 替身 / 容器
    // ─────────────────────────────────────────────────────────────

    /// <summary>被测 handler 链:HttpLoggingHandler(options, logger, publisher) → 真实 HttpClientHandler(打真网络)。</summary>
    private static HttpClient CreateClient(HttpLogOptions options, FakePublisher publisher)
        => CreateClient(options, publisher, new HttpClientHandler());

    /// <summary>同上,可注入内层 handler(用例 7 需要"抛出指定异常实例"的内层件)。</summary>
    private static HttpClient CreateClient(HttpLogOptions options, FakePublisher publisher, HttpMessageHandler inner)
        => new(new HttpLoggingHandler(Options.Create(options), NullLogger<HttpLoggingHandler>.Instance, publisher)
        {
            InnerHandler = inner
        });

    /// <summary>取唯一一条 http_log 载荷(条数不为 1 直接失败,防"多发/漏发"被放过)。</summary>
    private static SysLogHttp SingleHttpLog(FakePublisher publisher)
    {
        var items = publisher.Items;
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("http_log", item.Name);
        return Assert.IsType<SysLogHttp>(item.Payload);
    }

    /// <summary>请求头 JSON → 大小写不敏感字典(键名口径由实现决定,断言不应钉死大小写)。</summary>
    private static Dictionary<string, string> ParseHeaders(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 事件发布替身:捕获 (事件名, 载荷) 供字段级断言(隔离真实总线时序,同批 I 的 FakePublisher 思路)。
    /// </summary>
    private sealed class FakePublisher : IEventPublisher
    {
        private readonly ConcurrentQueue<(string Name, object? Payload)> _items = new();

        /// <summary>已发布项快照(载荷类型为 SysLogHttp)。</summary>
        public IReadOnlyList<(string Name, object? Payload)> Items => _items.ToArray();

        public int Count => _items.Count;

        public Task PublishAsync<T>(string eventName, T payload)
        {
            _items.Enqueue((eventName, payload));
            return Task.CompletedTask;
        }
    }

    /// <summary>内层 handler 替身:抛指定异常实例(用例 7 的 "Assert.Same 原样重抛" 取证件)。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    /// <summary>
    /// 本机 stub HTTP 服务端(HttpListener,127.0.0.1 随机空闲端口,固定 JSON 响应)。
    /// WHY 自建真服务端:被测件要观察的是真实 Http 管道产物(状态码/响应体/耗时),替身 handler 给不了。
    /// 端口先经 TcpListener(0) 探测空闲口再交给 HttpListener(后者不支持 0 号端口),冲突时重试 5 次。
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly int _statusCode;
        private readonly string _responseJson;
        private readonly CancellationTokenSource _cts = new();
        private HttpListener? _listener;
        private Task? _loop;
        private string _prefix = string.Empty;

        public StubServer(string responseJson, int statusCode = 200)
        {
            _responseJson = responseJson;
            _statusCode = statusCode;
        }

        /// <summary>服务端基地址(形如 http://127.0.0.1:46557/),Start 后稳定。</summary>
        public string BaseUrl => _prefix;

        public void Start()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                // 每次重试都重新取空闲端口:上一次失败可能正是端口被抢
                _prefix = $"http://127.0.0.1:{AllocateFreePort()}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(_prefix);
                try
                {
                    listener.Start();
                    _listener = listener;
                    _loop = Task.Run(LoopAsync);
                    return;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }

            throw new InvalidOperationException("stub HTTP 服务端启动失败:连续 5 次 HttpListener.Start 均失败(端口持续被占)");
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener!.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return; // Stop/Close 时 GetContextAsync 抛 → 循环退出(正常停机路径)
                }

                try
                {
                    // 请求体必须读完再回(否则客户端侧可能收到连接重置),但本 stub 不回显 → 丢弃即可
                    _ = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(_responseJson);
                    ctx.Response.StatusCode = _statusCode;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch
                {
                    // 客户端提前断开(如被测用例只关心发出):stub 侧忽略,不影响断言
                }
            }
        }

        private static int AllocateFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // 停机异常不影响用例结论(端口随进程释放)
            }

            _cts.Dispose();
        }
    }

    /// <summary>
    /// DB 测试容器(仅用例 8/9 需要):与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// InitTables 扫 Core 程序集增量建表(含本批新增 sys_log_http,幂等)。
    /// 改写自 BatchABTenantDbTest.DbScope(去掉租户/加密相关项);PG 不可达时标记降级(用例侧 Assert.Fail)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        public DbScope()
        {
            if (!IsPostgresReachable())
            {
                return;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysLogHttp).Assembly); // [Service] 自动发现(含 HttpLogSubscriber/SysLogHttpService)
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db); // 建 sys_log_http 等缺表(增量幂等)
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死,同 BatchABTenantDbTest 口径)。</summary>
        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(DbHost, DbPort);
                return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}