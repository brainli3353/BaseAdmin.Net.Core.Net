using System.Diagnostics;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Job;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Job;
using AdminCore.Core.Services.Job.Dto;
using AdminCore.Web.Entry.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace AdminCore.Test;

// ============================================================================
// 批 AJ「定时任务集群(5.16)」探针任务。
// WHY 探针落本测试程序集 + 名字一律 aj_probe_ 前缀:调度器按 Assembly[] 显式构造扫描,
// 任务名全局唯一(与既有 test_probe/clean_sys_log 不撞),每个用例各自专属探针 ⇒ 计数器互不串扰。
// 计数器 static volatile:调度执行在后台线程,用例内等待可见。
// ============================================================================

/// <summary>
/// 心跳/运行中登记探针:记录执行次数与"运行中快照"(在任务体内读 Hash sys_job_running)。
/// DelayMs &gt; 0 时执行体阻塞(供外部在途观察运行中登记行)。
/// </summary>
[JobName("aj_probe_beat")]
public sealed class AjProbeBeatJob : IJob
{
    /// <summary>本次执行体阻塞时长(用例设置;0=立即返回)。</summary>
    public static int DelayMs;

    /// <summary>执行开始次数(进入任务体即自增)。</summary>
    public static int Started;

    /// <summary>执行完成次数。</summary>
    public static int Completed;

    /// <summary>任务体内读到的本任务运行中登记行(用于断言"执行期有行")。</summary>
    public static AjStoredRunning? RunningSnapshot;

    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Started);
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs / 2, ct);
        }

        // WHY 在任务体内读(而不是外部读):只有"执行中途"的快照才证明登记行存在于执行期,
        // 外部读会与"执行已结束、finally 已清理"竞态。
        // WHY 直读原始值:暂不依赖生产读侧的双层反序列化(见 AjProbeServices.RawHashGetAsync 注释)
        RunningSnapshot = AjProbeServices.ParseStored<AjStoredRunning>(
            await AjProbeServices.RawHashGetAsync(CacheConst.KeyJobRunning, context.JobName));

        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs - DelayMs / 2, ct);
        }

        Interlocked.Increment(ref Completed);
    }
}

/// <summary>
/// 锁续租探针(用例③核心):任务体存续期间每 2s 采样防重入锁的剩余 TTL,
/// 记录全部读数与任务结束时刻 ⇒ 外部据此断言"锁 TTL 在执行中途被延长"。
/// </summary>
[JobName("aj_probe_renew")]
public sealed class AjProbeRenewJob : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var cache = AjProbeServices.Cache!;
        var lockKey = CacheConst.KeyJobLock + context.JobName;
        var samples = new List<double>();

        // WHY 采样而不断言式睡眠:LockRenewSeconds 被代码 Math.Max(30, …) 钳到 ≥30s,
        // 只有"连续读数先衰减、后被拉回"才是续租发生的实证(单点读数证明不了)。
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(AjProbeRenewConfig.RunSeconds))
        {
            var ttl = await cache.GetExpireAsync(lockKey);
            samples.Add(ttl?.TotalSeconds ?? -1);
            await Task.Delay(AjProbeRenewConfig.SampleIntervalMs, ct);
        }

        AjProbeRenewConfig.LockSamples = samples;
        AjProbeRenewConfig.FinishedAt = DateTime.Now;
        AjProbeRenewConfig.Completed = true;
    }
}

/// <summary>用例④(归属不匹配)/用例⑤(归属匹配)专用探针:各自独立计数,防两用例串扰。</summary>
[JobName("aj_probe_host_skip")]
public sealed class AjProbeHostSkipJob : IJob
{
    public static int Executions;

    public Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
}

/// <summary>见 <see cref="AjProbeHostSkipJob"/>:归属匹配用例的探针(独立类 ⇒ 独立静态计数)。</summary>
[JobName("aj_probe_host_ok")]
public sealed class AjProbeHostOkJob : IJob
{
    public static int Executions;

    public Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 节点表/运行中表的**存储形态**投影(批 AJ 写入的 JSON 字段逐一同名)。
/// WHY 单列而不复用 JobNodePayload:后者是"生产读侧期望的单层形态",而实际存储是双层(见 ParseStored);
/// 直接用后者会在生产读侧修好后仍失真,故用与被测写入体严格对应的独立投影。
/// </summary>
internal sealed class AjStoredNode
{
    public string InstanceId { get; set; } = string.Empty;

    public string NodeTag { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    public DateTime? LastBeat { get; set; }
}

/// <summary>运行中表的存储形态投影(见 <see cref="AjStoredNode"/>)。</summary>
public sealed class AjStoredRunning
{
    public string JobName { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    public string? NodeTag { get; set; }

    public DateTime? StartedAt { get; set; }

    public string? Trigger { get; set; }
}

/// <summary>探针共享服务定位(任务体由调度器按 DI 解析构造,构造签名不便传自定义参数 → 走静态注入)。</summary>
internal static class AjProbeServices
{
    /// <summary>真实 Redis 档 ICacheService(用例开始前赋值)。</summary>
    public static ICacheService? Cache;

    /// <summary>真实 Redis 连接(任务体内要读**原始存储值**:见 <see cref="RawHashGetAsync"/> 的 WHY)。</summary>
    public static IConnectionMultiplexer? Redis;

