using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AB 多租户库级隔离(C1–C5)测试:真实 PG(主库 huluyeye_admin + 已存在的租户库 huluyeye_admin_test),
/// 沙箱 PG 不可达时诚实降级(Assert.Fail,禁止冒充通过)。
/// 判定对象 = 数据物理落点 + 连接注册表状态 + fail-loud 语义,逐条对应契约 C1/C2/C3/C4/C5。
/// </summary>
/// <remarks>
/// WHY 用独立验证客户端(直连租户库)取证:"落对库"必须由**不经框架注册表**的连接独立确认,
/// 否则用被框架路由的客户端去查,等于拿被测机制证明被测机制(同义反复)。
/// WHY 每用例自建租户行 + finally 物理清理:租户行/租户库表数据在用例间共享同一套真实库,
/// 不清理会串扰后续断言行数;默认租户(1300000000001)本类只读不改(绝不物理删)。
/// WHY initDb 用例先显式登记测试程序集:InitDbAsync 内部 InitTenantTables 取"程序集登记簿"
/// (Program.cs 启动期登记过的那批),而 Core/Business 当前**没有任何 [Tenant] 实体**
/// → 不登记则 tableCount 恒 0(生产现状,见报告)。登记走公开的 InitTenantTables(在租户库建表),
/// 不为主库留下探针表。
/// </remarks>
public class BatchABTenantDbTest
{
    private readonly ITestOutputHelper _output;

    public BatchABTenantDbTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchCTenantTest/DataScopeTest 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string MainConnectionString =
        TestPg.ConnectionString;

    // 租户库连接串(环境事实:同实例同用户 owner 的已存在库,可直接建表)
    private const string TenantConnectionString =
        TestPg.ConnectionStringTestDb;

    private const string TenantDbName = "huluyeye_admin_test";
    private const string ProbeTable = "ab_tenant_probe";
    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchCTenantTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // 1. C1:无租户上下文 / 默认租户上下文 → 选库落主库
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void C1_无上下文与默认租户_选库落主库()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var db = dbScope.Db;
        var config = sp.GetRequiredService<IConfiguration>();

        // 判定点①:无上下文(后台任务/未认证/启动扫描) → 主库。
        // WHY 经仓储构造判定而非 SqlSugarSetup.SelectConfigIdFor:后者是 internal 且 Core 无 InternalsVisibleTo
        // (grep 实证),测试程序集不可访问;仓储构造走的就是同一选库单点,Context 即选中的逐连接 scope。
        Assert.Null(TenantScope.Current);
        var noContextRepo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
        Assert.Equal(SqlSugarConst.MainConfigId, noContextRepo.Context.CurrentConnectionConfig.ConfigId as string);

        // 判定点②:默认租户上下文 → 主库(默认租户恒主库,不存在独立库)
        using (TenantScope.Begin(SqlSugarConst.DefaultTenantId))
        {
            var defaultTenantRepo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
            Assert.Equal(SqlSugarConst.MainConfigId, defaultTenantRepo.Context.CurrentConnectionConfig.ConfigId as string);
        }

        // 上下文退出即恢复(防异步上下文泄漏到后续用例)
        Assert.Null(TenantScope.Current);
        _output.WriteLine($"C1 判定:无上下文/默认租户 均落 {SqlSugarConst.MainConfigId}");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. C1:租户上下文但连接未注册 → 选库 fail-loud(绝不静默回落主库)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C1_租户上下文但连接未注册_选库fail_loud()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var db = dbScope.Db;
        var config = sp.GetRequiredService<IConfiguration>();

