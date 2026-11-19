// 批 S-G 纵深期·错误码覆盖补漏小片（D 域·定时任务 6 码）——真库档（写 `sys_job_detail` 探针行）。
// 覆盖：job.name_required / interval_invalid（Add 与 Update **双入口**）/ code_unregistered / exists / not_found / run_rejected。
// WHY 用测试内 fake 调度器：`IJobScheduler` 的真身在生产里是**后台托管调度器**（扫库+防重入锁+线程），
//   夹具里拉起来会把"调度是否已启动"变成断言前提（不稳定），且 `run_rejected` 的真实语义就是
//   "任务未注册代码 **或** 上一轮未完成"（`IJobScheduler.RunNowAsync` 注释原文）——两种因由都返回 false。
//   ⇒ 用 fake 把"注册集"与"触发结果"变成**输入**，比拉真调度器更精确，且不引入线程/时间依赖。
//   生产调度器自身的端到端不在本片职责内（属运行面）。
// 纪律：真实 PG；探针 finally 物理删；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Job;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Job;
using AdminCore.Core.Services.Job.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageJobTest : IAsyncLifetime
{
    private readonly List<long> _jobIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_jobIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        var ids = _jobIds.ToArray();
        await scope.Db.Deleteable<SysJobDetail>().In(ids).ExecuteCommandAsync();
    }

    [Fact]
    public async Task 定时任务域_六条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysJobDetail>>();
        var logRepo = sp.GetRequiredService<ISqlSugarRepository<SysJobLog>>();
        var registered = $"sgx_job_{Guid.NewGuid():N}"[..24];
        var svc = new SysJobService(repo, logRepo, new FakeScheduler([registered], runResult: false));

        // ① 任务名空白 → job.name_required
        var ex1 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddJobInput(" ")));
        Assert.Equal(ErrorCodes.JobNameRequired, ex1.ErrorCode);

        // ② 间隔 ≤ 0 → job.interval_invalid(Add 入口)
        var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddJobInput(registered, 0)));
        Assert.Equal(ErrorCodes.JobIntervalInvalid, ex2.ErrorCode);

        // ③ 任务名未代码注册 → job.code_unregistered(禁止建"配得上但永远跑不了"的调度行)
        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddJobInput($"sgx_ghost_{Guid.NewGuid():N}"[..24], 60)));
        Assert.Equal(ErrorCodes.JobCodeUnregistered, ex3.ErrorCode);

        // ④ 同名已建 → job.exists(先插已注册名探针行)
        var probe = new SysJobDetail { JobName = registered, Interval = 60, Enabled = true, Remark = string.Empty };
        await repo.InsertAsync(probe);
        _jobIds.Add(probe.Id);
        var ex4 = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(new AddJobInput(registered, 60)));
        Assert.Equal(ErrorCodes.JobExists, ex4.ErrorCode);

        // ⑤ 不存在 id → job.not_found(更新/立即触发两个入口同码)
        var missing = 990_000_000_000_940L;
        var ex5a = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateJobInput(missing, Interval: 60)));
        Assert.Equal(ErrorCodes.JobNotFound, ex5a.ErrorCode);

        var ex5b = await Assert.ThrowsAsync<AppException>(() => svc.RunNowAsync(missing));
        Assert.Equal(ErrorCodes.JobNotFound, ex5b.ErrorCode);

        // ⑥ 调度器拒绝触发 → job.run_rejected(fake 返回 false＝"未注册代码或上一轮未完成")
        var ex6 = await Assert.ThrowsAsync<AppException>(() => svc.RunNowAsync(probe.Id));
        Assert.Equal(ErrorCodes.JobRunRejected, ex6.ErrorCode);

        // 反向哨兵 A：触发结果为 true 时应正常返回（证明 ⑥ 的因由＝调度器拒绝，而非 RunNow 必抛）
        var okSvc = new SysJobService(repo, logRepo, new FakeScheduler([registered], runResult: true));
        await okSvc.RunNowAsync(probe.Id);

        // 反向哨兵 B：Update 走合法间隔应成功落库（证明 ② 的因由＝取值 ≤0；同码的 Update 入口也被真实覆盖过）
        await svc.UpdateAsync(new UpdateJobInput(probe.Id, Interval: 120));
        Assert.Equal(120, (await repo.GetFirstAsync(u => u.Id == probe.Id))!.Interval);

        // Update 入口的 interval_invalid（同码第二入口，此前从未被任何测试触达）
        var ex2b = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateJobInput(probe.Id, Interval: 0)));
        Assert.Equal(ErrorCodes.JobIntervalInvalid, ex2b.ErrorCode);
    }

    /// <summary>可控调度器：注册集与触发结果都由输入决定（生产调度器是后台托管件，夹具不拉起）。</summary>
    private sealed class FakeScheduler(string[] registered, bool runResult) : IJobScheduler
    {
        public Task<bool> RunNowAsync(string jobName) => Task.FromResult(runResult);

        public IReadOnlyCollection<string> GetRegisteredJobNames() => registered;

        public string InstanceId => "test-fake";

        public IReadOnlyCollection<string> GetScheduledJobNames() => Array.Empty<string>();
    }

    /// <summary>最小宿主(照 BatchSGErrCodeCoverageSerialTest)：真实 PG ＋ 不配 Redis（缓存内存兜底）。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                scope.Provider = new ServiceCollection().BuildServiceProvider();
                return scope;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Cache:Type"] = "Memory",
                    ["Yit:WorkerId"] = "1",
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR();
            services.AddAutoServices(typeof(SysJobService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", 5432, timeout.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