    /// <summary>HGET 原始字符串值(不走 ICacheService 反序列化)。</summary>
    public static async Task<string?> RawHashGetAsync(string key, string field)
    {
        var raw = await Redis!.GetDatabase().HashGetAsync(key, field);
        return raw.ToString() is { Length: > 0 } text ? text : null;
    }

    /// <summary>解开"字符串里再装 JSON"的存储形态(兼容单层)。</summary>
    public static T? ParseStored<T>(string? raw) where T : class
        => BatchAJJobClusterTest.ParseStoredForProbe<T>(raw);
}

/// <summary>续租探针配置与观测结果(用例内重置)。</summary>
internal static class AjProbeRenewConfig
{
    /// <summary>本用例要求的任务体总时长(秒)——须 &gt; LockRenewSeconds 才能观察到续租。</summary>
    public static int RunSeconds = 35;

    /// <summary>采样间隔(毫秒)。</summary>
    public static int SampleIntervalMs = 2000;

    /// <summary>锁 TTL 采样读数(秒;-1=键不存在)。</summary>
    public static List<double> LockSamples = new();

    /// <summary>任务体结束时刻(用于对齐"外部观察起点")。</summary>
    public static DateTime? FinishedAt;

    /// <summary>任务体是否跑完。</summary>
    public static volatile bool Completed;

    public static void Reset(int runSeconds)
    {
        RunSeconds = runSeconds;
        LockSamples = new List<double>();
        FinishedAt = null;
        Completed = false;
    }
}

/// <summary>
/// 批 AJ「定时任务集群(5.16)」测试:被测主体 = <see cref="JobScheduler"/> 的集群能力
/// (节点心跳 / Enabled 开关 / 执行期锁续租 / TargetNode 归属过滤 / 运行中登记与清理)
/// 与 <see cref="SysJobController.Nodes"/> 的僵尸节点读侧过滤。
/// </summary>
/// <remarks>
/// WHY 全部用例走**真实 Redis(6380)+ 真实 PG**:被测的是"跨实例可见性"与"锁键真实 TTL 语义"——
/// MemoryCacheService 档的锁/TTL 只活在单进程字典里(见 MemoryCacheService 类注释),证明不了集群契约;
/// 内存替身只能验证"我喂了什么就返回什么"。任一依赖不可达即 Assert.Fail 显式降级(禁止冒充通过)。
/// WHY 调度器 tick 注入 200ms(生产缺省 5000ms):用例窗口按秒计,5s 粒度会让"几轮心跳"不可控。
/// 零污染:探针 sys_job_detail 行物理删除(只删本文件自造任务名,绝不触碰 clean_sys_log 等既有行),
/// Redis 侧只删本用例自己的 Hash field / 锁键(绝不做 KEYS */FLUSH 之类全库操作)。
/// </remarks>
public class BatchAJJobClusterTest : IDisposable
{
    private readonly ITestOutputHelper _output;

    // 开发库连接串(与 JobSchedulerTest/BatchADDatabaseTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 环境事实(与 BatchAFCacheQueueTest 同源):6379 是需口令的业务 Redis,6380 才是无口令的 eventbus 实例
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    private const int TickMs = 200;

    // 各用例专属任务名(aj_ 前缀自造,断言关键词与探针逐一对应)
    private const string BeatJobName = "aj_probe_beat";
    private const string RenewJobName = "aj_probe_renew";
    private const string HostSkipJobName = "aj_probe_host_skip";
    private const string HostOkJobName = "aj_probe_host_ok";

    /// <summary>本实例节点标签(用例统一口径;带 GUID ⇒ 与其它用例/环境的节点行天然不冲突)。</summary>
    private static readonly string NodeTagA = "aj-node-a-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>群外标签(用例②的 Enabled=false 实例;用例④的不匹配归属)。</summary>
    private static readonly string NodeTagB = "aj-node-b-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Redis 连接(进程级复用;不可达时用例显式降级)。</summary>
    private IConnectionMultiplexer? _redis;

    private readonly Task<IConnectionMultiplexer?> _redisConnect;

    private bool _redisProbed;
    private bool _redisReachable;

    public BatchAJJobClusterTest(ITestOutputHelper output)
    {
        _output = output;
        // 构造期只发起异步连接(不在构造函数里同步等待,避免 xUnit 构造超时);首个用例首次 await 时兑现
        _redisConnect = ConnectRedisAsync();
    }

    /// <summary>释放 Redis 连接(进程级共享资源,用例类退出即归还)。</summary>
    public void Dispose()
    {
        _redis?.Dispose();
    }

    // ── 环境探测 ─────────────────────────────────────────────────────────────

