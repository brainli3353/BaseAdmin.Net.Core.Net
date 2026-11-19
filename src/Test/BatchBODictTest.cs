// 批 BO sysDict 数据字典逐页复审测试(真实 PG,与 BatchCTenantTest 同源样板;沙箱不可达时诚实降级 Assert.Fail):
// ① 批 BO D1:字典类型 Code 判重=全局跨租户(复现 PermissionMiddleware 租户过滤装配——
//    租户用户视角下他租户同 Code 类型 → 拒绝;不同 Code → 放行;软删后同 Code 重建 → 放行);
// ② 跨租户读回归:种子 account_status(默认租户)在探针租户过滤视角下仍可读(ClearFilter 双 Enable);
// ③ 类型/值生命周期:类型建 → 值建 → 按 Code 读(缓存回填)→ 直改库(缓存旧值)→ 服务更新(变更即失效)→
//    值停用(list 空)→ 启用回(恢复)→ 类型级联删(值物理清零)→ 软删后同 Code 重建放行。
// 探针纪律(批 BM D2 机制):IAsyncLifetime 探针登记兜底——用例失败也不漏行。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Dict;
using AdminCore.Core.Services.Dict.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

public class BatchBODictTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // 探针登记:用例失败后 DisposeAsync 仍执行(成功路径内联清理保留,幂等)
    private readonly List<long> _probeTypeIds = new();
    private readonly List<long> _probeDataIds = new();

    public BatchBODictTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_probeTypeIds.Count == 0 && _probeDataIds.Count == 0)
        {
            return;
        }

        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            return;
        }

        using var diScope = dbScope.Provider.CreateScope();
        await CleanupAsync(diScope.ServiceProvider, _probeTypeIds, _probeDataIds);
    }

    // 开发库连接串(与 DataScopeTest/BatchC 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 探针租户段(单测 009 段;BM e2e 用 010 段)——仅作过滤值,无需 sys_tenant 行(字典域无租户存在性校验)
    private const long ProbeTenantId = 99_000_000_009_001L;

    // ─────────────────────────────────────────────────────────────
    // ① 批 BO D1:类型 Code 判重=全局跨租户
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 类型_Code判重_全局跨租户()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var typeService = sp.GetRequiredService<SysDictTypeService>();
        var db = sp.GetRequiredService<ISqlSugarClient>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var code = $"bo.{p}";

        // 默认租户(超管视角,无过滤)建类型
        var idA = await typeService.AddAsync(new AddDictTypeInput($"bo类型{p}", code, false, "A"));
        _probeTypeIds.Add(idA);
        Assert.True(idA > 0);

        // 复现「租户用户」请求视角(PermissionMiddleware 同源装配:先清后设;finally 恢复)
        try
        {
            db.QueryFilter.Clear<ITenantIdFilter>();
            db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == ProbeTenantId);

            // D1:他租户已有同 Code → 租户用户视角再建 → 拒绝(修复前:租户过滤后判重看不到他租户行 → 放行成双行)
            var ex = await Assert.ThrowsAsync<AppException>(() =>
                typeService.AddAsync(new AddDictTypeInput($"bo类型{p}B", code, false, "B")));
            Assert.Equal(ErrorCodes.DictTypeCodeExists, ex.ErrorCode);
            Assert.Equal(code, ex.Args?["code"]);

            // 不同 Code → 放行(租户用户可建自己的类型,Code 命名空间全局保留)
            var codeB = $"{code}.b";
            var idB = await typeService.AddAsync(new AddDictTypeInput($"bo类型{p}B", codeB, false, "B"));
            _probeTypeIds.Add(idB);
            Assert.True(idB > 0);

            // 同视角下再建同 CodeB → 拒绝(本租户内判重仍生效)
            await Assert.ThrowsAsync<AppException>(() =>
                typeService.AddAsync(new AddDictTypeInput($"bo类型{p}C", codeB, false, "C")));
        }
        finally
        {
            db.QueryFilter.Clear<ITenantIdFilter>();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ② 跨租户读回归:种子字典在探针租户视角可读
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 跨租户读_种子字典_探针租户视角可读()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var dataService = sp.GetRequiredService<SysDictDataService>();
        var db = sp.GetRequiredService<ISqlSugarClient>();

        try
        {
            db.QueryFilter.Clear<ITenantIdFilter>();
            db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == ProbeTenantId);

            // 种子 account_status 落默认租户;探针租户视角读(服务内 ClearFilter)→ 全量 5 值
            var list = await dataService.GetDataListByIdOrCodeAsync(null, "account_status");
            Assert.Equal(5, list.Count);
            Assert.Contains(list, d => d.Label == "待验号");
            // 只取双 Enable:种子全启用 → 5 值全在
            Assert.All(list, d => Assert.True(d.IsEnable));
        }
        finally
        {
            db.QueryFilter.Clear<ITenantIdFilter>();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ③ 类型/值生命周期 + 缓存回填/失效 + 级联删 + 软删后重建
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 类型值_生命周期_缓存失效级联删()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var typeService = sp.GetRequiredService<SysDictTypeService>();
        var dataService = sp.GetRequiredService<SysDictDataService>();
        var dataRepo = sp.GetRequiredService<SqlSugarRepository<SysDictData>>();
        var db = sp.GetRequiredService<ISqlSugarClient>();
        var p = Guid.NewGuid().ToString("N")[..8];
        var code = $"bo.{p}.full";

        // 类型建
        var typeId = await typeService.AddAsync(new AddDictTypeInput($"bo生命周期{p}", code, true, "生命周期"));
        _probeTypeIds.Add(typeId);

        // 值建 + 同类型 Code 判重
        var dataId = await dataService.AddAsync(new AddDictDataInput(typeId, "1", "1", "甲", 1, null, null, 0, false, null, "备注"));
        _probeDataIds.Add(dataId);
        await Assert.ThrowsAsync<AppException>(() =>
            dataService.AddAsync(new AddDictDataInput(typeId, "1", "9", "乙", 1, null, null, 1, false, null, null)));

        // 按 Code 读(缓存回填)
        var list1 = await dataService.GetDataListByIdOrCodeAsync(null, code);
        Assert.Single(list1);
        Assert.Equal("甲", list1[0].Label);

        // 直改库绕过服务 → 读仍返旧值(缓存生效的直证)
        var row = await dataRepo.GetFirstAsync(d => d.Id == dataId);
        row!.Label = "直改";
        await dataRepo.UpdateAsync(row);
        var list2 = await dataService.GetDataListByIdOrCodeAsync(null, code);
        Assert.Equal("甲", list2[0].Label);

        // 服务更新(变更即失效)→ 读即新值
        await dataService.UpdateAsync(new UpdateDictDataInput(dataId, "1", "1", "乙", 1, null, null, 0, false, null, "备注2"));
        var list3 = await dataService.GetDataListByIdOrCodeAsync(null, code);
        Assert.Equal("乙", list3[0].Label);
        row = await dataRepo.GetFirstAsync(d => d.Id == dataId);
        Assert.Equal("备注2", row!.Remark);

        // 值停用 → list 不再含(双 Enable 的值侧);启用回 → 恢复
        await dataService.UpdateStatusAsync(new UpdateDictDataStatusInput(dataId, false));
        Assert.Empty(await dataService.GetDataListByIdOrCodeAsync(null, code));
        await dataService.UpdateStatusAsync(new UpdateDictDataStatusInput(dataId, true));
        Assert.Single(await dataService.GetDataListByIdOrCodeAsync(null, code));

        // 类型级联删:值物理清零(裸 SQL 验物理面,软删过滤器会挡)+ 类型软删不可见
        await typeService.DeleteAsync(typeId);
        // SugarParameter 显式参数:Ado 匿名对象重载不做 {0} 占位替换(实机 42601,同 SysUserSeed 口径)
        Assert.Equal(0, db.Ado.GetInt(
            "SELECT count(*) FROM sys_dict_data WHERE \"DictTypeId\" = @tid",
            new SugarParameter("@tid", typeId)));
        var page = await typeService.PageAsync(new PageDictTypeInput(1, 50));
        Assert.DoesNotContain(page.List, t => t.Id == typeId);

        // 软删后同 Code 重建放行(软删过滤排除;D1 全局判重同口径)
        var typeId2 = await typeService.AddAsync(new AddDictTypeInput($"bo生命周期{p}2", code, true, "重建"));
        _probeTypeIds.Add(typeId2);
        Assert.True(typeId2 > 0 && typeId2 != typeId);

        // 成功路径清理(幂等;失败路径由 DisposeAsync 兜底)
        await CleanupAsync(sp, new List<long> { typeId2 }, new List<long>());
        _probeTypeIds.Remove(typeId2);
    }

    // ─────────────────────────────────────────────────────────────
    // 清理与 DB 样板(与 BatchCTenantTest 同源)
    // ─────────────────────────────────────────────────────────────
    private static async Task CleanupAsync(IServiceProvider sp, IEnumerable<long> typeIds, IEnumerable<long> dataIds)
    {
        // 裸 SQL 物理清:仓储 DeletePhysicalByAsync 走 Deleteable,受全局软删过滤器
        // (IDeletedFilter → IsDelete=false)约束——被服务软删过的探针行对 Deleteable 不可见,清不掉
        // (本批实测:失败轮次 DisposeAsync 对软删行 no-op,累积残留;同 BatchBH「裸 SQL 物理删」教训)
        var db = sp.GetRequiredService<ISqlSugarClient>();

        foreach (var id in dataIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_dict_data WHERE id = @id", new SugarParameter("@id", id));
        }

        foreach (var id in typeIds.Where(id => id > 0))
        {
            await db.Ado.ExecuteCommandAsync("DELETE FROM sys_dict_type WHERE id = @id", new SugarParameter("@id", id));
        }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// 框架表增量建(InitTables 扫 Core 程序集,幂等)。PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
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
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要;加解密对称,与生产值无关
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysDictTypeService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db); // 扫 Core 程序集 → 建 sys_dict_* 等缺表(增量幂等)
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 DataScopeTest 口径)。</summary>
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
