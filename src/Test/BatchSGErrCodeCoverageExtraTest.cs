// 批 S-G 纵深期·错误码覆盖补漏(父层自写小片):覆盖 7 条"服务校验分支明确、无需外部故障注入"的 B 域错误码——
// config.code_required / config.code_exists / config.not_found / dict.type_not_found / dict.type_name_required /
// menu.parent_self / menu.parent_not_found。
// WHY 单独一个文件:大臂(30+ 码/文件)多次中途失败,改为**小片可验证增量**——每片 5–10 条、独立文件、独立探针清理,
// 覆盖读数由 node scripts/error-code-coverage.mjs 统一统计(不在此硬编码总数)。
// 纪律:真实 PG;探针 finally 物理删;PG 不可达 Assert.Fail 禁跳过(照 BatchSGConsistencyTest 口径)。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.Dict;
using AdminCore.Core.Services.Dict.Dto;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Services.Menu.Dto;
using AdminCore.Core.SqlSugar;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageExtraTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<long> _configIds = new();
    private readonly List<long> _dictTypeIds = new();
    private readonly List<long> _menuIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public BatchSGErrCodeCoverageExtraTest(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_configIds.Count == 0 && _dictTypeIds.Count == 0 && _menuIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        await PhysicalDeleteAsync<SysConfig>(scope.Db, _configIds);
        await PhysicalDeleteAsync<SysDictType>(scope.Db, _dictTypeIds);
        await PhysicalDeleteAsync<SysMenu>(scope.Db, _menuIds);
    }

    [Fact]
    public async Task 配置域_三条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysConfigService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysConfig>>();
        var code = $"sgx_cfg_{Guid.NewGuid():N}"[..24];

        // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):本用例断的是配置域**其它**校验码
        // (code_required/code_exists/not_found),必须在超管身份下穿过闸才能继续命中原分支;
        // 不得把期望改成 common.forbidden(那等于丢掉这三条错误码覆盖)。整方法挂超管,不影响其它域用例。
        scope.Accessor.SetSuperAdmin();

        // ① code 空白 → config.code_required
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddConfigInput("sgx", " ", "v", null)));
        Assert.Equal(ErrorCodes.ConfigCodeRequired, ex1.ErrorCode);

        // ② 重复 code → config.code_exists(先自建探针行,再以同 code 新增)
        var probe = new SysConfig { Name = "sgx_cfg", Code = code, Value = "1", GroupCode = string.Empty };
        await repo.InsertAsync(probe);
        _configIds.Add(probe.Id);
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddConfigInput("sgx2", code, "2", null)));
        Assert.Equal(ErrorCodes.ConfigCodeExists, ex2.ErrorCode);

        // ③ 不存在 id → config.not_found
        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateConfigInput(990_000_000_000_901L, "n")));
        Assert.Equal(ErrorCodes.ConfigNotFound, ex3.ErrorCode);
    }

    [Fact]
    public async Task 字典域_两条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysDictTypeService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysDictType>>();
        var code = $"sgx_dict_{Guid.NewGuid():N}"[..24];

        // ① 不存在 id → dict.type_not_found
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateDictTypeInput(990_000_000_000_902L, "n")));
        Assert.Equal(ErrorCodes.DictTypeNotFound, ex1.ErrorCode);

        // ② 名称空白 → dict.type_name_required(探针类型行)
        var type = new SysDictType { Name = "sgx", Code = code, IsEnum = false, Remark = string.Empty };
        await repo.InsertAsync(type);
        _dictTypeIds.Add(type.Id);
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateDictTypeInput(type.Id, " ")));
        Assert.Equal(ErrorCodes.DictTypeNameRequired, ex2.ErrorCode);
    }

    [Fact]
    public async Task 菜单域_两条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var svc = sp.GetRequiredService<SysMenuService>();
        var repo = sp.GetRequiredService<ISqlSugarRepository<SysMenu>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // 探针菜单(目录,type=1)
        var menu = new SysMenu { Pid = 0, Name = $"sgx_menu_{suffix}", Title = "sgx", Type = 1, OrderNo = 0 };
        await repo.InsertAsync(menu);
        _menuIds.Add(menu.Id);

        // ① 父设为自己 → menu.parent_self
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.UpdateAsync(new UpdateMenuInput(menu.Id, menu.Id, menu.Name, menu.Title, 1, null, 0, null)));
        Assert.Equal(ErrorCodes.MenuParentSelf, ex1.ErrorCode);

        // ② 父不存在 → menu.parent_not_found
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddMenuInput(990_000_000_000_903L, $"sgx_child_{suffix}", "sgx child", 1, null, 0, null)));
        Assert.Equal(ErrorCodes.MenuParentNotFound, ex2.ErrorCode);
    }

    private static async Task PhysicalDeleteAsync<T>(ISqlSugarClient db, List<long> ids)
        where T : class, new()
    {
        if (ids.Count == 0)
        {
            return;
        }

        await db.Deleteable<T>().In(ids.ToArray()).ExecuteCommandAsync();
    }

    /// <summary>最小宿主(照 BatchSGConsistencyTest):真实 PG + 自动服务注册。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public bool PgReachable { get; private set; }

        // ㉖ 唯一身份构造点:默认 null(未认证);配置域错误码用例整方法挂超管以穿过写闸
        public TestActorAccessor Accessor { get; } = new();

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
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR();
            services.AddAutoServices(typeof(SysMenuService).Assembly);
            // ㉖ 放在 AddAutoServices 之后注册:单服务解析取最后一次注册,确保 accessor 落到本夹具实例
            services.AddSingleton<IHttpContextAccessor>(scope.Accessor);

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
}
