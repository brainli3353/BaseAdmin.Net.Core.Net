using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.EventBus;
using AdminCore.Core.Extensions;
using AdminCore.Core.Logging;
using AdminCore.Core.Option;
using AdminCore.Core.Services.Serial;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AE 两处修复的回归测试:
/// ① G12 —— <c>SqlSugarSetup.resolveSerialService</c>(无 <c>ICacheService</c> 的容器插入不再抛 + 降级不填充);
/// ② E3  —— <c>HttpClientBuilderExtensions.AddHttpOutboundLogging()</c>(命名客户端一行接线 → 出站被记录)。
/// </summary>
/// <remarks>
/// WHY 用例 1/2 要**故意不调 AddCache**:G12 的触发形态是"<c>AddAutoServices</c> 注册了 SysSerialService、
/// 但容器没有 ICacheService"——修复前 AOP 每次插入都无条件解析该服务,MS.DI 因 ctor 依赖缺失抛
/// InvalidOperationException(不是返回 null),于是"未注册缓存 → 降级不填充"的注释承诺落空、任何插入都炸。
/// 故本类必须复刻该容器形态(而不是用既有 DbScope —— 后者调了 AddCache,永远走不到 G12 分支)。
/// WHY 用例 1/2 探针表自建自删(而不是复用 BatchWSerialTest.ProbeSerialEntity):见文件末尾实体注释
/// (既有探针的 OrderNo 列在库内是 NOT NULL,"以 null 插入成功"物理上不可能)。
/// WHY 用例 3/4 用**本机 stub HTTP 服务端**(HttpListener + 随机空闲端口,同 BatchADHttpLogTest 口径):
/// 被测件挂在真实 HttpClient 管道上,"记录了一条 http_log 且状态码 = 200"必须是真网络行为的产物;
/// 替身 HttpMessageHandler 只能证明"记录了替身给的值"(拿被测机制的输入证明被测机制)。
/// 零污染:容器/stub 用 using 或 finally 收尾;探针行 DELETE、自建表 DROP;端口随机取空闲口。
/// </remarks>
public class BatchAETechDebtTest
{
    private readonly ITestOutputHelper _output;

    public BatchAETechDebtTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchADHttpLogTest/BatchABTenantDbTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchABTenantDbTest/BatchADHttpLogTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // stub 服务端固定响应体(与 BatchADHttpLogTest 同源,便于口径对齐)
    private const string StubResponseJson = """{"ok":true,"from":"stub"}""";

    // ─────────────────────────────────────────────────────────────
    // 1. G12:无缓存容器(有 SysSerialService、无 ICacheService)→ 插入不抛 + 不填充流水号
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task G12_无缓存容器_插入不抛且不填充流水号()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var scope = new NoCacheDbScope();
        var sp = scope.Provider;
        var db = sp.GetRequiredService<ISqlSugarClient>();

        // 前置断言(非恒真):G12 的触发形态 = 容器里**没有** ICacheService。
        // 若将来有人给本容器补上 AddCache,用例应在此失败,而不是悄悄退化成"有缓存路径"而失去意义。
        Assert.Null(sp.GetService<AdminCore.Core.Cache.ICacheService>());

        // 反证(修复前行为的最小复现):修复前 AOP 的 InsertByObject 分支**无条件**执行
        // scope.ServiceProvider.GetService<SysSerialService>()(修复前 lambda 原文见 git diff),
        // 而 MS.DI 对"服务类型已注册、其 ctor 依赖解析失败"抛 InvalidOperationException 而非返回 null。
        // 下面这条断言就是修复前那条调用的真实取证:同容器直解 → 抛,且消息点名 ICacheService。
        var diFailure = Assert.Throws<InvalidOperationException>(() => sp.GetService<SysSerialService>());
        Assert.Contains("ICacheService", diFailure.Message);

