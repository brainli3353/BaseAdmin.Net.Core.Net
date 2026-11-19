// 批 S-G 纵深期·错误码覆盖补漏小片（D 域·日志详情 3 码 ＋ 站内消息校验 2 码）。
// 覆盖：log.op_not_found / log.diff_not_found / log.ex_not_found / message.content_required / message.receivers_required。
// 两档形状（有意分开）：
//   ① **message 两条＝零库形状**——`SendAllUserAsync`/`SendUserAsync` 的两条校验都在触碰 cache/Hub **之前**
//     （实读 `SysMessageService.cs:47`/`:77`/`:82`）⇒ 服务可 `default!` 直构，不建夹具、不连 PG/Redis。
//     有意**不做正向哨兵**：正向会向真 `OnlineUserHub` 广播／写在线表快照，属外部可见副作用，不为凑对称而做。
//   ② **log 三条＝真库只读**——三处抛点同构（`GetDetailAsync` 查不到行即抛，实读
//     `SysLogOpService.cs:67`／`SysLogDiffService.cs:59`／`SysLogExService.cs:68`），需 PG 才能拿到 null；
//     **只 SELECT 不写库** ⇒ 无共用表污染面（邻域清单记"只读免"）。
// 纪律：PG 不可达 Assert.Fail 禁跳过；日志详情正向哨兵用**条件哨兵**（表内无行则不断言，非跳过用例）。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Message;
using AdminCore.Core.Services.Message.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageLogMsgTest : IAsyncLifetime
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 站内消息_两条参数校验码_零库形状()
    {
        // 实读修正（本拍踩到）：构造函数带 `ArgumentNullException.ThrowIfNull` 守卫（`SysMessageService.cs:36`）
        //   ⇒ `default!` 直构不成立。改从**不含 PG 的轻量容器**解析＝内存缓存＋进程内 SignalR＋自动服务注册，
        //   仍然零 PG／零 Redis；两条校验在真正发消息之前返回，不会向任何 Hub 推送。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:Type"] = "Memory" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
        services.AddSingleton<IConfiguration>(config);
        services.AddCache(config);
        services.AddSignalR();
        services.AddAutoServices(typeof(SysMessageService).Assembly);
        using var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<SysMessageService>();

        // ① 内容为空 → message.content_required（广播/单播两个入口同码）
        var empty = new MessageInput(1, "t", " ", 1L, [1L]);
        var ex1a = await Assert.ThrowsAsync<AppException>(() => svc.SendAllUserAsync(empty));
        Assert.Equal(ErrorCodes.MessageContentRequired, ex1a.ErrorCode);

        var ex1b = await Assert.ThrowsAsync<AppException>(() => svc.SendUserAsync(empty));
        Assert.Equal(ErrorCodes.MessageContentRequired, ex1b.ErrorCode);

        // ② 接收人为空 → message.receivers_required（单播专有；内容合法 ⇒ 证明 ① 的因由＝内容而非"该方法必抛"）
        var noReceiver = new MessageInput(1, "t", "正文", 1L, []);
        var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.SendUserAsync(noReceiver));
        Assert.Equal(ErrorCodes.MessageReceiversRequired, ex2.ErrorCode);
    }

    [Fact]
    public async Task 日志详情_三条不存在码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var missing = 990_000_000_000_950L;

        var opEx = await Assert.ThrowsAsync<AppException>(
            () => sp.GetRequiredService<SysLogOpService>().GetDetailAsync(missing));
        Assert.Equal(ErrorCodes.LogOpNotFound, opEx.ErrorCode);

        var diffEx = await Assert.ThrowsAsync<AppException>(
            () => sp.GetRequiredService<SysLogDiffService>().GetDetailAsync(missing));
        Assert.Equal(ErrorCodes.LogDiffNotFound, diffEx.ErrorCode);

        var exEx = await Assert.ThrowsAsync<AppException>(
            () => sp.GetRequiredService<SysLogExService>().GetDetailAsync(missing));
        Assert.Equal(ErrorCodes.LogExNotFound, exEx.ErrorCode);

        // 条件反向哨兵：库里确有历史日志行时，详情必须能取到（证明上面三条的因由＝id 不存在，而非详情路径整体坏掉）。
        // 审计表按运行常态有行；万一空表则此断言不适用（不是跳过用例——三条主断言已实跑）
        var opRow = await sp.GetRequiredService<ISqlSugarRepository<SysLogOp>>().AsQueryable().FirstAsync();
        if (opRow is not null)
        {
            var detail = await sp.GetRequiredService<SysLogOpService>().GetDetailAsync(opRow.Id);
            Assert.Equal(opRow.Id, detail.Id);
        }
    }

    /// <summary>最小宿主：真实 PG ＋ 不配 Redis（本片只用日志查询路径）。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
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
            services.AddAutoServices(typeof(SysLogOpService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            var db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(db);
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
