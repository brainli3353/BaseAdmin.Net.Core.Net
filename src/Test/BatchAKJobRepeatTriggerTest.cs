using System.Diagnostics;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using ICacheService = AdminCore.Core.Cache.ICacheService;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Job;
using AdminCore.Core.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace AdminCore.Test;

// ============================================================================
// 批 AK「调度器重复触发」验证件(缺陷已由「共享权威上次触发时刻 + 锁内复检」修复)。
// 被测主体 = JobScheduler 的到期判定与触发链路(src/Core/Job/JobScheduler.cs):
//   IsDue 读内存快照 _schedules 的 LastRunTime(快速筛选)→ FireAsync 取防重入锁 →
//   **锁内以共享键 CacheConst.KeyJobLast 复检(权威判据)**→ 到点则写共享键 + 回写内存快照再执行。
// 修复前的缺陷(本文件即由它立项):触发成功后 ExecuteTriggeredAsync 只写 DB 行、不回写内存快照,
//   而快照每 12 tick(缺省 60s)才整表重载 ⇒ 任务执行完、锁释放后,刷新前的每个 tick 都被反复判到期
//   并重复执行(实测窗口 5.1s 内执行 11 次,期望 1)。
// 判据①(单实例):到期任务在快照刷新窗口内只执行一次(执行次数 == 1)。
// 判据②(跨实例,核心证明):两实例共享同一真实 Redis 档 ICacheService、各自独立内存快照,
//   且**故意把 DB 基准改回陈旧** ⇒ 只有共享权威基准能挡住第二轮;断言全集群执行次数 == 1,
//   并以"另一实例的专属任务确实执行了 1 次"反证该实例的调度链路可用(排除"B 没跑起来"的假绿)。
// 探针任务名 ak_probe_repeat / ak2_probe_* 前缀专属,与既有 test_probe/aj_probe_*/clean_sys_log 不撞。
// ============================================================================

/// <summary>
/// 重复触发探针:任务体立即返回(执行窗口最短 ⇒ 锁释放最快 ⇒ 重复触发窗口内次数最多),
/// 静态计数器记录进入任务体的次数。计数器 static + Interlocked:调度执行在后台线程,用例内等待可见。
/// </summary>
[JobName("ak_probe_repeat")]
public sealed class AkProbeRepeatJob : IJob
{
    /// <summary>进入任务体的次数(每次成功取锁的执行 +1)。</summary>
    public static int Executions;

    public Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 判据②跨实例竞争探针:两实例共用同一任务行,谁先取锁谁执行;共享基准令后到者复检挡下。
/// 独立类 = 独立静态计数,与判据①/自身"独享任务"探针互不串扰。
/// </summary>
[JobName("ak2_probe_cluster")]
public sealed class Ak2ProbeClusterJob : IJob
{
    /// <summary>进入任务体的次数(全集群合计:两个调度器实例都在本进程,静态计数即"全集群")。</summary>
    public static int Executions;

    public Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 判据②反证探针:TargetNode 只匹配实例 B ⇒ 只能由 B 执行。
/// WHY 必要:若 B 因装置问题压根没跑起来,"全集群只执行一次"就会**假绿**;
/// 本探针执行满 1 次即证明 B 的预载/到期判定/取锁/复检/执行链路真实可用。
/// </summary>
[JobName("ak2_probe_own_b")]
public sealed class Ak2ProbeOwnBJob : IJob
{
    /// <summary>进入任务体的次数(期望恰 1:证明 B 能执行,同时再次钉住"单实例同窗口只一次")。</summary>
    public static int Executions;