        var tableExistedBefore = await TableExistsAsync(db, AeProbeTables.Serial);
        var entity = new AeG12SerialProbeEntity { OrderNo = null };
        try
        {
            db.CodeFirst.InitTables(typeof(AeG12SerialProbeEntity));

            // 修复前:此行在 SQL 生成前的 AOP 阶段就抛 InvalidOperationException(即上一条断言的形态)
            var affected = await db.Insertable(entity).ExecuteCommandAsync();

            Assert.Equal(1, affected);
            Assert.True(entity.Id > 0, "主键应由 AOP 自动填雪花(降级不填充不牵连主键/审计填充)");
            Assert.Null(entity.OrderNo); // 降级口径 = 不填充(既不抛、也不编造值)

            // 物理复核(不经实体映射):行确实落到库里,断言不是"只在内存对象上成立"
            var hit = await db.Ado.SqlQueryAsync<int>(
                $"SELECT count(*)::int FROM {AeProbeTables.Serial} WHERE id = @id",
                new SugarParameter("@id", entity.Id));
            Assert.Equal(1, hit[0]);

            _output.WriteLine($"判定:无缓存容器插入成功(affected={affected}, Id={entity.Id}),[BindSerial] 属性={entity.OrderNo ?? "<null>"};" +
                              $"同容器直解 SysSerialService 抛 {diFailure.GetType().Name}:{FirstLine(diFailure.Message)}");
        }
        finally
        {
            // 物理清理:探针行删除;表仅在本用例新建时 DROP(库内已存在的表不动,零跨用例污染)
            await CleanupProbeAsync(db, AeProbeTables.Serial, entity.Id, createdTable: !tableExistedBefore);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. G12:同容器口径下"降级不影响常规插入"——两种实体(带/不带 [BindSerial])都覆盖
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task G12_无缓存且无流水号服务_插入仍正常()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail(Downgrade);
        }

        using var scope = new NoCacheDbScope();
        var sp = scope.Provider;
        var db = sp.GetRequiredService<ISqlSugarClient>();
        Assert.Null(sp.GetService<AdminCore.Core.Cache.ICacheService>());