    /// <summary>PG 可达性探测(短超时 TCP,不依赖 ORM;同既有测试口径)。</summary>
    private static async Task<bool> IsPostgresReachableAsync()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            // WHY WaitAsync 而非 Task.Wait:同步等待违反"禁 .Wait()"约定;此处 3s 超时防 PG 黑洞挂死
            await client.ConnectAsync(DbHost, DbPort).WaitAsync(TimeSpan.FromSeconds(3));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Redis 6380 异步连接(缺省 AbortOnConnectFail=false:探测已确认可达,不为瞬时抖动放弃)。</summary>
    private async Task<IConnectionMultiplexer?> ConnectRedisAsync()
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { { RedisHost, RedisPort } },
                AbortOnConnectFail = false,
                ConnectTimeout = 3000
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取真实 Redis 档缓存(不可达即 Assert.Fail,不静默降级到 Memory——降级会让集群契约失真)。</summary>
    private async Task<ICacheService> RequireRedisCacheAsync()
    {
        if (!_redisProbed)
        {
            _redis = await _redisConnect;
            _redisProbed = true;
            _redisReachable = _redis is { IsConnected: true };
        }

        Assert.True(_redisReachable,
            $"降级:Redis {RedisHost}:{RedisPort} 不可达——本用例未实跑,禁止冒充通过");
        var cache = new RedisCacheService(_redis!);
        AjProbeServices.Cache = cache;
        // WHY 任务体要 HGET 原始存储值(ParseStored 双层解包)⇒ 原始连接必须从这注入;
        // 缺此行 ⇒ 任务体内 RawHashGetAsync 对 null 解引用 → 探针中途异常 → Completed 永不置位(本批实测)
        AjProbeServices.Redis = _redis;
        return cache;
    }

    // ── 容器/调度器 ├──

    /// <summary>
    /// 构建测试 DI 容器:真实 PG(SqlSugar)+ 真实 Redis 档 ICacheService + JobCluster:* 配置
    /// + 本文件探针按具体类注册(调度器按具体类型解析 IJob——生产注册路径同口径)。
    /// </summary>
    /// <param name="cache">真实 Redis 档缓存。</param>
    /// <param name="nodeTag">JobCluster:NodeTag。</param>
    /// <param name="clusterEnabled">JobCluster:Enabled。</param>
    /// <param name="renewSeconds">JobCluster:LockRenewSeconds(代码 Math.Max(30, …) 钳底)。</param>
    private static ServiceProvider BuildProvider(ICacheService cache, string nodeTag, bool clusterEnabled, int renewSeconds)
    {
        var vars = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = DbConnectionString,
            ["Yit:WorkerId"] = "1",
            ["JobCluster:NodeTag"] = nodeTag,
            ["JobCluster:Enabled"] = clusterEnabled ? "true" : "false",
            ["JobCluster:LockRenewSeconds"] = renewSeconds.ToString()
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        var services = new ServiceCollection();
        // WHY 显式 <IConfiguration>:.NET 10 起 Build() 返回 IConfigurationRoot,裸注册会以具体类型为键,
        // JobScheduler 注入 IConfiguration 解析不到(既有测试实机诊断实证)
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSqlSugar(config);
        services.AddSingleton(cache);

        // 探针按具体类注册(调度器 GetRequiredService(_jobTypes[name]) 解析;漏注册会让任务执行异常)
        services.AddScoped<AjProbeBeatJob>();
        services.AddScoped<AjProbeRenewJob>();
        services.AddScoped<AjProbeHostSkipJob>();
        services.AddScoped<AjProbeHostOkJob>();
        return services.BuildServiceProvider();
    }

    /// <summary>起调度器:真实探针程序集扫描 + 200ms tick(生产缺省 5000ms 不变)。</summary>
    private static JobScheduler NewScheduler(IServiceProvider provider, ICacheService cache, int tickMs = TickMs)
        => new(provider, cache, provider.GetRequiredService<ILogger<JobScheduler>>(),
            new[] { typeof(BatchAJJobClusterTest).Assembly }, tickMs);

    /// <summary>表结构初始化(开发库;sys_job_detail.TargetNode 随 Core 程序集 CodeFirst 自动补齐)。</summary>
    private static void InitDb(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<ISqlSugarClient>();
        db.HealColumnCase();
        db.InitTables();
    }

    /// <summary>轮询等待条件成立(最多 timeoutMs)。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>
    /// 异步条件轮询(需真实 await 判定时用,如读 Redis)。
    /// WHY 独立重载而非把同步版包一层:同步委托里只能 GetAwaiter().GetResult() 阻塞线程池线程——
    /// 既是"禁 .Result/.Wait()"约定的同类违规,也会让等待窗口在 I/O 上白耗(此处以 20ms 粒度 await 让出)。
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    /// <summary>插入探针调度行(每个用例自造任务名;Interval 缺省 3600 ⇒ 只有显式 RunNow 会触发)。</summary>
    private static async Task InsertProbeRowAsync(ISqlSugarRepository<SysJobDetail> repo, string jobName,
        int interval, string? targetNode, string remark, DateTime? lastRunTime = null)
    {
        await repo.DeletePhysicalByAsync(u => u.JobName == jobName);
        await repo.InsertAsync(new SysJobDetail
        {
            JobName = jobName,
            Interval = interval,
            Enabled = true,
            TargetNode = targetNode,
            Remark = remark,
            // WHY lastRunTime 默认 null(首轮即可触发);03 传过去时刻 ⇒ 首轮 IsDue=false,
            // 避免"调度器首轮自动触发占走防重入锁"顶掉 RunNow 的独占触发(本批实测)
            LastRunTime = lastRunTime
        });
    }

    /// <summary>清理探针行(只删本文件自造任务名;物理删除防跨用例串扰)。</summary>
    private static async Task CleanupProbeRowsAsync(ISqlSugarRepository<SysJobDetail> repo, params string[] jobNames)
    {
        foreach (var jobName in jobNames)
        {
            await repo.DeletePhysicalByAsync(u => u.JobName == jobName);
        }
    }