    public Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 批 AK:定时任务重复触发防护——单实例(判据①)与跨实例(判据②)两个方向各自钉住
/// 「同一 Interval 窗口内至多执行一次」。
/// </summary>
/// <remarks>
/// 装置照抄复用 BatchAJJobClusterTest:真实 PG(SqlSugar)+ 真实 Redis 档 ICacheService(6380)
/// + tick 注入 200ms(生产缺省 5000ms)+ 专属探针任务类 + 带重试的探针行清理。
/// WHY 全部走真实依赖:被测的是调度循环本身的时间行为与**跨实例缓存可见性**,
/// 内存替身会掩盖 tick/锁竞态细节(Memory 档的键只在单进程内可见,证明不了集群契约)。
/// 零污染:只删本文件自造任务名与自身 Redis 键(锁键/基准键/Hash field 逐键删,绝不全库操作)。
/// 关键前置:用例开始前必清 <see cref="CacheConst.KeyJobLast"/>——残留基准(1 小时 TTL)
/// 会把本轮判成「未到点」⇒ 计数 0 而非 1,同样判红,且会污染后续用例。
/// </remarks>
public class BatchAKJobRepeatTriggerTest : IDisposable
{
    private readonly ITestOutputHelper _output;

    // 开发库连接串(与 BatchAJJobClusterTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 环境事实(与 BatchAFCacheQueueTest/BatchAJ 同源):6379 是需口令的业务 Redis,6380 才是无口令的 eventbus 实例
    private const string RedisHost = "127.0.0.1";
    private const int RedisPort = 6380;

    private const int TickMs = 200;

    /// <summary>本用例专属探针任务名(与 BatchAJ 探针、内置 clean_sys_log、test_probe 均不撞)。</summary>
    private const string ProbeJobName = "ak_probe_repeat";

    /// <summary>判据②跨实例竞争探针任务名(两实例共用同一库行)。</summary>
    private const string ClusterProbeJobName = "ak2_probe_cluster";

    /// <summary>判据②反证探针任务名(TargetNode 只匹配实例 B,证明 B 的调度链路真的能执行)。</summary>
    private const string OwnProbeJobName = "ak2_probe_own_b";

    /// <summary>探针调度间隔(秒):用例窗口(约 5s)远小于该间隔 ⇒ 快照一旦刷新读到刚写的 LastRunTime 即停止触发,总次数收敛。</summary>
    private const int ProbeIntervalSeconds = 10;

    /// <summary>判据②调度间隔(秒):取 60s ⇒ 观察窗口(5s)与快照刷新(2.4s)都落在同一轮之内,期望全集群恰 1 次。</summary>
    private const int ClusterIntervalSeconds = 60;

    /// <summary>判据②「上次基准」预置为 N 秒前:大于 Interval ⇒ 首轮即到期(竞争的起点条件)。</summary>
    private const int StaleBaseSeconds = 120;

    /// <summary>用例观察窗口(毫秒):tick=200ms ⇒ 约 25 轮;首刷快照在 12 tick=2.4s 处。窗口远大于一个 tick。</summary>
    private const int ObserveWindowMs = 5000;

    /// <summary>本实例节点标签(带 GUID,与其它用例/环境的节点行天然不冲突)。</summary>
    private static readonly string NodeTag = "ak-node-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>判据②实例 A 标签(独立进程内调度器 A 的身份;带 GUID 防串扰)。</summary>
    private static readonly string ClusterNodeTagA = "ak2-node-a-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>判据②实例 B 标签(独立进程内调度器 B 的身份)。</summary>
    private static readonly string ClusterNodeTagB = "ak2-node-b-" + Guid.NewGuid().ToString("N")[..8];

    private IConnectionMultiplexer? _redis;

    private readonly Task<IConnectionMultiplexer?> _redisConnect;

    private bool _redisProbed;
    private bool _redisReachable;

    public BatchAKJobRepeatTriggerTest(ITestOutputHelper output)
    {
        _output = output;
        // 构造期只发起异步连接(不在构造函数里同步等待,避免 xUnit 构造超时);用例首次 await 时兑现
        _redisConnect = ConnectRedisAsync();
    }

    public void Dispose()
    {
        _redis?.Dispose();
    }

    // ── 环境探测(与 BatchAJ 同口径:不可达即显式降级判红,禁止静默跳过冒充通过)──────

