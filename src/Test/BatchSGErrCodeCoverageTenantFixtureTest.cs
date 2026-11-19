// 批 S-G 纵深期·错误码覆盖补漏（租户域·**真库只读档**：全程不写一行数据）。
// 覆盖：tenant_db.tenant_not_found / tenant.default_undisableable（把"需真库夹具"的两条一并收掉）。
// 抛点实读：
//   · `TenantDbRegistry.cs:131-140`——`EnsureConnectionAsync(tenantId, required: true)` 查不到租户登记行才抛；
//     **`required: false` 时走"回落主库"分支不抛**（`:133-137`），所以本用例必须显式传 true，否则测的是另一条路径。
//   · `SysTenantService.cs:258-267`——`SetStatusAsync` 先查行：查不到 ⇒ `TenantNotFound`；查到且
//     `Id == SqlSugarConst.DefaultTenantId && Status != 0` ⇒ `TenantDefaultUndisableable`（code 400）。
// **零写库依据（实读，不是推定）**：两条用例的抛出点都在 `UpdateAsync` **之前** ⇒ 本文件不产生数据行，
//   也就不需要探针行与 finally 物理清理（对比同目录写库档必须清理，否则串扰后续行数断言）。
// 依赖注入口径：只从容器取 `ISqlSugarRepository<SysTenant>`，服务其余 5 个构造依赖传 `null!`——两条路径都在
//   触碰它们之前抛出；**若将来某依赖被提前触碰，本用例将以 NullReference 红，而不是悄悄假绿**。
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Localization;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Tenant;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

public class BatchSGErrCodeCoverageTenantFixtureTest
{
    // 连接常量由 BatchABTenantDbTest.cs 原样搬入（脚本注入，避免在会话/评审面里回显凭据）

    private const string MainConnectionString =
    TestPg.ConnectionString;
    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;
    private const string CryptogramKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    [Fact]
    public async Task 租户登记行缺失且要求分库_精确断言()
    {
        using var scope = DbScope.Create();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        // 不存在的租户 Id 取"合法 long"（口径见 CLAUDE.md e2e 负探针断言：超 Int64.MaxValue 会被模型验证层截走，拿不到业务 errCode）
        const long missingTenantId = 9_223_372_036_854_770_001;

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            TenantDbRegistry.EnsureConnectionAsync(missingTenantId, required: true));
        Assert.Equal(ErrorCodes.TenantDbTenantNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task 默认租户禁被停用_精确断言()
    {
        using var scope = DbScope.Create();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var repo = scope.Provider.GetRequiredService<ISqlSugarRepository<SysTenant>>();
        // 前置：默认租户行必须真实存在——它是"禁停用"这条语义的承载体，缺了就只能红，不能假装通过
        Assert.NotNull(await repo.GetFirstAsync(u => u.Id == SqlSugarConst.DefaultTenantId));

        var svc = new SysTenantService(repo, null!, null!, null!, null!, null!);

        // Status=1（停用）打在默认租户上 ⇒ 必抛；Status=0 的正常路径不在本用例内（会写库，本文件承诺零写库）
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.SetStatusAsync(new SetTenantStatusInput(SqlSugarConst.DefaultTenantId, 1)));
        Assert.Equal(ErrorCodes.TenantDefaultUndisableable, ex.ErrorCode);
        Assert.Equal(400, ex.Code);
    }

    /// <summary>最小真库夹具（照 BatchABTenantDbTest 的装配口径裁剪：AddSqlSugar＋AddCache＋自动发现＋schema 幂等自举；不做租户分库）。</summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;
        public IServiceProvider Provider { get; } = null!;
        public bool PgReachable { get; }

        public static DbScope Create() => new();

        private DbScope()
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
                    ["Cryptogram:Key"] = CryptogramKey,
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddCache(config);
            services.AddAdminLocalization();
            services.AddAutoServices(typeof(SysTenantService).Assembly);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测（2 秒，沙箱 PG 黑洞防挂死，同批 AB/批 C 口径）。</summary>
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