    /// <summary>
    /// 等"本用例全部探针的防重入锁"消失 —— 即调度器的在途执行已跑完 ExecuteTriggeredAsync 的 finally。
    /// WHY 必须等:调度器后台执行与测试**共用同一个单例 SqlSugar scope**,StopAsync 返回不代表
    /// fire-and-forget 的收尾 DB 操作已停(框架已知差距,见 docs/框架差距清单.md 的 HTTP 直连仓储并发限制);
    /// 此时抢着用同一 scope 清理会撞 "A command is already in progress"(本批实测)。
    /// 锁的 finally 释放是执行体最后一件事 ⇒ 锁消失即收尾完成的最强可观测信号(比 sleep 猜时更可靠)。
    /// </summary>
    private static async Task SettleInFlightAsync(ICacheService cache, params string[] jobNames)
    {
        await WaitUntilAsync(async () =>
        {
            foreach (var jobName in jobNames)
            {
                if (await cache.ExistKeyAsync(CacheConst.KeyJobLock + jobName))
                {
                    return false;
                }
            }

            return true;
        }, 20000);
    }

    /// <summary>
    /// 带重试的探针行删除:即便在途收尾仍残留一次碰撞,也不让"清理"本身把用例判红
    /// (被测契约是集群行为,不是并发清理;但清理失败会污染库,故重试而非忽略)。
    /// </summary>
    private static async Task DeleteProbeRowsWithRetryAsync(ISqlSugarRepository<SysJobDetail> repo, string jobName)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await repo.DeletePhysicalByAsync(u => u.JobName == jobName);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(300 * attempt);
            }
        }

        await repo.DeletePhysicalByAsync(u => u.JobName == jobName);
    }

    /// <summary>
    /// 直读 Redis 原始字符串值(HGET)。
    /// WHY 不走 ICacheService.HashGetAsync:批 AJ 心跳/运行中登记调用方把**已序列化的 JSON 字符串**
    /// 又交给 HashSetAsync(其内部再序列化一次)⇒ 存进 Redis 的是"被转义的 JSON 字符串"(双层),
    /// 读侧按 T 反序列化直接 JsonException(本批实测,见报告"生产代码发现")。
    /// 本测试据此按**真实存储形态**断言(双层解开),既不假绿也不因生产缺陷把集群行为判红。
    /// </summary>
    private async Task<string?> RawHashGetAsync(string key, string field)
        => (await _redis!.GetDatabase().HashGetAsync(key, field)).ToString() is { Length: > 0 } raw ? raw : null;

    /// <summary>供探针复用的反序列化入口(探针类非嵌套类型,无法访问 private static)。</summary>
    internal static T? ParseStoredForProbe<T>(string? raw) where T : class => ParseStored<T>(raw);

    /// <summary>解开"字符串里再装 JSON"的存储形态(兼容单层:正常形态也能读)。</summary>
    private static T? ParseStored<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw;
        if (text.StartsWith('"'))
        {
            text = System.Text.Json.JsonSerializer.Deserialize<string>(text)!;
        }

