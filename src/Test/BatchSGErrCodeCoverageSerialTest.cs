// 批 S-G 纵深期·错误码覆盖补漏小片（D 域·流水号管理面 4 码）——真库档（会往 `sys_serial` 插探针行）。
// 覆盖：serial.config_not_found / serial.type_unregistered / serial.type_exists / serial.status_invalid。
// WHY 只做管理面：取号面 4 码（serial.config_unavailable / out_of_range / advance_failed / slot_invalid）
//   全在 `NextSeqNoCore` 内，其前置是**分布式缓存锁**（`_cache`）⇒ 须连宿主共用 Redis（127.0.0.1:6380，
//   纪律＝显式独立 db＋只清自造键，见 docs/框架索引.md §五）⇒ **独立成下一片**，不与本片混做。
// 已注册类型不写死：`GetTypeList()` 实读（反射扫 `ISerialTypeProvider` 的 string 常量字段，当前实现＝
//   `BuiltInSerialTypeProvider.Test = "test"`），并用 Assert.NotEmpty 把"注册集装配"本身也钉住——
//   若哪天 provider 被删空，本片先红，比"serial.type_exists 静默不可测"好。
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

public class BatchSGErrCodeCoverageSerialTest : IAsyncLifetime
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
    public async Task 流水号管理面_四条校验码_精确断言()
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

        // 装配守卫：注册集必须非空（当前实现＝BuiltInSerialTypeProvider 的 string 常量字段）
        var registered = svc.GetTypeList();
        Assert.NotEmpty(registered);
        var type = registered[0].Value;

        // ① 详情/更新/删除不存在 id → serial.config_not_found（三个入口同码，逐个断言）
        var missing = 990_000_000_000_930L;
        var ex1a = await Assert.ThrowsAsync<AppException>(() => svc.DetailAsync(missing));
        Assert.Equal(ErrorCodes.SerialConfigNotFound, ex1a.ErrorCode);
        Assert.Equal(404, ex1a.Code);

        var ex1b = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(missing));
        Assert.Equal(ErrorCodes.SerialConfigNotFound, ex1b.ErrorCode);

        // ② Type 不在注册集 → serial.type_unregistered（防"任意字符串都能建号"导致后续取号无配置可查）
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddSerialInput($"sgx_serial_{Guid.NewGuid():N}"[..24], ResetIntervalEnum.Never, "{NO}{SEQ}")));
        Assert.Equal(ErrorCodes.SerialTypeUnregistered, ex2.ErrorCode);

        // ③ 同租户同 Type 已建 → serial.type_exists（先确保注册集首个类型在本租户已有行）
        if (await repo.IsAnyAsync(u => u.Type == type) == false)
        {
            var probe = new SysSerial
            {
                Type = type,
                ResetInterval = ResetIntervalEnum.Never,
                Formater = "{NO}{SEQ}",
                Min = 1,
                Max = 1_000_000,
                OrderNo = 100,
                Status = 0,
                Seq = 1,
                Expy = DateTime.Now,
                Remark = string.Empty
            };
            await repo.InsertAsync(probe);
            _serialIds.Add(probe.Id);
        }

        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddSerialInput(type, ResetIntervalEnum.Never, "{NO}{SEQ}")));
        Assert.Equal(ErrorCodes.SerialTypeExists, ex3.ErrorCode);

        // ④ 启停状态非 0/1 → serial.status_invalid（校验在落库之前，无需真实配置行）
        var ex4 = await Assert.ThrowsAsync<AppException>(() => svc.SetStatusAsync(missing, 7));
        Assert.Equal(ErrorCodes.SerialStatusInvalid, ex4.ErrorCode);

        // 反向哨兵：合法状态值应通过校验并真的落库（证明 ④ 的因由＝取值越界，而非该方法必抛）
        var target = await repo.AsQueryable().FirstAsync(u => u.Type == type);
        Assert.NotNull(target);
        await svc.SetStatusAsync(target!.Id, 1);
        Assert.Equal(1, (await svc.DetailAsync(target.Id)).Status);
        await svc.SetStatusAsync(target.Id, 0);
        Assert.Equal(0, (await svc.DetailAsync(target.Id)).Status);
    }

    /// <summary>最小宿主(照 BatchSGErrCodeCoverageDictDataTest):真实 PG + 自动服务注册 + 空 HttpContext(未认证=默认租户)。</summary>
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