    /// <summary>PG 可达性探测(短超时 TCP,不依赖 ORM)。</summary>
    private static async Task<bool> IsPostgresReachableAsync()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(DbHost, DbPort).WaitAsync(TimeSpan.FromSeconds(3));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

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
        return new RedisCacheService(_redis!);
    }

    // ── 容器/调度器(照抄 BatchAJ 装置,探针按具体类注册)────────────────────────

    private static ServiceProvider BuildProvider(ICacheService cache, string nodeTag)
    {
        var vars = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = DbConnectionString,
            ["Yit:WorkerId"] = "1",
            ["JobCluster:NodeTag"] = nodeTag,
            ["JobCluster:Enabled"] = "true",
            ["JobCluster:LockRenewSeconds"] = "300"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        var services = new ServiceCollection();
        // WHY 显式 <IConfiguration>:.NET 10 起 Build() 返回 IConfigurationRoot,裸注册以具体类型为键,
        // JobScheduler 注入 IConfiguration 解析不到(BatchAJ 实机诊断实证)
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSqlSugar(config);
        services.AddSingleton(cache);

        // 调度器 GetRequiredService(_jobTypes[name]) 按具体类型解析 ⇒ 必须注册探针具体类
        services.AddScoped<AkProbeRepeatJob>();
        services.AddScoped<Ak2ProbeClusterJob>();
        services.AddScoped<Ak2ProbeOwnBJob>();
        return services.BuildServiceProvider();
    }

    private static JobScheduler NewScheduler(IServiceProvider provider, ICacheService cache)
        => new(provider, cache, provider.GetRequiredService<ILogger<JobScheduler>>(),
            new[] { typeof(BatchAKJobRepeatTriggerTest).Assembly }, TickMs);

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

    /// <summary>清理探针行(带重试:与在途收尾的调度器共用 scope 连接可能瞬撞,BatchAJ 实证)。</summary>
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

    /// <summary>等探针防重入锁消失(= 最后一轮在途执行已跑完 finally,BatchAJ 同口径)。</summary>
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
    /// 清探针在 Redis 的全部痕迹:防重入锁键 + 共享「上次触发时刻」基准键 + 运行中登记 field。
    /// WHY 必须清基准键:它带 ≥1 小时 TTL,跨用例/跨进程存活——残留会把本轮判成「未到点」(计数 0),
    /// 与真缺陷(计数 &gt; 1)同样判红,还会让后续同名探针用例失真 ⇒ 前置清 + finally 清双保险。
    /// </summary>
    private static async Task ClearProbeKeysAsync(ICacheService cache, params string[] jobNames)
    {
        foreach (var jobName in jobNames)
        {
            await cache.RemoveAsync(CacheConst.KeyJobLock + jobName);
            await cache.RemoveAsync(CacheConst.KeyJobLast + jobName);
            await cache.HashDeleteAsync(CacheConst.KeyJobRunning, jobName);
        }
    }

    /// <summary>
    /// 带重试改写探针行的 LastRunTime(判据②用它把 DB 基准**故意改回陈旧**,以逼出共享基准的独立作用)。
    /// WHY 重试:测试与调度器连同一真实库,调度器每 12 tick(2.4s)刷新快照的查询可能与本写入瞬撞
    /// (框架级 SqlSugarScope 并发限制,见差距清单 G7;BatchAJ 的删除重试同因)。
    /// </summary>
    private static async Task UpdateProbeLastRunTimeWithRetryAsync(
        ISqlSugarRepository<SysJobDetail> repo, string jobName, DateTime lastRunTime)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                var row = await repo.GetFirstAsync(u => u.JobName == jobName);
                if (row is null)
                {
                    return;
                }

                row.LastRunTime = lastRunTime;
                await repo.UpdateAsync(row);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(300 * attempt);
            }
        }