        return System.Text.Json.JsonSerializer.Deserialize<T>(
            text, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// 清空节点表键(仅用例⑦/⑧前置:让端点只面对本用例手工植入的节点)。
    /// WHY 必须清:端点 Nodes() 用的是 HashGetAllAsync&lt;JobNodePayload&gt;(单层期望),
    /// 而调度器真实心跳写的是双层形态(见 ParseStored WHY)——两者混合时端点整表反序列化直接抛。
    /// 本批不改生产代码,故端点用例与"真实心跳行"用例分表隔离:端点判据本身仍被真实验证。
    /// </summary>
    private async Task ClearNodeTableAsync()
    {
        if (await _redis!.GetDatabase().KeyExistsAsync(CacheConst.KeyJobNode))
        {
            await _redis.GetDatabase().KeyDeleteAsync(CacheConst.KeyJobNode);
        }
    }

    /// <summary>清理本用例在 Redis 留下的探针痕迹:节点行(field)、运行中行(field)、防重入锁键与共享基准键(逐键删,不全库操作)。</summary>
    private static async Task CleanupRedisAsync(ICacheService cache, string instanceId, params string[] jobNames)
    {
        await cache.HashDeleteAsync(CacheConst.KeyJobNode, instanceId);
        foreach (var jobName in jobNames)
        {
            await cache.HashDeleteAsync(CacheConst.KeyJobRunning, jobName);
            await cache.RemoveAsync(CacheConst.KeyJobLock + jobName);
            // 批 AK:共享「上次触发时刻」基准键带 ≥1 小时 TTL,跨用例/跨进程存活——不清会把后续用例
            // 里「DB 基准已过期、期望首轮触发」的判据压成 0 次(尤其用例⑥ 与①②共用 beat 任务名)
            await cache.RemoveAsync(CacheConst.KeyJobLast + jobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例① 心跳:节点表写入且含标识(InstanceId/NodeTag/LastBeat)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例01_心跳_写入节点表且含标识()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await InsertProbeRowAsync(repo, BeatJobName, 3600, null, "test: AJ 心跳用例");

        AjProbeBeatJob.DelayMs = 0;
        AjProbeBeatJob.Started = 0;
        AjProbeBeatJob.Completed = 0;
        AjProbeBeatJob.RunningSnapshot = null;

        var scheduler = NewScheduler(provider, cache);
        try
        {
            // 先确认调度器预载成功(schedule 生效集含探针) —— 否则"心跳"可能是"调度器压根没跑起来"
            await scheduler.StartAsync(CancellationToken.None);
            var loaded = await WaitUntilAsync(() => scheduler.GetScheduledJobNames().Contains(BeatJobName), 8000);
            Assert.True(loaded, "调度器应在 8s 内预载并生效本探针任务(心跳用例的前置条件)");

            AjStoredNode? self = null;
            var found = await WaitUntilAsync(async () =>
            {
                self = ParseStored<AjStoredNode>(await RawHashGetAsync(CacheConst.KeyJobNode, scheduler.InstanceId));
                return self is not null;
            }, 8000);

            Assert.True(found && self is not null,
                $"心跳应在 8s 内写入节点表 Hash {CacheConst.KeyJobNode} 的 field={scheduler.InstanceId}");

            Assert.Equal(scheduler.InstanceId, self!.InstanceId);
            Assert.Equal(NodeTagA, self.NodeTag);
            Assert.NotNull(self.LastBeat);
            Assert.InRange(self.LastBeat!.Value, DateTime.Now.AddSeconds(-10), DateTime.Now.AddSeconds(10));
            Assert.NotNull(self.StartedAt);
            Assert.InRange(self.StartedAt!.Value, DateTime.Now.AddMinutes(-5), DateTime.Now.AddSeconds(10));
            Assert.True(await cache.ExistKeyAsync(CacheConst.KeyJobNode), "节点表键应存在");

            var raw = await RawHashGetAsync(CacheConst.KeyJobNode, scheduler.InstanceId);
            _output.WriteLine($"判定①:field={self.InstanceId},nodeTag={self.NodeTag},lastBeat={self.LastBeat:O}," +
                $"startedAt={self.StartedAt:O}");
            _output.WriteLine($"判定①(存储形态实证):{raw}");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, BeatJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId);
            await DeleteProbeRowsWithRetryAsync(repo, BeatJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例② JobCluster:Enabled=false ⇒ 不写节点表()其余实例行仍在)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例02_心跳_Enabled为false时不写节点()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();

        // 先手工植入"别的实例"的心跳行:断言"其余实例的行仍在"⇒ 证明 Enabled=false 只关掉自己的心跳,
        // 不是"节点表整体不可用"
        var otherInstance = $"{NodeTagA}:{Environment.ProcessId}:{"case01-sibling"}";
        await cache.HashSetAsync(CacheConst.KeyJobNode, otherInstance, new JobNodePayload
        {
            InstanceId = otherInstance,
            NodeTag = NodeTagA,
            StartedAt = DateTime.Now.AddMinutes(-1),
            LastBeat = DateTime.Now
        });

        using var provider = BuildProvider(cache, NodeTagB, clusterEnabled: false, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        await InsertProbeRowAsync(repo, BeatJobName, 3600, null, "test: AJ Enabled=false 用例");
        AjProbeBeatJob.DelayMs = 0;

        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var loaded = await WaitUntilAsync(() => scheduler.GetScheduledJobNames().Contains(BeatJobName), 8000);
            Assert.True(loaded, "Enabled=false 只关集群心跳,不应影响调度预载");

            // 跑满约 6 个 tick(200ms×6>1s),给心跳足够机会出现(若实现漏判开关,这里必被抓)
            await Task.Delay(1200);

            var nodes = await cache.HashGetAllAsync<JobNodePayload>(CacheConst.KeyJobNode);
            Assert.DoesNotContain(nodes, n => n.InstanceId == scheduler.InstanceId);
            Assert.Contains(nodes, n => n.InstanceId == otherInstance);

            _output.WriteLine($"判定②:本实例 {scheduler.InstanceId} 未入节点表(节点表 {nodes.Count} 行),旁证行 {otherInstance} 仍在");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, BeatJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId);
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, otherInstance);
            await DeleteProbeRowsWithRetryAsync(repo, BeatJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例③ 续租(本批核心):执行期锁 TTL 被 SetExpireAsync 拉回;执行毕锁键消失
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例03_续租_执行期锁TTL被延长()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();

        // WHY LockRenewSeconds=30(代码 Math.Max(30, …) 的钳底,最短续租周期):
        // 任务体 35s ⇒ 覆盖一次完整续租(30s 到点),同时把用例耗时压到约 40s。
        const int renewSeconds = 30;
        const int runSeconds = 35;
        AjProbeRenewConfig.Reset(runSeconds);

        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: renewSeconds);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // Interval=3600 + LastRunTime=10s 前 ⇒ 首轮 IsDue=false,调度循环不自动触发;
        // 只经 RunNow 手动触发一次(避免首轮自动触发占走锁再顶掉 RunNow——本批实测根因)
        await InsertProbeRowAsync(repo, RenewJobName, 3600, null, "test: AJ 续租用例", DateTime.Now.AddSeconds(-10));

        var lockKey = CacheConst.KeyJobLock + RenewJobName;
        await cache.RemoveAsync(lockKey);
        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var loaded = await WaitUntilAsync(() => scheduler.GetScheduledJobNames().Contains(RenewJobName), 8000);
            Assert.True(loaded, "调度器应在 8s 内预载探针任务(续租用例的前置条件)");

            var sw = Stopwatch.StartNew();
            var fired = await scheduler.RunNowAsync(RenewJobName);
            Assert.True(fired, "RunNow 应接受本次触发(无在途锁)");

            // 锁已建立:初始 TTL ≈ 1800s(LockTtl=30 分钟)
            var initialTtl = await cache.GetExpireAsync(lockKey);
            Assert.NotNull(initialTtl);
            Assert.InRange(initialTtl!.Value.TotalSeconds, 1700, 1810);

            // WHY 等待窗口 = 任务体时长 + 60s 余量:Release 下 JIT/调度排队会让 35s 任务体显著后移
            // (Debug 首跑实测 50s 窗口不够),窗口过紧会把"跑得慢"误判成"续租没发生"
            var completed = await WaitUntilAsync(() => AjProbeRenewConfig.Completed, (runSeconds + 60) * 1000);
            Assert.True(completed, $"探针任务体应在 {runSeconds + 60}s 内跑完(实际 {sw.Elapsed.TotalSeconds:F1}s)");
            sw.Stop();

            var samples = AjProbeRenewConfig.LockSamples;
            var maxTtl = samples.Count > 0 ? samples.Max() : -1;
            var minTtl = samples.Count > 0 ? samples.Min() : -1;

            // 强断言(续租实证):TTL 先被消耗(最小读数明显低于初始值)后又回升到接近完整 TTL
            Assert.NotEmpty(samples);
            Assert.True(minTtl < 1790,
                $"执行期锁 TTL 应出现过消耗(读数 <1790s),实际最小 {minTtl:F1}s,读数={string.Join(",", samples.Select(s => s.ToString("F1")))}");
            Assert.True(maxTtl >= 1740,
                $"执行期锁 TTL 应被续租拉回(读数 ≥1740s),实际最大 {maxTtl:F1}s,读数={string.Join(",", samples.Select(s => s.ToString("F1")))}");

            // 执行结束:锁键已被 finally 释放(立即读 + 短暂重试,避开与 finally 的毫秒级竞态)
            var gone = await WaitUntilAsync(async () => !await cache.ExistKeyAsync(lockKey), 3000);
            Assert.True(gone, $"执行结束后防重入锁应被释放(键 {lockKey} 仍存在)");

            _output.WriteLine($"判定③:续租间隔={renewSeconds}s,任务体 {sw.Elapsed.TotalSeconds:F1}s," +
                $"初始 TTL={initialTtl.Value.TotalSeconds:F1}s,执行期读数=[{string.Join(", ", samples.Select(s => s.ToString("F1")))}]," +
                $"max={maxTtl:F1}s / min={minTtl:F1}s,结束后 ExistKey={await cache.ExistKeyAsync(lockKey)}");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, RenewJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId, RenewJobName);
            await DeleteProbeRowsWithRetryAsync(repo, RenewJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例④ 归属:TargetNode 不匹配 ⇒ 从不执行且不加锁
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例04_归属_TargetNode不匹配则跳过()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // TargetNode=别的标签 + Interval=1(到期条件满足,否则"没执行"可能只是"没到期")
        await InsertProbeRowAsync(repo, HostSkipJobName, 1, "aj-node-other", "test: AJ 归属不匹配用例");
        AjProbeHostSkipJob.Executions = 0;

        var lockKey = CacheConst.KeyJobLock + HostSkipJobName;
        await cache.RemoveAsync(lockKey);
        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var loaded = await WaitUntilAsync(() => scheduler.GetScheduledJobNames().Contains(HostSkipJobName), 8000);
            Assert.True(loaded, "调度器应预载该行(归属过滤发生在触发前,不进生效集就无法断言「跳过」)");

            // 跑满约 15 个 tick:Interval=1 的到期任务若未被归属过滤,早该执行多次
            await Task.Delay(3000);
            Assert.Equal(0, AjProbeHostSkipJob.Executions);
            Assert.False(await cache.ExistKeyAsync(lockKey), $"不匹配归属的任务不应加锁(键 {lockKey} 存在 ⇒ 已进入取锁路径)");

            _output.WriteLine($"判定④:TargetNode=aj-node-other 对本机 NodeTag={NodeTagA} 3s 内执行 {AjProbeHostSkipJob.Executions} 次,锁键不存在");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, HostSkipJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId, HostSkipJobName);
            await DeleteProbeRowsWithRetryAsync(repo, HostSkipJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例⑤ 归属:TargetNode 与方法机一致 ⇒ 正常执行
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例05_归属_TargetNode匹配则执行()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // 与用例④同形,仅 TargetNode 不同(大小写刻意写大写,验证比对不区分大小写)
        await InsertProbeRowAsync(repo, HostOkJobName, 1, NodeTagA.ToUpperInvariant(), "test: AJ 归属匹配用例");
        AjProbeHostOkJob.Executions = 0;

        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var executed = await WaitUntilAsync(() => AjProbeHostOkJob.Executions >= 1, 8000);
            Assert.True(executed,
                $"TargetNode 与本机 NodeTag 一致(忽略大小写)时应执行,实际执行 {AjProbeHostOkJob.Executions} 次");

            _output.WriteLine($"判定⑤:TargetNode={NodeTagA.ToUpperInvariant()} 对 NodeTag={NodeTagA} 执行 {AjProbeHostOkJob.Executions} 次");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, HostOkJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId, HostOkJobName);
            await DeleteProbeRowsWithRetryAsync(repo, HostOkJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例⑥ 运行中登记:执行期有行(instanceId/trigger=scheduled),结束后该行被清理
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例06_运行中登记_执行期有行执行后清理()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // WHY Interval=3600 + LastRunTime=1 小时前:首轮 IsDue 恰好到点一次,执行后 LastRunTime=开始时刻 ⇒ 3600s 内不再重复;
        // 若 Interval=1 无 LastRunTime ⇒ 探针反复触发,field 被后续轮次重新登记,"执行结束后已删除"永不成立(本批实测)
        await InsertProbeRowAsync(repo, BeatJobName, 3600, null, "test: AJ 运行中登记用例",
            DateTime.Now.AddSeconds(-3601));
        AjProbeBeatJob.DelayMs = 3000; // 任务体阻塞 3s ⇒ 外部/体内都能观察到在途窗口
        AjProbeBeatJob.Started = 0;
        AjProbeBeatJob.Completed = 0;
        AjProbeBeatJob.RunningSnapshot = null;
        await cache.HashDeleteAsync(CacheConst.KeyJobRunning, BeatJobName);
        // 批 AK:共享「上次触发时刻」基准键是跨实例权威判据(键 TTL ≥1 小时,跨用例存活)。
        // 本用例的前置是「Interval=3600 + LastRunTime=1 小时前 ⇒ 首轮到期一次」,若残留上一次运行(用例①/②
        // 也跑同名 beat 任务)写下的基准,复检会正确判「未到点」⇒ 探针不执行、用例假红 ⇒ 前置逐键清(零污染同族)
        await cache.RemoveAsync(CacheConst.KeyJobLast + BeatJobName);

        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var inFlight = await WaitUntilAsync(() => AjProbeBeatJob.Started >= 1, 8000);
            Assert.True(inFlight, "探针应被触发(运行中登记用例的前置条件)");

            // 外部视角(仍在途:DelayMs=3000):Hash 里应有本任务行(按真实存储形态解析,见 ParseStored WHY)
            var duringRaw = await RawHashGetAsync(CacheConst.KeyJobRunning, BeatJobName);
            var during = ParseStored<AjStoredRunning>(duringRaw);
            Assert.NotNull(during);
            Assert.Equal(BeatJobName, during!.JobName);
            Assert.Equal(scheduler.InstanceId, during.InstanceId);
            Assert.Equal(NodeTagA, during.NodeTag);
            Assert.Equal("scheduled", during.Trigger);
            Assert.NotNull(during.StartedAt);

            var finished = await WaitUntilAsync(() => AjProbeBeatJob.Completed >= 1, 15000);
            Assert.True(finished, "探针应在 15s 内执行完毕");

            // 任务体内视角(执行中途采样)——与外部视角互证,防"外部读到的是别轮残留"
            Assert.NotNull(AjProbeBeatJob.RunningSnapshot);
            Assert.Equal(scheduler.InstanceId, AjProbeBeatJob.RunningSnapshot!.InstanceId);
            Assert.Equal("scheduled", AjProbeBeatJob.RunningSnapshot.Trigger);

            // 执行结束后登记行必须被 finally 清理
            var cleaned = await WaitUntilAsync(
                async () => await RawHashGetAsync(CacheConst.KeyJobRunning, BeatJobName) is null, 5000);
            Assert.True(cleaned, $"执行结束后运行中登记行应被删除(Hash {CacheConst.KeyJobRunning} 仍含 field {BeatJobName})");

            _output.WriteLine($"判定⑥:在途行 instanceId={during.InstanceId},nodeTag={during.NodeTag},trigger={during.Trigger}," +
                $"startedAt={during.StartedAt:O};执行毕 field 已删");
            _output.WriteLine($"判定⑥(存储形态实证):{duringRaw}");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, BeatJobName);
            await CleanupRedisAsync(cache, scheduler.InstanceId, BeatJobName);
            await DeleteProbeRowsWithRetryAsync(repo, BeatJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例⑦ 僵尸节点读侧过滤(lastBeat 早于 now-90s ⇒ 不可见)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例07_僵尸节点_读侧过滤()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // 先清空节点表:本用例只需手工单层节点(端点判据与其它用例的真实心跳行互不干扰,见 ClearNodeTableAsync WHY)
        await ClearNodeTableAsync();

        // 手工伪造僵尸节点(LastBeat=5 分钟前)与存活节点(LastBeat=现在),InstanceId 带 GUID ⇒ 只与本用例相关
        var ghostId = "aj-ghost-" + Guid.NewGuid().ToString("N");
        var liveId = "aj-live-" + Guid.NewGuid().ToString("N");
        await cache.HashSetAsync(CacheConst.KeyJobNode, ghostId, new JobNodePayload
        {
            InstanceId = ghostId,
            NodeTag = "aj-ghost-node",
            StartedAt = DateTime.Now.AddHours(-1),
            LastBeat = DateTime.Now.AddMinutes(-5)
        });
        await cache.HashSetAsync(CacheConst.KeyJobNode, liveId, new JobNodePayload
        {
            InstanceId = liveId,
            NodeTag = "aj-live-node",
            StartedAt = DateTime.Now.AddMinutes(-1),
            LastBeat = DateTime.Now
        });

        var scheduler = NewScheduler(provider, cache);
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => scheduler.GetScheduledJobNames() is not null, 3000);

            // 直接调控制器 Nodes():复用端点真实过滤口径(不自行复刻 90s 判据,避免"复刻对了、端点错了"假绿)
            var controller = new SysJobController(
                new SysJobService(repo, scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobLog>>(), scheduler), scheduler, cache);
            var result = await controller.Nodes();
            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<ApiResult<JobClusterOutput>>(ok.Value);

            Assert.Equal(0, payload.Code);
            Assert.NotNull(payload.Data);
            Assert.DoesNotContain(payload.Data.Nodes, n => n.InstanceId == ghostId);
            var live = payload.Data.Nodes.FirstOrDefault(n => n.InstanceId == liveId);
            Assert.NotNull(live);
            Assert.True(live!.IsAlive, "90s 内的心跳节点应判为存活(反证过滤不是「谁都滤掉」)");

            _output.WriteLine($"判定⑦:端点 nodes 共 {payload.Data.Nodes.Count} 行;ghost(LastBeat=-5min) 不可见," +
                $"live(LastBeat=now) 可见且 IsAlive={live.IsAlive}");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, ghostId);
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, liveId);
            await CleanupRedisAsync(cache, scheduler.InstanceId);
            await DeleteProbeRowsWithRetryAsync(repo, BeatJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 用例⑧ 端点:IsSelf 标注精确指向本实例
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例08_端点_含本实例标注()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTagA, clusterEnabled: true, renewSeconds: 300);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // 先清表(见 ClearNodeTableAsync WHY):端点只面对手工植入的两行
        await ClearNodeTableAsync();