        var plainExistedBefore = await TableExistsAsync(db, AeProbeTables.Plain);
        var serialExistedBefore = await TableExistsAsync(db, AeProbeTables.Serial);
        var plain = new AeG12PlainProbeEntity { Remark = "AE-普通实体" };
        var serial = new AeG12SerialProbeEntity { OrderNo = null };
        try
        {
            db.CodeFirst.InitTables(typeof(AeG12PlainProbeEntity), typeof(AeG12SerialProbeEntity));

            // 修复前本用例同样会抛:resolveSerialService() 在 Insert 分支是**无条件**调用,
            // 与实体是否带 [BindSerial] 无关——故"降级不影响常规插入"必须由普通实体单独取证。
            var plainAffected = await db.Insertable(plain).ExecuteCommandAsync();
            Assert.Equal(1, plainAffected);
            Assert.True(plain.Id > 0);
            Assert.Equal("AE-普通实体", plain.Remark); // 业务字段原样落库,未被降级路径改动

            // 同一容器内带 [BindSerial] 的实体:插入同样成功,属性保持 null(降级不填充)
            var serialAffected = await db.Insertable(serial).ExecuteCommandAsync();
            Assert.Equal(1, serialAffected);
            Assert.True(serial.Id > 0);
            Assert.Null(serial.OrderNo);

            var plainHit = await db.Ado.SqlQueryAsync<int>(
                $"SELECT count(*)::int FROM {AeProbeTables.Plain} WHERE id = @id",
                new SugarParameter("@id", plain.Id));
            Assert.Equal(1, plainHit[0]);

            _output.WriteLine($"判定:无缓存容器下普通实体插入 affected={plainAffected}(Id={plain.Id})," +
                              $"带 [BindSerial] 实体插入 affected={serialAffected}(Id={serial.Id}, OrderNo={serial.OrderNo ?? "<null>"})");
        }
        finally
        {
            await CleanupProbeAsync(db, AeProbeTables.Plain, plain.Id, createdTable: !plainExistedBefore);
            await CleanupProbeAsync(db, AeProbeTables.Serial, serial.Id, createdTable: !serialExistedBefore);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. E3:命名客户端一行接线 → 出站被记录;未接线客户端不记录(反向证据)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task E3_命名客户端一行接线_出站被记录()
    {
        var (provider, publisher) = BuildHttpLogContainer(enabled: true);
        using (provider)
        {
            // 前置断言:配置确实绑进了选项(否则"记了"可能只是默认值/实现巧合)
            var options = provider.GetRequiredService<IOptions<HttpLogOptions>>().Value;
            Assert.True(options.Enabled, "前置:Logging:Http:Enabled=true 必须绑定到 HttpLogOptions.Enabled");
            Assert.Equal(2048, options.MaxBodyLength);

            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

            // 接线取证(与开关正交,直查管道链):"probe" 挂了 handler,"plain" 没挂。
            // WHY 直查链而非只看发布条数:否则"没记录"无法区分"没接线"与"开关关",两个方向的证据会互相冒充。
            Assert.True(PipelineContains<HttpLoggingHandler>(handlerFactory.CreateHandler("probe")),
                "接线断:probe 命名客户端管道应含 HttpLoggingHandler");
            Assert.False(PipelineContains<HttpLoggingHandler>(handlerFactory.CreateHandler("plain")),
                "反向证据断:plain 命名客户端未调 AddHttpOutboundLogging,管道不应含 HttpLoggingHandler");

            var stub = new StubServer(StubResponseJson);
            try
            {
                stub.Start();
                var url = stub.BaseUrl.TrimEnd('/') + "/api/ae-probe?x=1";

                using (var client = factory.CreateClient("probe"))
                using (var response = await client.GetAsync(url))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode); // 记录不得改变业务响应
                }

                var log = SingleHttpLog(publisher);
                Assert.Equal("GET", log.HttpMethod);
                Assert.Contains(stub.BaseUrl.TrimEnd('/'), log.RequestUrl);
                Assert.Contains("/api/ae-probe?x=1", log.RequestUrl);
                Assert.Equal(200, log.StatusCode);
                Assert.False(log.HasException);

                // 反向证据:未接线的命名客户端发同样请求 → 发布条数不新增
                var countAfterWired = publisher.Count;
                Assert.Equal(1, countAfterWired);
                using (var client = factory.CreateClient("plain"))
                using (var response = await client.GetAsync(url))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(StubResponseJson, await response.Content.ReadAsStringAsync());
                }

                Assert.Equal(countAfterWired, publisher.Count);
                Assert.Equal(1, publisher.Count);

                _output.WriteLine($"判定:接线客户端 1 条 http_log({log.RequestUrl} → {log.StatusCode});" +
                                  $"未接线客户端同请求后仍为 {publisher.Count} 条");
            }
            finally
            {
                stub.Dispose();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. E3:接线但开关关 → 请求成功且零记录(证"接线 ≠ 启用"两级控制)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task E3_接线但开关关_不记录()
    {
        // ExcludeHosts 配一个与 stub 无关的主机:它绑定成功即证明配置段确实被读到(非默认值)——
        // 否则本用例的 Enabled=false 与"选项根本没绑定"无法区分(缺省恰好也是 false)。
        var (provider, publisher) = BuildHttpLogContainer(enabled: false, excludeHost: "monitor.invalid");
        using (provider)
        {
            var options = provider.GetRequiredService<IOptions<HttpLogOptions>>().Value;
            Assert.False(options.Enabled);
            Assert.Equal("monitor.invalid", Assert.Single(options.ExcludeHosts));

            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
            // handler 确实挂在 probe 管道上(证明"关"是开关的效果,不是"没接线"的效果)
            Assert.True(PipelineContains<HttpLoggingHandler>(handlerFactory.CreateHandler("probe")),
                "接线断:probe 命名客户端管道应含 HttpLoggingHandler(本用例的零记录应来自开关)");

            var stub = new StubServer(StubResponseJson);
            try
            {
                stub.Start();
                using (var client = factory.CreateClient("probe"))
                using (var response = await client.GetAsync(stub.BaseUrl.TrimEnd('/') + "/api/ae-probe"))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(StubResponseJson, await response.Content.ReadAsStringAsync());
                }

                Assert.Empty(publisher.Items);
                _output.WriteLine("判定:已接线 + Enabled=false → 发布 0 条,请求仍 200 且响应体可读");
            }
            finally
            {
                stub.Dispose();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:容器装配 / 断言取件 / 管道链检查 / stub 服务端 / PG 工具
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 出站日志测试容器:AddHttpOutboundLogging(装配选项 + handler)+ FakePublisher 替身 +
    /// "probe"(接线)/"plain"(未接线)两个命名客户端。
    /// WHY publisher 以 IEventPublisher 单例注册:handler 的可选依赖走 DI 解析,替身才能直接捕获 http_log。
    /// </summary>
    private static (ServiceProvider Provider, FakePublisher Publisher) BuildHttpLogContainer(
        bool enabled, string? excludeHost = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Logging:Http:Enabled"] = enabled ? "true" : "false",
            ["Logging:Http:MaxBodyLength"] = "2048"
        };
        if (excludeHost is not null)
        {
            settings["Logging:Http:ExcludeHosts:0"] = excludeHost;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var publisher = new FakePublisher();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpOutboundLogging(config);                  // 装配:选项绑定 + handler 注册(不启用)
        services.AddSingleton<IEventPublisher>(publisher);        // 捕获 (事件名, 载荷) 的替身
        services.AddHttpContextAccessor();                       // handler 的可选依赖,按宿主形态提供
        services.AddHttpClient("probe").AddHttpOutboundLogging(); // ★ E3 被测接线(一行)
        services.AddHttpClient("plain");                          // 反向证据:同容器内**不接线**的客户端

        return (services.BuildServiceProvider(), publisher);
    }

    /// <summary>取唯一一条 http_log 载荷(条数不为 1 直接失败,防"多发/漏发"被放过)。</summary>
    private static SysLogHttp SingleHttpLog(FakePublisher publisher)
    {
        var items = publisher.Items;
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("http_log", item.Name);
        return Assert.IsType<SysLogHttp>(item.Payload);
    }

    /// <summary>
    /// 判定 HttpClient 管道链里是否存在指定 handler(沿 DelegatingHandler.InnerHandler 下钻)。
    /// WHY 需要它:"接线"是 E3 的判定对象,只有直查链才能把"接线"与"开关"两级控制分别固定住。
    /// </summary>
    private static bool PipelineContains<T>(HttpMessageHandler? handler) where T : HttpMessageHandler
    {
        for (var depth = 0; handler is not null && depth < 16; depth++)
        {
            if (handler is T)
            {
                return true;
            }

            handler = (handler as DelegatingHandler)?.InnerHandler;
        }

        return false;
    }

    private static string FirstLine(string text) => text.Split('\n')[0];

    /// <summary>表是否存在(建表前探测,决定 finally 是否 DROP——不动物理上已存在的表)。</summary>
    private static async Task<bool> TableExistsAsync(ISqlSugarClient db, string table)
    {
        var hit = await db.Ado.SqlQueryAsync<int>(
            "SELECT count(*)::int FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name",
            new SugarParameter("@name", table));
        return hit[0] > 0;
    }

    /// <summary>探针清理:删本用例的行;表仅在由本用例新建时 DROP。</summary>
    private static async Task CleanupProbeAsync(ISqlSugarClient db, string table, long id, bool createdTable)
    {
        if (id > 0)
        {
            await db.Ado.ExecuteCommandAsync($"DELETE FROM {table} WHERE id = @id", new SugarParameter("@id", id));
        }

        if (createdTable)
        {
            await db.Ado.ExecuteCommandAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    /// <summary>
    /// 短超时 TCP 探测(2 秒,PG 黑洞防挂死,同 BatchABTenantDbTest 口径)。
    /// WHY 用 CancellationToken 而非 Task.Wait(timeout):本仓约定禁 .Wait()/.Result(同步阻塞语义)。
    /// </summary>
    private static async Task<bool> IsPostgresReachableAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            await client.ConnectAsync(DbHost, DbPort, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// G12 触发形态的容器:与生产同一条 AddSqlSugar 注册路径 + AddAutoServices(Core 程序集,
    /// 从而注册 SysSerialService——其 ctor 依赖 ICacheService),**故意不调 AddCache**。
    /// </summary>
    private sealed class NoCacheDbScope : IDisposable
    {
        private readonly ServiceProvider _provider;

        public IServiceProvider Provider => _provider;

        public NoCacheDbScope()
        {
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
            services.AddAutoServices(typeof(SqlSugarSetup).Assembly);

            // ★ 此处刻意缺 AddCache:有 ICacheService 的容器永远走不到 G12 降级分支
            _provider = services.BuildServiceProvider();
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// 事件发布替身:捕获 (事件名, 载荷) 供字段级断言(隔离真实总线时序,同 BatchADHttpLogTest 的 FakePublisher 思路)。
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

    /// <summary>
    /// 本机 stub HTTP 服务端(HttpListener,127.0.0.1 随机空闲端口,固定 JSON 响应;照 BatchADHttpLogTest 同构)。
    /// WHY 自建真服务端:被测件要观察的是真实 Http 管道产物(状态码/响应体),替身 handler 给不了。
    /// 端口先经 TcpListener(0) 探测空闲口再交给 HttpListener(后者不支持 0 号端口),冲突时重试 5 次。
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly string _responseJson;
        private readonly CancellationTokenSource _cts = new();
        private HttpListener? _listener;
        private Task? _loop;
        private string _prefix = string.Empty;

        public StubServer(string responseJson)
        {
            _responseJson = responseJson;
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
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch
                {
                    // 客户端提前断开:stub 侧忽略,不影响断言
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
}

/// <summary>本批探针表名(单点声明:实体 [SugarTable] 与用例清理共用,防两处口径漂移)。</summary>
internal static class AeProbeTables
{
    /// <summary>带 [BindSerial] 的探针表(dsh_ 前缀 = 开发期手建表约定;用例内建/删)。</summary>
    public const string Serial = "dsh_ae_g12_serial_probe";

    /// <summary>不带 [BindSerial] 的普通探针表。</summary>
    public const string Plain = "dsh_ae_g12_plain_probe";
}

/// <summary>
/// G12 探针实体(带 [BindSerial]):OrderNo 列显式 <c>IsNullable = true</c>。
/// WHY 自建而非复用 BatchWSerialTest.ProbeSerialEntity(实测取舍):
/// 后者的 OrderNo 列在库内是 <b>NOT NULL</b>(psql \d dsh_w_serial_probe 实证),而"无缓存 → 降级不填充"
/// 恰恰要求**以 null 值插入成功**——复用它在物理上不可能成立(PG 会以 23502 not-null 违约拒绝,
/// 失败原因与 G12 无关,断言会变成"测 NOT NULL 约束");为它 ALTER TABLE DROP NOT NULL 又会改动
/// 另一个测试类共用的表(跨用例污染,且不回滚就是库结构漂移)。故本文件自建探针表(用例内建/删,零残留)。
/// </summary>
[SugarTable(AeProbeTables.Serial)]
public class AeG12SerialProbeEntity : EntityTenant
{
    /// <summary>业务单号(绑定 test 类型流水号;无缓存容器下应保持 null)。</summary>
    [BindSerial("test")]
    [SugarColumn(ColumnName = "OrderNo", ColumnDescription = "业务单号(绑定 test 类型流水号)", IsNullable = true)]
    public string? OrderNo { get; set; }
}

/// <summary>
/// G12 对照探针实体(**不带** [BindSerial]):证"降级不填充"不牵连常规插入路径。
/// </summary>
[SugarTable(AeProbeTables.Plain)]
public class AeG12PlainProbeEntity : EntityTenant
{
    /// <summary>普通业务字段(原样落库,未被降级路径改动)。</summary>
    [SugarColumn(ColumnName = "Remark", ColumnDescription = "备注", IsNullable = true)]
    public string? Remark { get; set; }
}