        var tail = await repo.GetFirstAsync(u => u.JobName == jobName);
        if (tail is not null)
        {
            tail.LastRunTime = lastRunTime;
            await repo.UpdateAsync(tail);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 判据① 单实例:到期任务在快照刷新窗口内只执行一次(重复触发防护的回归钉)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 用例01_重复触发_到期任务在快照刷新窗口内只执行一次()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();
        using var provider = BuildProvider(cache, NodeTag);
        InitDb(provider);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        // 探针行:Interval=10s、LastRunTime=30s 前(已过期)⇒ 启动首轮即到期。
        // 缺陷口径(修复前):触发成功后只把 LastRunTime 写 DB 行,内存快照仍停在预载时那个 30s 前的值
        //   ⇒ 每 tick(200ms)都判到期;执行体立即返回、锁随即释放 ⇒ 窗口内反复执行(实测 5.1s 内 11 次)。
        // 修复口径:锁内复检到点后先写共享基准键、再回写内存快照 ⇒ 下一 tick IsDue 即 false,窗口内恰 1 次。
        await repo.DeletePhysicalByAsync(u => u.JobName == ProbeJobName);
        await repo.InsertAsync(new SysJobDetail
        {
            JobName = ProbeJobName,
            Interval = ProbeIntervalSeconds,
            Enabled = true,
            TargetNode = null,
            Remark = "test: AK 判据① 单实例重复触发",
            LastRunTime = DateTime.Now.AddSeconds(-30)
        });

        AkProbeRepeatJob.Executions = 0;
        await ClearProbeKeysAsync(cache, ProbeJobName);

        var scheduler = NewScheduler(provider, cache);
        var sw = Stopwatch.StartNew();
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            var loaded = await WaitUntilAsync(() => scheduler.GetScheduledJobNames().Contains(ProbeJobName), 8000);
            Assert.True(loaded, "调度器应在 8s 内预载探针行(判据前置条件:任务确已生效并被到期触发)");

            // 观察窗口 5s:远大于一个 tick(200ms),也跨过首个快照刷新(12 tick=2.4s)——
            // 无论刷新前(靠回写的快照)还是刷新后(靠 DB 行新值),计数都应停在 1。
            await Task.Delay(ObserveWindowMs);

            var executions = AkProbeRepeatJob.Executions;
            var sharedLast = await cache.GetAsync<DateTime?>(CacheConst.KeyJobLast + ProbeJobName);
            var dbLastRun = (await repo.GetFirstAsync(u => u.JobName == ProbeJobName))?.LastRunTime;
            _output.WriteLine($"判定①:窗口 {sw.Elapsed.TotalSeconds:F1}s 内执行次数={executions}(期望 1);" +
                $"共享基准 KeyJobLast={sharedLast:O};DB.LastRunTime={dbLastRun:O}(二者应与本次触发时刻一致)");

            // 契约:一个到期周期(Interval=10s)内单实例至多执行一次 ⇒ 期望恰 1 次(0=漏触发,同样判红)。
            Assert.True(executions == 1,
                "重复触发防护失效:任务在同一 Interval 内被执行多次(或一次都没跑)。机制要求——FireAsync 取到"
                + "防重入锁后须在**锁内**以共享 KeyJobLast 基准复检,到点则先写基准再回写内存快照,不得只靠 60s"
                + $"才重载的内存快照判到期。(观察窗口 {sw.Elapsed.TotalSeconds:F1}s,tick={TickMs}ms,"
                + $"实际执行次数={executions},期望=1)");

            // 判据的判据:本次触发时刻必须已进共享缓存(那才是跨实例可见的权威基准)
            Assert.NotNull(sharedLast);
            Assert.InRange(sharedLast!.Value, DateTime.Now.AddSeconds(-15), DateTime.Now.AddSeconds(2));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
            await SettleInFlightAsync(cache, ProbeJobName);
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, scheduler.InstanceId);
            await ClearProbeKeysAsync(cache, ProbeJobName);
            await DeleteProbeRowsWithRetryAsync(repo, ProbeJobName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 判据② 跨实例(核心证明):两实例共享同一真实 Redis 档缓存,同一 Interval 窗口内全集群只执行一次
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 两个 JobScheduler 实例(各自独立 DI 容器 ⇒ 独立 SqlSugar 单例 + 独立内存快照)、
    /// **共享同一个真实 Redis 档 ICacheService**、共用同一张 sys_job_detail 的同一任务行。
    /// 竞争点被刻意做硬:实例 A 执行完把 DB 基准改回陈旧(120s 前)再启 B ⇒
    /// B 预载的内存快照判「已过期」,A 的快照也会被 2.4s 周期刷新重置回同一陈旧值 ⇒
    /// **只有共享权威基准能挡住这一轮**(这正是"权威判据必须落在共享缓存、不能只靠本地快照"的证明点)。
    /// 反证假绿:B 的专属任务(TargetNode 只匹配 B)必须由 B 执行满 1 次 ⇒ 证明 B 的调度链路真的在跑。
    /// </summary>
    [Fact]
    public async Task 用例02_跨实例重复触发_两实例同窗口全集群只执行一次()
    {
        if (!await IsPostgresReachableAsync())
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var cache = await RequireRedisCacheAsync();

        // 实例 A:容器 + 建表(同一真实库,B 无需再建);实例 B 的容器在阶段②才构造(见下)
        using var providerA = BuildProvider(cache, ClusterNodeTagA);
        InitDb(providerA);
        using var scopeA = providerA.CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();

        var probes = new (string JobName, string? TargetNode)[]
        {
            (ClusterProbeJobName, null),      // 竞争探针:任意实例可执行
            (OwnProbeJobName, ClusterNodeTagB) // 反证探针:只有实例 B 能执行
        };
        foreach (var probe in probes)
        {
            await repoA.DeletePhysicalByAsync(u => u.JobName == probe.JobName);
            await repoA.InsertAsync(new SysJobDetail
            {
                JobName = probe.JobName,
                Interval = ClusterIntervalSeconds,
                Enabled = true,
                TargetNode = probe.TargetNode,
                Remark = "test: AK 判据② 跨实例重复触发",
                LastRunTime = DateTime.Now.AddSeconds(-StaleBaseSeconds)
            });
        }

        Ak2ProbeClusterJob.Executions = 0;
        Ak2ProbeOwnBJob.Executions = 0;
        await ClearProbeKeysAsync(cache, ClusterProbeJobName, OwnProbeJobName);

        var schedulerA = NewScheduler(providerA, cache);
        ServiceProvider? providerB = null;
        JobScheduler? schedulerB = null;
        var sw = Stopwatch.StartNew();
        try
        {
            // 阶段①:只起实例 A ⇒ 本轮触发权必然归它(写共享基准 + 落 DB + 执行 1 次)
            await schedulerA.StartAsync(CancellationToken.None);
            var loadedA = await WaitUntilAsync(() => schedulerA.GetScheduledJobNames().Contains(ClusterProbeJobName), 8000);
            Assert.True(loadedA, "实例 A 应在 8s 内预载竞争探针(阶段①的前置条件)");

            var aFired = await WaitUntilAsync(() => Ak2ProbeClusterJob.Executions >= 1, 8000);
            Assert.True(aFired, $"实例 A 应把到期的竞争探针执行一次,实际 {Ak2ProbeClusterJob.Executions} 次(否则判据②没有对手)");
            await SettleInFlightAsync(cache, ClusterProbeJobName);

            var lastAfterA = await cache.GetAsync<DateTime?>(CacheConst.KeyJobLast + ClusterProbeJobName);
            Assert.NotNull(lastAfterA);

            // 阶段②:把 DB 基准**故意改回陈旧**(= 模拟"另一个实例刚跑完但库里基准仍是旧的"这类本地不可信局面),
            // 再起实例 B。B 预载读到的就是"已过期"⇒ 每个 tick 都判到期并取锁,与 A 的取锁互相错开。
            await UpdateProbeLastRunTimeWithRetryAsync(repoA, ClusterProbeJobName, DateTime.Now.AddSeconds(-StaleBaseSeconds));

            providerB = BuildProvider(cache, ClusterNodeTagB);
            schedulerB = NewScheduler(providerB, cache);
            await schedulerB.StartAsync(CancellationToken.None);

            var loadedB = await WaitUntilAsync(() =>
                schedulerB.GetScheduledJobNames().Contains(ClusterProbeJobName)
                && schedulerB.GetScheduledJobNames().Contains(OwnProbeJobName), 8000);
            Assert.True(loadedB, "实例 B 应在 8s 内预载两枚探针(跨实例竞争的前置条件)");

            // 观察窗口 5s ≈ 25 个 tick:两实例都在循环里判到期、都在取锁。
            // 修复前:B 取到锁即执行(第 2 次起),A 刷新后同样再执行 ⇒ 全集群次数远大于 1。
            // 修复后:共享基准令二者在锁内复检时一律挡下 ⇒ 全集群合计恰 1 次。
            await Task.Delay(ObserveWindowMs);

            var clusterExecutions = Ak2ProbeClusterJob.Executions;
            var ownExecutions = Ak2ProbeOwnBJob.Executions;
            var lastAfterWindow = await cache.GetAsync<DateTime?>(CacheConst.KeyJobLast + ClusterProbeJobName);
            var dbLastRun = (await repoA.GetFirstAsync(u => u.JobName == ClusterProbeJobName))?.LastRunTime;

            _output.WriteLine($"判定②:窗口 {sw.Elapsed.TotalSeconds:F1}s 内竞争探针全集群执行={clusterExecutions}(期望 1);" +
                $"B 专属探针执行={ownExecutions}(期望 1,反证 B 调度链路可用);" +
                $"共享基准 A 触发后={lastAfterA:O} → 窗口末={lastAfterWindow:O};DB.LastRunTime={dbLastRun:O}" +
                "(DB 基准已被人为改回陈旧,只有共享基准能挡住后续轮次)");

            Assert.True(clusterExecutions == 1,
                "跨实例重复触发防护失效:两实例在同一 Interval 窗口内合计执行 "
                + $"{clusterExecutions} 次(期望 1)。机制要求——共享 KeyJobLast 基准须在**防重入锁内**复检,"
                + "未到点即释放锁、不执行、并把自己的内存快照对齐权威值。"
                + $"(A/B 标签={ClusterNodeTagA}/{ClusterNodeTagB},tick={TickMs}ms,DB 基准已改回 {StaleBaseSeconds}s 前)");

            // 被挡下的一轮**不得改写**权威基准(挡下 = 没触发 = 上次触发时刻不变)
            Assert.NotNull(lastAfterWindow);
            Assert.True(Math.Abs((lastAfterWindow!.Value - lastAfterA!.Value).TotalMilliseconds) < 1,
                $"共享基准被挡下的一轮改写了:{lastAfterA:O} → {lastAfterWindow:O}(复检不通过时不得写 KeyJobLast)");

            Assert.True(ownExecutions == 1,
                $"实例 B 的专属探针执行 {ownExecutions} 次(期望 1)——该任务 TargetNode 只匹配 B:"
                + "0 次说明 B 的预载/到期判定/取锁/复检/执行链路根本没跑起来,"
                + "此时「竞争探针只执行一次」属假绿(没有对手);多次则说明单实例侧防护同样失效");
        }
        finally
        {
            await schedulerA.StopAsync(CancellationToken.None);
            schedulerA.Dispose();
            if (schedulerB is not null)
            {
                await schedulerB.StopAsync(CancellationToken.None);
                schedulerB.Dispose();
            }

            await SettleInFlightAsync(cache, ClusterProbeJobName, OwnProbeJobName);
            await cache.HashDeleteAsync(CacheConst.KeyJobNode, schedulerA.InstanceId);
            if (schedulerB is not null)
            {
                await cache.HashDeleteAsync(CacheConst.KeyJobNode, schedulerB.InstanceId);
            }

            await ClearProbeKeysAsync(cache, ClusterProbeJobName, OwnProbeJobName);
            await DeleteProbeRowsWithRetryAsync(repoA, ClusterProbeJobName);
            await DeleteProbeRowsWithRetryAsync(repoA, OwnProbeJobName);
            providerB?.Dispose();
        }
    }
}