        // ①本实例行:field/instanceId 逐字等于 scheduler.InstanceId(端点 isSelf 判据正是逐字比对)
        // ②旁证行(LastBeat=now,存活但非本实例)⇒ 断言 IsSelf 不是"恒 true"
        var siblingId = "aj-sibling-" + Guid.NewGuid().ToString("N");
        var scheduler = NewScheduler(provider, cache);
        var selfRow = new JobNodePayload
        {
            InstanceId = scheduler.InstanceId,
            NodeTag = NodeTagA,
            StartedAt = DateTime.Now.AddMinutes(-1),
            LastBeat = DateTime.Now
        };
        await cache.HashSetAsync(CacheConst.KeyJobNode, scheduler.InstanceId, selfRow);
        await cache.HashSetAsync(CacheConst.KeyJobNode, siblingId, new JobNodePayload
        {
            InstanceId = siblingId,
            NodeTag = NodeTagB,
            StartedAt = DateTime.Now.AddMinutes(-2),
            LastBeat = DateTime.Now
        });

        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => scheduler.GetScheduledJobNames() is not null, 3000);
            // 心跳一旦跑起来会把本实例 field 覆盖成双层真实形态 ⇒ 端点整表反序列化必抛;
            // 故等待"field 已被心跳覆盖"作为心跳生效证据,随后用直读原始值断言真实心跳行
            var overwritten = await WaitUntilAsync(
                async () => (await RawHashGetAsync(CacheConst.KeyJobNode, scheduler.InstanceId)) is { Length: > 0 } raw
                             && raw.Contains("lastBeat", StringComparison.OrdinalIgnoreCase), 8000);
            Assert.True(overwritten, "调度器心跳应把本实例 field 覆盖为真实心跳载荷");

            var controller = new SysJobController(new SysJobService(repo, scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobLog>>(), scheduler), scheduler, cache);
            var result = await controller.Nodes();
            var payload = Assert.IsType<ApiResult<JobClusterOutput>>(Assert.IsType<OkObjectResult>(result).Value);

            var selfs = payload.Data.Nodes.Where(n => n.IsSelf).ToList();
            Assert.Single(selfs);
            Assert.Equal(scheduler.InstanceId, selfs[0].InstanceId);
            Assert.Equal(NodeTagA, selfs[0].NodeTag);
            Assert.DoesNotContain(payload.Data.Nodes, n => n.InstanceId == siblingId && n.IsSelf);

            // 真实心跳行:按存储形态直读断言(端点侧受生产双层序列化缺陷限制,详见报告)
            var realBeat = ParseStored<AjStoredNode>(await RawHashGetAsync(CacheConst.KeyJobNode, scheduler.InstanceId));
            Assert.NotNull(realBeat);
            Assert.Equal(scheduler.InstanceId, realBeat!.InstanceId);
            Assert.Equal(NodeTagA, realBeat.NodeTag);
            Assert.InRange(realBeat.LastBeat!.Value, DateTime.Now.AddSeconds(-10), DateTime.Now.AddSeconds(10));

            _output.WriteLine($"判定⑧:IsSelf 行数={selfs.Count},instanceId={selfs[0].InstanceId},nodeTag={selfs[0].NodeTag};" +
                $"旁证伪节点 {siblingId} 存活但非 self");
            _output.WriteLine($"判定⑧(真实心跳行存储形态):{await RawHashGetAsync(CacheConst.KeyJobNode, scheduler.InstanceId)}");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, BeatJobName);
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, siblingId);
            await CleanupRedisAsync(cache, scheduler.InstanceId);
            await DeleteProbeRowsWithRetryAsync(repo, BeatJobName);
        }
    }
}