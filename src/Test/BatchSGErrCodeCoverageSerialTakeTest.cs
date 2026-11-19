// 批 S-G 纵深期·错误码覆盖补漏小片（D 域·流水号**取号面** 3 码）——真库档（写 `sys_serial` 探针行）。
// 覆盖：serial.config_unavailable / serial.out_of_range / serial.slot_invalid。
// 有意不含 serial.advance_failed：其唯一触发点是"单语句 UPDATE 影响行数 ≤ 0"（持锁期间行被并发删除），
//   属**并发/故障注入面**——本仓无故障注入门，硬凑需要 mock 仓储而本服务用具体仓储类型 ⇒ 登记为已知缺口，不假测。
// 缓存口径（实读 `CacheSetup.cs:38-42`）：夹具未配 `ConnectionStrings:Redis` ⇒ AddCache **自动内存兜底**，
//   取号用的分布式锁退化为进程内锁 ⇒ 本片**完全不触碰宿主共用 Redis**（无需独立 db 纪律，零跨项目污染）。
// 前置实测（2026-10-26）：`sys_serial` 启用行数＝0 ⇒ 注册集虽只有 `BuiltInSerialTypeProvider.Test="test"` 一个值，
//   但库里无既有配置行可撞，本片对 `test` 类型是独占的；跑完 finally 物理删探针，恢复"空表"现状。
// 纪律：真实 PG；探针 finally 物理删；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Serial;
using AdminCore.Core.Services.Serial.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageSerialTakeTest : IAsyncLifetime
{
    private readonly List<long> _serialIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_serialIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        var ids = _serialIds.ToArray();
        await scope.Db.Deleteable<SysSerial>().In(ids).ExecuteCommandAsync();
    }

    [Fact]
    public void 插槽渲染_一条校验码_精确断言()
    {
        // 预览路径不查库、不消耗序列、不取锁 ⇒ 插槽码可脱离数据面单测
        using var scope = new ServiceCollection().AddLogging().BuildServiceProvider().CreateScope();
        // 三个依赖在本路径均不被触碰（PreviewAsync 只走插槽渲染）⇒ 直构传 null!；
        // 仓储参数本服务收的是具体类 SqlSugarRepository<T>（非接口），null! 由构造函数推断类型
        var svc = new SysSerialService(
            null!,
            null!,
            new AdminCore.Core.Security.UserManager(new FakeHttpContextAccessor()));

        // ① 未知插槽 → serial.slot_invalid（模板配错必须硬失败，禁止静默输出字面量 `{NOPE}` 当流水号）
        var ex = Assert.Throws<AppException>(() => svc.PreviewAsync(new PreviewSerialInput("{NOPE_SLOT}{SEQ}")));
        Assert.Equal(ErrorCodes.SerialSlotInvalid, ex.ErrorCode);

        // 反向哨兵：合法插槽 `{SEQ}` 正常渲染（证明 ① 的因由＝插槽名不认识，而非渲染器整体坏了）
        var preview = svc.PreviewAsync(new PreviewSerialInput("{SEQ}", Seq: 42, Max: 999));
        Assert.Contains("42", preview);
    }

    [Fact]
    public async Task 取号面_两条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysSerialService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysSerial>>();

        var registered = svc.GetTypeList();
        Assert.NotEmpty(registered);
        var type = registered[0].Value;

        // ① 注册类型在本租户无**启用**配置 → serial.config_unavailable(取号必须先有启用配置,404)
        var ex1 = await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync(type));
        Assert.Equal(ErrorCodes.SerialConfigUnavailable, ex1.ErrorCode);

        // ② 序号超上限 → serial.out_of_range(Max=2/Seq=1:第一次取号 Seq→2 合法,第二次 Seq→3 越界)
        var probe = new SysSerial
        {
            Type = type,
            ResetInterval = ResetIntervalEnum.Never,
            Formater = "{SEQ}",
            Min = 1,
            Max = 2,
            OrderNo = 100,
            Status = 0,
            Seq = 1,
            Expy = DateTime.Now,
            Remark = string.Empty
        };
        await repo.InsertAsync(probe);
        _serialIds.Add(probe.Id);

        var first = await svc.NextSeqNoAsync(type);
        Assert.Contains("2", first);

        var ex2 = await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync(type));
        Assert.Equal(ErrorCodes.SerialOutOfRange, ex2.ErrorCode);

        // 反向哨兵：①② 的因由分别是"没有启用配置"与"序号越界"——把探针停用后应回到 ①（而非第三种错误）
        await svc.SetStatusAsync(probe.Id, 1);
        var ex3 = await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync(type));
        Assert.Equal(ErrorCodes.SerialConfigUnavailable, ex3.ErrorCode);
    }

    /// <summary>最小宿主：真实 PG ＋ 不配 Redis（⇒ 缓存自动内存兜底，取号锁退化为进程内锁）。</summary>
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
            services.AddAutoServices(typeof(SysSerialService).Assembly);

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
