// 批 t846 租户隔离用例族·第一员:sys_open_access(2026-10-27 批 2 纳管)。
// 判据来自 docs/业务/规划/专项_租户纳管全量化_2026-10-26.md §7(已定稿,勿自创):
//   正＝租户 A 非超管视角 → 管理面列表只含 TenantId=A 的行;
//   负＝同视角按他租户 B 的行 id 直取 → 取不到(对应错误码);
//   前提＝另有一条**不经租户过滤的裸 SQL**断言"TenantId=B 的行确实存在"——缺它本族会因
//          "库里根本没造出 B 的行"而假绿(判据硬组成)。
// 复用形状:BatchBODictTest 的租户视角装配(与 PermissionMiddleware 同源:先清后设,finally 恢复)
//          ＋ BatchEOpenAccessTest 的服务取得方式/DbScope/PgReachable 探针纪律。
// 探针纪律:AccessKey 唯一前缀 t46_iso_ + Guid;IAsyncLifetime 兜底清理 + 用例内 finally 清理,
//          结束时按前缀物理删除并断言 count=0(现有 8 行 t13v_ak 软删行不动)。
// 用例族可追加:后续每纳管一张表加一个成员(一个 [Fact] 覆盖 正/负/裸查询前提 三段),不逐表复制机制。
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Test
{
    public class BatchT46TenantIsolationTest : IAsyncLifetime
    {
        // 探针登记:用例失败后 DisposeAsync 仍执行(成功路径内联清理保留,幂等)
        private readonly List<string> _probeMarkers = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_probeMarkers.Count == 0)
            {
                return;
            }

            using var dbScope = new DbScope();
            if (!dbScope.PgReachable)
            {
                return;
            }

            foreach (var marker in _probeMarkers)
            {
                await CleanupAndAssertAsync(dbScope, marker);
            }
        }

        // 探针租户段(仅作过滤值/行归属值,不要求 sys_tenant 存在——本表无租户存在性校验)
        private const long TenantA = 99_000_000_046_001L;
        private const long TenantB = 99_000_000_046_002L;

        // ─────────────────────────────────────────────────────────────
        // 前提(硬组成):不经租户过滤器,证明 TenantId=B 的行确实在库里
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_open_access_租户隔离_裸查询证明他租户行确实存在()
        {
            using var dbScope = new DbScope();
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = NewMarker();
            await SeedProbeRowsAsync(dbScope, marker);
            try
            {
                // 裸 SQL(不经 SqlSugar QueryFilter:租户/软删过滤器均不参与)直读物理行。
                // 这一条是判据的硬组成:正/负两侧都依赖"B 行真的存在",否则负向会假绿。
                var countB = await dbScope.Db.Ado.GetIntAsync(
                    "select count(*) from sys_open_access where \"AccessKey\" = @k and \"TenantId\" = @t and \"IsDelete\" = false",
                    new { k = marker + "b", t = TenantB });
                Assert.Equal(1, countB);

                // 同时钉住 A 行也在(两侧对照,避免只有 B 存在时正向断言误读)
                var countA = await dbScope.Db.Ado.GetIntAsync(
                    "select count(*) from sys_open_access where \"AccessKey\" = @k and \"TenantId\" = @t and \"IsDelete\" = false",
                    new { k = marker + "a", t = TenantA });
                Assert.Equal(1, countA);
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, marker);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 正:租户 A 视角 → 管理面列表只含 TenantId=A 的探针行
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_open_access_租户隔离_租户A视角列表只含本租户行()
        {
            using var dbScope = new DbScope();
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = NewMarker();
            await SeedProbeRowsAsync(dbScope, marker);
            try
            {
                using var diScope = dbScope.Provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var svc = sp.GetRequiredService<SysOpenAccessService>();
                var db = sp.GetRequiredService<ISqlSugarClient>();

                // 复现「租户 A 非超管」请求视角(PermissionMiddleware 同源装配:先清后设;finally 恢复)
                try
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                    db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == TenantA);

                    // 管理面列表(PageAsync 即管理端唯一列表入口,按 AccessKey 前缀圈本探针)
                    var page = await svc.PageAsync(new PageOpenAccessInput(marker));
                    Assert.Equal(1, page.Total);
                    var only = Assert.Single(page.List);
                    Assert.Equal(marker + "a", only.AccessKey);
                    Assert.Equal(TenantA, only.BindTenantId);
                    // 他租户 B 的行(前提已证存在)不得出现在 A 的列表里
                    Assert.DoesNotContain(page.List, r => r.AccessKey == marker + "b");
                    // 列表口径:只含 TenantId=A 的行(BindTenantId 与 TenantId 建号同源)
                    Assert.All(page.List, r => Assert.Equal(TenantA, r.BindTenantId));
                }
                finally
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                }
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, marker);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 负:同视角按他租户 B 的行 id 直取 → 取不到
        // ─────────────────────────────────────────────────────────────
        [Fact]
        public async Task sys_open_access_租户隔离_按他租户B行id直取取不到()
        {
            using var dbScope = new DbScope();
            if (!dbScope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = NewMarker();
            var rows = await SeedProbeRowsAsync(dbScope, marker);
            var idB = rows.BId;
            Assert.True(idB > 0);
            try
            {
                using var diScope = dbScope.Provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var svc = sp.GetRequiredService<SysOpenAccessService>();
                var repo = sp.GetRequiredService<ISqlSugarRepository<SysOpenAccess>>();
                var db = sp.GetRequiredService<ISqlSugarClient>();

                try
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                    db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == TenantA);

                    // 直取读路径(UpdateAsync 存在性校验用的同一个仓储入口):A 视角按 B 的 id 查 → null
                    var byId = await repo.GetFirstAsync(u => u.Id == idB);
                    Assert.Null(byId);

                    // 管理面按 id 直取的服务入口(UpdateAsync 首步即 GetFirstAsync(u => u.Id == id)),
                    // 过滤生效时必须抛 NotFound;若过滤失效则会先改绑/报别的码 ⇒ 断言直接翻红。
                    var ex = await Assert.ThrowsAsync<AppException>(() =>
                        svc.UpdateAsync(new UpdateOpenAccessInput(idB, 0, TenantB)));
                    Assert.Equal(ErrorCodes.OpenAccessNotFound, ex.ErrorCode);
                }
                finally
                {
                    db.QueryFilter.Clear<ITenantIdFilter>();
                }

                // 负向操作未产生副作用:B 行仍在原租户(裸 SQL 复核)
                var untouched = await dbScope.Db.Ado.GetIntAsync(
                    "select count(*) from sys_open_access where id = @id and \"TenantId\" = @t and \"BindTenantId\" = @t and \"IsDelete\" = false",
                    new { id = idB, t = TenantB });
                Assert.Equal(1, untouched);
            }
            finally
            {
                await CleanupAndAssertAsync(dbScope, marker);
            }
        }

        // ── 辅助 ─────────────────────────────────────────────────────────

        private string NewMarker()
        {
            var marker = $"t46_iso_{Guid.NewGuid():N}";
            _probeMarkers.Add(marker);
            return marker;
        }

        /// <summary>造 A/B 各一行探针(行归属 TenantId 与绑定租户同源,与 AddAsync 写侧同语义)。</summary>
        private static async Task<(long AId, long BId)> SeedProbeRowsAsync(DbScope scope, string marker)
        {
            var a = new SysOpenAccess
            {
                AccessKey = marker + "a",
                AccessSecret = OpenAccessSignHelper.CreateSecret(),
                BindUserId = 0,
                BindTenantId = TenantA,
                TenantId = TenantA
            };
            var b = new SysOpenAccess
            {
                AccessKey = marker + "b",
                AccessSecret = OpenAccessSignHelper.CreateSecret(),
                BindUserId = 0,
                BindTenantId = TenantB,
                TenantId = TenantB
            };
            await scope.Db.Insertable(new[] { a, b }).ExecuteCommandAsync();
            return (a.Id, b.Id);
        }

        /// <summary>按唯一前缀物理删除探针行,并断言行数归零(撞唯一索引事故史的收尾纪律)。</summary>
        private static async Task CleanupAndAssertAsync(DbScope scope, string marker)
        {
            var db = scope.Db;
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_open_access where \"AccessKey\" like @p", new { p = marker + "%" });
            var left = await db.Ado.GetIntAsync(
                "select count(*) from sys_open_access where \"AccessKey\" like @p", new { p = marker + "%" });
            Assert.Equal(0, left);
        }

        /// <summary>
        /// DB 作用域:与生产同一条 AddSqlSugar/AddCache 装配(仓储/AOP/软删全生效),形状照抄 BatchEOpenAccessTest;
        /// CodeFirst 扫 Core 程序集(含 sys_open_access 的 TenantId 列)。
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
                        ["Yit:WorkerId"] = "1"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddSignalR(); // 与 BatchEOpenAccessTest 同装配(SysOpenAccessService 链路依赖不触碰,保持同源)

                services.AddAutoServices(typeof(SysUser).Assembly);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                Db.HealColumnCase();
                Db.InitTables(typeof(SqlSugarSetup).Assembly);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
                (Db as IDisposable)?.Dispose();
            }

            private static bool IsPostgresReachable()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var task = client.ConnectAsync(DbHost, DbPort);
                    return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
                }
                catch
                {
                    return false;
                }
            }

            private const string DbHost = "127.0.0.1";
            private const int DbPort = 5432;
            private const string DbConnectionString =
                TestPg.ConnectionString;
        }
    }
}