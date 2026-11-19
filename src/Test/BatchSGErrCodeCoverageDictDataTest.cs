// 批 S-G 纵深期·错误码覆盖补漏小片（C 域·字典类型/字典数据，父层自写）：覆盖 6 条校验分支——
// dict.type_name_code_required / dict.type_id_code_required / dict.data_fields_required /
// dict.type_code_exists_under / dict.data_not_found / dict.data_code_exists。
// WHY 小片：同域同夹具一批做完（一次探针建表清理），每片 5–10 码；覆盖读数唯一家＝`node scripts/error-code-coverage.mjs`。
// 与 BatchSGErrCodeCoverageExtraTest 的分工：那片已钉 dict.type_not_found / dict.type_name_required，
//   本片钉字典**数据**面与"名称+编码双必填"面，不重复计数。
// 纪律：真实 PG；探针 finally 物理删（DeletePhysicalByAsync 绕软删过滤器，防同 Code 判重被历史残留干扰）；PG 不可达 Assert.Fail 禁跳过。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Dict;
using AdminCore.Core.Services.Dict.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageDictDataTest : IAsyncLifetime
{
    private readonly List<long> _typeIds = new();
    private readonly List<long> _dataIds = new();

    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_typeIds.Count == 0 && _dataIds.Count == 0)
        {
            return;
        }

        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            return;
        }

        var db = scope.Db;
        if (_dataIds.Count > 0)
        {
            var ids = _dataIds.ToArray();
            await db.Deleteable<SysDictData>().In(ids).ExecuteCommandAsync();
        }

        if (_typeIds.Count > 0)
        {
            var ids = _typeIds.ToArray();
            await db.Deleteable<SysDictType>().In(ids).ExecuteCommandAsync();
        }
    }

    [Fact]
    public async Task 字典类型域_两条必填码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var svc = diScope.ServiceProvider.GetRequiredService<SysDictTypeService>();

        // ① 名称空白 → dict.type_name_code_required(名称与编码**双必填**,与 dict.type_name_required 的"仅名称"码并存——D5.3 已记近似双码)
        var ex1 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddDictTypeInput(" ", $"sgd_t_{Guid.NewGuid():N}"[..16])));
        Assert.Equal(ErrorCodes.DictTypeNameCodeRequired, ex1.ErrorCode);

        // ② 编码空白 → 同码(同一守卫的两个入口,分别断言防"只测一半")
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            svc.AddAsync(new AddDictTypeInput($"sgd_t_{Guid.NewGuid():N}"[..16], " ")));
        Assert.Equal(ErrorCodes.DictTypeNameCodeRequired, ex2.ErrorCode);
    }

    [Fact]
    public async Task 字典数据域_四条校验码_精确断言()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = scope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var dataSvc = sp.GetRequiredService<SysDictDataService>();
        var typeRepo = sp.GetRequiredService<ISqlSugarRepository<SysDictType>>();
        var dataRepo = sp.GetRequiredService<ISqlSugarRepository<SysDictData>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var type = new SysDictType { Name = $"sgd_type_{suffix}", Code = $"sgd_t_{suffix}", IsEnum = false, Remark = string.Empty };
        await typeRepo.InsertAsync(type);
        _typeIds.Add(type.Id);

        // ① 类型 Id 与 Code 同时为空 → dict.type_id_code_required(取数入口的空值守卫)
        var ex1 = await Assert.ThrowsAsync<AppException>(() => dataSvc.GetDataListByIdOrCodeAsync(null, null));
        Assert.Equal(ErrorCodes.DictTypeIdCodeRequired, ex1.ErrorCode);

        // ② 数据三字段任一空白 → dict.data_fields_required(类型存在才走到这里)
        var ex2 = await Assert.ThrowsAsync<AppException>(() =>
            dataSvc.AddAsync(new AddDictDataInput(type.Id, " ", "v", "l")));
        Assert.Equal(ErrorCodes.DictDataFieldsRequired, ex2.ErrorCode);

        // ③ 同类型下 Code 重复 → dict.type_code_exists_under(常量名以 type 开头,实际语义＝"该类型下 code 已存在"——D5.3 近似双码候选)
        var first = new SysDictData { DictTypeId = type.Id, Code = $"c1_{suffix}", Value = "1", Label = "一" };
        await dataRepo.InsertAsync(first);
        _dataIds.Add(first.Id);
        var ex3 = await Assert.ThrowsAsync<AppException>(() =>
            dataSvc.AddAsync(new AddDictDataInput(type.Id, $"c1_{suffix}", "2", "二")));
        Assert.Equal(ErrorCodes.DictTypeCodeExistsUnder, ex3.ErrorCode);

        // ④ 更新不存在 id → dict.data_not_found
        var ex4 = await Assert.ThrowsAsync<AppException>(() =>
            dataSvc.UpdateAsync(new UpdateDictDataInput(990_000_000_000_920L, "c", "v", "l")));
        Assert.Equal(ErrorCodes.DictDataNotFound, ex4.ErrorCode);

        // ⑤ 改成同类型他行的 Code → dict.data_code_exists(排除自身仍撞他行)
        var second = new SysDictData { DictTypeId = type.Id, Code = $"c2_{suffix}", Value = "9", Label = "九" };
        await dataRepo.InsertAsync(second);
        _dataIds.Add(second.Id);
        var ex5 = await Assert.ThrowsAsync<AppException>(() =>
            dataSvc.UpdateAsync(new UpdateDictDataInput(second.Id, $"c1_{suffix}", "9", "九")));
        Assert.Equal(ErrorCodes.DictDataCodeExists, ex5.ErrorCode);

        // 反向哨兵:保持自身 Code 不变应更新成功(证明 ⑤ 的因由＝与**他行**撞码,排除自身口径生效)
        await dataSvc.UpdateAsync(new UpdateDictDataInput(second.Id, $"c2_{suffix}", "99", "九改"));
    }

    /// <summary>最小宿主(照 BatchSGErrCodeCoverageExtraTest):真实 PG + 自动服务注册 + 空 HttpContext(未认证)。</summary>
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
            services.AddAutoServices(typeof(SysDictDataService).Assembly);

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