        var tenantId = await InsertProbeTenantAsync(sp, "noreg", connection: null);
        try
        {
            using (TenantScope.Begin(tenantId))
            {
                // 未注册即装配/配置缺陷:静默回落主库会让租户数据落错库且无感知 → 必须抛
                var ex = Assert.Throws<InvalidOperationException>(() =>
                {
                    var repo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
                    return repo;
                });
                Assert.Contains("未注册", ex.Message);
                _output.WriteLine($"C1 fail-loud 文案:{ex.Message}");
            }
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. C5:租户未配置连接串 → 请求路径口径回落主库(不抛、不注册)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C5_租户未配置连接串_请求路径回落主库()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;

        var tenantId = await InsertProbeTenantAsync(sp, "nocfg", connection: null);
        try
        {
            // 判定点①:required 缺省 false(请求路径口径)——未配置连接串 = 该租户不分库(opt-in 未开启)→ 主库,且不抛
            var configId = await TenantDbRegistry.EnsureConnectionAsync(tenantId);
            Assert.Equal(SqlSugarConst.MainConfigId, configId);

            // 判定点②:未发生任何连接注册(注册表零副作用)
            Assert.False(TenantDbRegistry.IsRegistered(tenantId));

            // 契约外观察(只留痕,不写成断言——防把待裁决语义固化成测试契约):本用例的事实是
            // "Ensure 回落 main",但仓储侧选库单点 SelectConfigIdFor 对 [Tenant]+非默认租户只认
            // "ConfigId=租户 Id 已注册",并不消费 Ensure 的返回值 → 同一租户在仓储路径走第 2 条用例的
            // fail-loud 分支。两处口径的落点差异见报告"生产代码疑问"一节。
            _output.WriteLine("C5 判定:EnsureConnectionAsync 回落 main 且未注册(仓储路径口径差异见报告)");
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. C2+C3:加密连接串 → 解密注册 → 跨库读写(独立连接取证)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C2C3_加密连接串_解密注册并跨库读写()
    {
        using var dbScope = new DbScope(enableConnEncrypt: true); // 加密开关开启的容器
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var db = dbScope.Db;
        var config = sp.GetRequiredService<IConfiguration>();

        // 用容器解析的 CryptogramHelper 加密(与机制解密同一密钥材料:同一容器的 Cryptogram:Key)
        var cryptogram = sp.GetRequiredService<CryptogramHelper>();
        var cipher = cryptogram.Encrypt(TenantConnectionString);
        Assert.NotEqual(TenantConnectionString, cipher); // 确属密文

        var tenantId = await InsertProbeTenantAsync(sp, "crypt", cipher);
        try
        {
            // ① 解密 + 注册:required=true 下返回租户 Id 字符串(ConfigId 约定)
            var configId = await TenantDbRegistry.EnsureConnectionAsync(tenantId, required: true);
            Assert.Equal(tenantId.ToString(), configId);
            Assert.True(TenantDbRegistry.IsRegistered(tenantId));

            // ② 租户库建表(测试程序集显式传入:探针实体在该程序集)
            var tableCount = db.InitTenantTables(tenantId, typeof(AbTenantProbeEntity).Assembly);
            Assert.True(tableCount >= 1, $"InitTenantTables 应至少处理探针表,实际 {tableCount}");

            // ③ 租户上下文下写入:仓储必须绑到租户库连接(ConfigId=租户 Id)
            long probeId;
            var tag = $"ab-{Guid.NewGuid().ToString("N")[..8]}";
            using (TenantScope.Begin(tenantId))
            {
                var repo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
                Assert.Equal(tenantId.ToString(), repo.Context.CurrentConnectionConfig.ConfigId as string);

                var entity = new AbTenantProbeEntity { Name = tag };
                await repo.InsertAsync(entity);
                probeId = entity.Id;
                Assert.True(probeId > 0, "探针行主键应由 AOP 自动填雪花");
            }

            // ④ 独立取证:另起一个直连 huluyeye_admin_test 的客户端(不经框架注册表)确认该行真的落在租户库
            using var verify = CreateProbeClient(TenantConnectionString);
            var tenantHit = await verify.Queryable<AbTenantProbeEntity>().Where(e => e.Id == probeId).CountAsync();
            Assert.Equal(1, tenantHit);

            // ⑤ 反向取证:主库同名表若存在(其他测试扫测试程序集建过)→ 必须不含该行;不存在 → 主库必然无此行
            if (await TableExistsAsync(db, ProbeTable))
            {
                // 主键列名为小写 id(EntityBaseId 声明口径:主键小写、其余 PascalCase)→ 按声明原样引用,不能写 "Id"
                var mainHit = await db.Ado.SqlQueryAsync<int>(
                    $"SELECT count(*) FROM \"{ProbeTable}\" WHERE id = @id",
                    new SugarParameter("@id", probeId));
                Assert.Equal(0, mainHit[0]);
                _output.WriteLine($"C2C3 判定:租户库命中 1 行,主库同名表命中 {mainHit[0]} 行(主库表存在)");
            }
            else
            {
                _output.WriteLine("C2C3 判定:租户库命中 1 行;主库无 ab_tenant_probe 表 → 主库必然无此行");
            }
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. C2:密文非法 → 解密失败 fail-loud
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C2_密文非法_解密失败fail_loud()
    {
        using var dbScope = new DbScope(enableConnEncrypt: true);
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;

        var tenantId = await InsertProbeTenantAsync(sp, "badcipher", "not-base64!!");
        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => TenantDbRegistry.EnsureConnectionAsync(tenantId, required: true));
            Assert.Equal(ErrorCodes.TenantDbDecryptFailed, ex.ErrorCode); // 批 AR-5 口径
            _output.WriteLine($"C2 解密失败文案:{ex.Message}");

            // fail-loud 后不得留下半注册状态(注册表零残留,否则后续解析会拿到空串连接)
            Assert.False(TenantDbRegistry.IsRegistered(tenantId));
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6. C5:未配置连接串 + required=true → 业务异常(初始化场景必须真具备独立库)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C5_未配置连接串_required_抛业务异常()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;

        var tenantId = await InsertProbeTenantAsync(sp, "reqcfg", connection: null);
        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(
                () => TenantDbRegistry.EnsureConnectionAsync(tenantId, required: true));
            Assert.Equal(ErrorCodes.TenantDbConnectionMissing, ex.ErrorCode);
            _output.WriteLine($"C5 required 文案:{ex.Message}");
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 7. C3:并发 ensure → 只注册一次(全部成功、无异常、注册表有且仅有一条该租户项)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C3_并发ensure_只注册一次()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;

        var tenantId = await InsertProbeTenantAsync(sp, "conc", TenantConnectionString);
        try
        {
            // 闸内双检:并发下不得出现"多次建连接"或"半成品注册"(SemaphoreSlim + 双检,见 TenantDbRegistry)
            var results = await Task.WhenAll(
                Enumerable.Range(0, 10).Select(_ => TenantDbRegistry.EnsureConnectionAsync(tenantId)));

            Assert.Equal(10, results.Length);
            Assert.All(results, r => Assert.Equal(tenantId.ToString(), r));
            Assert.True(TenantDbRegistry.IsRegistered(tenantId));

            // 注册后再次解析仍返回同一 ConfigId(幂等,不重建)
            Assert.Equal(tenantId.ToString(), await TenantDbRegistry.EnsureConnectionAsync(tenantId));
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 8. C4:initDb 幂等(重跑不重复、不因表已存在失败)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C4_initDb幂等_重跑不重复()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var db = dbScope.Db;
        var tenantService = sp.GetRequiredService<SysTenantService>();

        var tenantId = await InsertProbeTenantAsync(sp, "initdb", TenantConnectionString);
        try
        {
            // 前置:登记测试程序集(见类注释 WHY)——建表走租户库,不在主库留探针表
            Assert.Equal(tenantId.ToString(), await TenantDbRegistry.EnsureConnectionAsync(tenantId, required: true));
            Assert.True(db.InitTenantTables(tenantId, typeof(AbTenantProbeEntity).Assembly) >= 1);

            var first = await tenantService.InitDbAsync(tenantId);
            var second = await tenantService.InitDbAsync(tenantId);

            Assert.Equal(tenantId, first.TenantId);
            Assert.Equal(tenantId.ToString(), first.ConfigId);
            Assert.True(first.TableCount > 0, "租户库应至少处理 1 张 [Tenant] 表");
            // 幂等判定:第二次不因表已存在失败,且处理面一致
            Assert.Equal(first.TableCount, second.TableCount);
            Assert.Equal(first.SeedCount, second.SeedCount);
            _output.WriteLine($"C4 initDb:tableCount={first.TableCount}/{second.TableCount} seedCount={first.SeedCount} database={first.Database}");
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 9. C2:连接串变更 → 变更即失效 → 再解析按新串重注册
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C2_连接串变更_失效即重注册()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantService = sp.GetRequiredService<SysTenantService>();

        var tenantId = await InsertProbeTenantAsync(sp, "chg", TenantConnectionString);
        try
        {
            Assert.Equal(tenantId.ToString(), await TenantDbRegistry.EnsureConnectionAsync(tenantId));
            Assert.True(TenantDbRegistry.IsRegistered(tenantId));

            // 同库不同串(追加无害参数):验证"变更即失效"而非连通性变化
            var newConnection = TenantConnectionString + ";ApplicationName=dsh_ab_probe";
            await tenantService.UpdateAsync(new UpdateTenantInput(tenantId, null, null, null, null, null, newConnection));

            Assert.False(TenantDbRegistry.IsRegistered(tenantId)); // 变更即失效,不靠重启

            // 再解析 → 按新串重新注册成功
            Assert.Equal(tenantId.ToString(), await TenantDbRegistry.EnsureConnectionAsync(tenantId));
            Assert.True(TenantDbRegistry.IsRegistered(tenantId));
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 10. C4:默认租户 initDb → 拒绝(默认租户恒主库,不存在独立租户库)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C4_默认租户_initDb拒绝()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();
        var tenantService = sp.GetRequiredService<SysTenantService>();

        // 默认租户只读不改:仅在其缺失时补齐(测试环境不跑种子),绝不修改/删除既有行;故本用例无清理动作
        await EnsureDefaultTenantAsync(tenantRepo);

        var ex = await Assert.ThrowsAsync<AppException>(() => tenantService.InitDbAsync(SqlSugarConst.DefaultTenantId));
        Assert.Equal(ErrorCodes.TenantDefaultMainDb, ex.ErrorCode); // 批 AR-3 口径:断言稳定错误码(文案由资源层渲染)
        _output.WriteLine($"C4 默认租户错误码:{ex.ErrorCode}");

        // 默认租户恒主库:ConfigId 映射为 main(不产生任何独立库注册项)
        Assert.Equal(SqlSugarConst.MainConfigId, TenantDbRegistry.ConfigIdForTenant(SqlSugarConst.DefaultTenantId));
    }

    // ─────────────────────────────────────────────────────────────
    // 11. 修订用例(批 AB 自抓缺陷②):未分库租户经请求路径 Ensure 后,选库必须落主库而非 fail-loud
    //     背景:选库在仓储构造(同步静态,无法查库),而"未配置连接串=落主库"的判定在中间件(异步查库);
    //     若中间件判定结果不被选库单点消费,存量未分库租户一旦出现 [Tenant] 实体,仓储构造即抛
    //     "租户库连接未注册" → 所有非默认租户请求 500。本用例钉住修复后的闭环。
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task C5修订_未分库租户_请求路径Ensure后选库落主库()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        using var diScope = dbScope.Provider.CreateScope();
        var sp = diScope.ServiceProvider;
        var db = dbScope.Db;
        var config = sp.GetRequiredService<IConfiguration>();

        var tenantId = await InsertProbeTenantAsync(sp, "nodblink", connection: null);
        try
        {
            // ① 请求路径口径:Ensure(required=false) 判定"未配置连接串 → 落主库",并登记负面判定
            var configId = await TenantDbRegistry.EnsureConnectionAsync(tenantId);
            Assert.Equal(SqlSugarConst.MainConfigId, configId);
            Assert.True(TenantDbRegistry.IsMainDatabaseTenant(tenantId), "未分库判定应被登记(供同步选库消费)");

            // ② 修复点:同一租户上下文中构造 [Tenant] 实体仓储 → 必须绑主库(修复前此处抛"租户库连接未注册")
            using (TenantScope.Begin(tenantId))
            {
                var repo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
                Assert.Equal(SqlSugarConst.MainConfigId, repo.Context.CurrentConnectionConfig.ConfigId as string);
            }

            // ③ 该租户随后配置连接串(经服务更新)→ Invalidate 清负面登记 → Ensure(required:true) 走注册分支
            var tenantService = sp.GetRequiredService<SysTenantService>();
            await tenantService.UpdateAsync(new UpdateTenantInput(tenantId, null, null, null, null, null, TenantConnectionString));
            Assert.False(TenantDbRegistry.IsMainDatabaseTenant(tenantId), "连接串变更后负面判定应被清除(下次重新判定)");

            var registered = await TenantDbRegistry.EnsureConnectionAsync(tenantId, required: true);
            Assert.Equal(tenantId.ToString(), registered);
            Assert.False(TenantDbRegistry.IsMainDatabaseTenant(tenantId));

            // ④ 再选库:此时应绑租户库(与 ② 相反,证路由随判定变化)
            using (TenantScope.Begin(tenantId))
            {
                var repo = new SqlSugarRepository<AbTenantProbeEntity>(db, config);
                Assert.Equal(tenantId.ToString(), repo.Context.CurrentConnectionConfig.ConfigId as string);
            }
        }
        finally
        {
            await CleanupProbeAsync(sp, tenantId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:租户行 / 清理 / 取证客户端 / 容器
    // ─────────────────────────────────────────────────────────────

    /// <summary>建本用例的探针租户行(唯一 Name,防与既有行串读);Id 由 AOP 自动填雪花。</summary>
    private static async Task<long> InsertProbeTenantAsync(IServiceProvider sp, string tag, string? connection)
    {
        var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();
        var tenant = new SysTenant
        {
            Name = $"abp_{tag}_{Guid.NewGuid().ToString("N")[..8]}",
            Status = 0,
            Connection = connection,
            DbName = TenantDbName
        };
        await tenantRepo.InsertAsync(tenant);
        return tenant.Id;
    }

    /// <summary>
    /// 用例后置清理(幂等、绝不碰默认租户):
    /// ① 物理删主库探针租户行(唯一 Id;软删会留行干扰后续 IsRegistered/查询口径);
    /// ② 清租户库探针表数据(TRUNCATE;表不存在=本用例未建过 → 跳过);
    /// ③ TenantDbRegistry.Invalidate(释放注册表条目与旧 scope)。
    /// </summary>
    private static async Task CleanupProbeAsync(IServiceProvider sp, long tenantId)
    {
        if (tenantId != SqlSugarConst.DefaultTenantId)
        {
            var tenantRepo = sp.GetRequiredService<SqlSugarRepository<SysTenant>>();
            await tenantRepo.DeletePhysicalByAsync(t => t.Id == tenantId);
        }

        using (var client = CreateProbeClient(TenantConnectionString))
        {
            if (await TableExistsAsync(client, ProbeTable))
            {
                await client.Ado.ExecuteCommandAsync($"TRUNCATE TABLE \"{ProbeTable}\"");
            }
        }

        TenantDbRegistry.Invalidate(tenantId);
    }

    /// <summary>确保默认租户行存在(测试环境不跑种子);已存在则跳过。绝不物理删默认租户(生产数据)。</summary>
    private static async Task EnsureDefaultTenantAsync(SqlSugarRepository<SysTenant> tenantRepo)
    {
        var existing = await tenantRepo.GetFirstAsync(t => t.Id == SqlSugarConst.DefaultTenantId);
        if (existing is null)
        {
            await tenantRepo.InsertAsync(new SysTenant
            {
                Id = SqlSugarConst.DefaultTenantId,
                Name = "默认租户",
                Status = 0,
                ExpirationTime = null
            });
        }
    }

    /// <summary>
    /// 独立取证客户端(直连目标库,不经框架注册表):用于"数据真的落在哪个库"的独立确认。
    /// WHY 手工构造 ConnectionConfig 而非复用 SqlSugarSetup.BuildPgConnectionConfig:后者是 internal
    /// 且 Core 无 InternalsVisibleTo;PG 标识符精确大小写三开关必须与生产口径一致,否则 PascalCase 列名失配。
    /// </summary>
    private static SqlSugarClient CreateProbeClient(string connectionString)
        => new(new ConnectionConfig
        {
            ConfigId = "ab_probe_verify",
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                PgSqlIsAutoToLower = false,
                PgSqlIsAutoToLowerCodeFirst = false,
                PgSqlIsAutoToLowerSchema = false
            }
        });

    /// <summary>目标库当前 schema 是否存在该表(表不存在 → 无行可查,断言据此分支)。</summary>
    private static async Task<bool> TableExistsAsync(ISqlSugarClient client, string table)
    {
        var count = await client.Ado.SqlQueryAsync<int>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t",
            new SugarParameter("@t", table));
        return count.Count > 0 && count[0] > 0;
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// 框架表增量建(InitTables 扫 Core 程序集 → 建 sys_tenant 等缺表,含批 AB 新增 Connection/DbName 列,幂等)。
    /// 改写自 BatchCTenantTest.DbScope,差异 = 可切换的 DbSettings:EnableConnEncrypt(加密/明文两套口径)。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        /// <param name="enableConnEncrypt">DbSettings:EnableConnEncrypt(缺省 false=明文连接串;true=密文口径)。</param>
        public DbScope(bool enableConnEncrypt = false)
        {
            if (!IsPostgresReachable())
            {
                return;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = MainConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):仅本测试解析 CryptogramHelper 需要;加解密对称,与生产值无关
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    // Jwt 段(开发占位口径,与 appsettings 同源,照抄批 C):本类不签发 token,保留以维持容器口径一致
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api",
                    // 库级隔离开关(批 AB):租户连接串是否密文;AddSqlSugar 工厂解析 ISqlSugarClient 时读取
                    ["DbSettings:EnableConnEncrypt"] = enableConnEncrypt ? "true" : "false"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config); // SysTenantService/CryptogramHelper 依赖
            services.AddSqlSugar(config);
            services.AddCache(config);
            // 批 BP D4:补 AddAdminLocalization(与 Program.cs 装配面同口径)——批 AL 时代 LocalizationService
            // 贴 [Service] 经自动发现兜底注册(缺省 Scoped),批 BP 拆双注册归一显式单例后,仅走自动发现
            // 的测试容器(本 DbScope)失去该注册 ⇒ 控制器 BizError 出口(经 RequestServices 解析 ILocalizationService)
            // 回退裸键(BatchBJ 用例全量回归实抓)。
            services.AddAdminLocalization();
            services.AddAutoServices(typeof(SysTenantService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db); // 扫 Core 程序集 → 建 sys_tenant 等缺表(增量幂等)
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 BatchCTenantTest 口径)。</summary>
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