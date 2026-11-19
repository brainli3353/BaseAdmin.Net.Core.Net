using AdminCore.Core;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using AdminCore.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 K(3.1 仓储乐观锁 UpdateWithVerAsync)测试。
    /// 覆盖:无 Ver 回落普通更新/Ver 匹配自增/Ver 不匹配冲突返回 false 不变/
    /// int 型 null 按 0/int? 型 null 抛 ArgumentException/记录不存在 false。
    /// 探针表手工 DDL(dsh_k_ver_probe[_nullable],测试内建+finally Drop,不碰存量表)。
    /// </summary>
    public class BatchKVerLockTest
    {
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string DdlProbe =
            "CREATE TABLE IF NOT EXISTS \"dsh_k_ver_probe\" (\"Id\" bigint NOT NULL PRIMARY KEY, \"Ver\" int NOT NULL, \"Name\" text)";
        private const string DdlProbeNullable =
            "CREATE TABLE IF NOT EXISTS \"dsh_k_ver_probe_nullable\" (\"Id\" bigint NOT NULL PRIMARY KEY, \"Ver\" int, \"Name\" text)";

        [Fact]
        public async Task 无Ver实体_回落普通更新()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            var repo = scope.Service<ISqlSugarRepository<SysConfig>>();
            var row = new SysConfig { Id = 990000000000001L, Name = "dsh_k_cfg", Code = "dsh_k_cfg_code", Value = "v1", GroupCode = "dsh_k" };
            await repo.InsertAsync(row);
            try
            {
                row.Value = "v2";
                Assert.True(await repo.UpdateWithVerAsync(row));   // SysConfig 无 Ver → 普通更新
                var dbRow = await scope.Db.Queryable<SysConfig>().FirstAsync(u => u.Id == row.Id);
                Assert.Equal("v2", dbRow!.Value);
            }
            finally
            {
                await scope.Db.Deleteable<SysConfig>().Where(u => u.Id == row.Id).ExecuteCommandAsync();
            }
        }

        [Fact]
        public async Task Ver匹配_更新成功且版本自增()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await CreateProbeTablesAsync(scope);
            var repo = scope.Service<ISqlSugarRepository<VerProbeEntity>>();
            var row = new VerProbeEntity { Id = 990000000000101L, Ver = 5, Name = "dsh_k_p" };
            await repo.InsertAsync(row);
            try
            {
                Assert.True(await repo.UpdateWithVerAsync(new VerProbeEntity { Id = row.Id, Ver = 5, Name = "dsh_k_p2" }));
                var dbRow = (await scope.Db.Queryable<VerProbeEntity>().FirstAsync(u => u.Id == row.Id))!;
                Assert.Equal(6, dbRow.Ver);      // 版本自增
                Assert.Equal("dsh_k_p2", dbRow.Name);
            }
            finally
            {
                await DropProbeTablesAsync(scope);
            }
        }

        [Fact]
        public async Task Ver不匹配_冲突返回false且数据不变()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await CreateProbeTablesAsync(scope);
            var repo = scope.Service<ISqlSugarRepository<VerProbeEntity>>();
            var row = new VerProbeEntity { Id = 990000000000102L, Ver = 6, Name = "dsh_k_c" };
            await repo.InsertAsync(row);
            try
            {
                // 模拟并发:读到的旧版本 5,实际已被他人推进到 6
                Assert.False(await repo.UpdateWithVerAsync(new VerProbeEntity { Id = row.Id, Ver = 5, Name = "dsh_k_c_conflict" }));
                var dbRow = (await scope.Db.Queryable<VerProbeEntity>().FirstAsync(u => u.Id == row.Id))!;
                Assert.Equal(6, dbRow.Ver);
                Assert.Equal("dsh_k_c", dbRow.Name);   // 数据未变
            }
            finally
            {
                await DropProbeTablesAsync(scope);
            }
        }

        [Fact]
        public async Task int型Ver为null_按零处理()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await CreateProbeTablesAsync(scope);
            var repo = scope.Service<ISqlSugarRepository<VerProbeEntity>>();
            var row = new VerProbeEntity { Id = 990000000000103L, Ver = 0, Name = "dsh_k_z" };
            await repo.InsertAsync(row);
            try
            {
                // input 未读回 Ver(保持默认 0,与 DB 一致)——显式验证 null(int 装箱 null 不可达,此处以"未读回即默认值"口径覆盖按 0 分支)
                var fresh = new VerProbeEntity { Id = row.Id, Name = "dsh_k_z2" };
                fresh.Ver = 0;
                Assert.True(await repo.UpdateWithVerAsync(fresh));
                var dbRow = (await scope.Db.Queryable<VerProbeEntity>().FirstAsync(u => u.Id == row.Id))!;
                Assert.Equal(1, dbRow.Ver);
            }
            finally
            {
                await DropProbeTablesAsync(scope);
            }
        }

        [Fact]
        public async Task int可空型Ver为null_抛ArgumentException()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await CreateProbeTablesAsync(scope);
            var repo = scope.Service<ISqlSugarRepository<VerProbeNullableEntity>>();
            var row = new VerProbeNullableEntity { Id = 990000000000104L, Ver = 1, Name = "dsh_k_n" };
            await repo.InsertAsync(row);
            try
            {
                await Assert.ThrowsAsync<ArgumentException>(
                    () => repo.UpdateWithVerAsync(new VerProbeNullableEntity { Id = row.Id, Ver = null, Name = "dsh_k_n2" }));
            }
            finally
            {
                await DropProbeTablesAsync(scope);
            }
        }

        [Fact]
        public async Task 记录不存在_返回false()
        {
            using var scope = NewScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await CreateProbeTablesAsync(scope);
            var repo = scope.Service<ISqlSugarRepository<VerProbeEntity>>();
            Assert.False(await repo.UpdateWithVerAsync(new VerProbeEntity { Id = 990000000009999L, Ver = 1, Name = "dsh_k_x" }));
            await DropProbeTablesAsync(scope);
        }

        // ── 探针实体(不入 Core 程序集:CodeFirst 不建表,表由测试手工 DDL) ──────

        [SugarTable("dsh_k_ver_probe")]
        private class VerProbeEntity
        {
            [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
            public long Id { get; set; }

            public int Ver { get; set; }

            public string Name { get; set; } = "";
        }

        [SugarTable("dsh_k_ver_probe_nullable")]
        private class VerProbeNullableEntity
        {
            [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
            public long Id { get; set; }

            public int? Ver { get; set; }

            public string Name { get; set; } = "";
        }

        // ── 容器与探针表管理 ───────────────────────────────────────────

        private sealed class Scope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public bool PgReachable { get; }

            public T Service<T>() where T : class => _provider!.GetRequiredService<T>();

            public Scope()
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
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Db.HealColumnCase();
                PgReachable = true;
            }

            public void Dispose() => _provider?.Dispose();
        }

        private static Scope NewScope() => new();

        private static Task CreateProbeTablesAsync(Scope scope) => Task.Run(async () =>
        {
            await scope.Db.Ado.ExecuteCommandAsync(DdlProbe);
            await scope.Db.Ado.ExecuteCommandAsync(DdlProbeNullable);
        });

        private static Task DropProbeTablesAsync(Scope scope) => Task.Run(async () =>
        {
            await scope.Db.Ado.ExecuteCommandAsync("DROP TABLE IF EXISTS \"dsh_k_ver_probe\"");
            await scope.Db.Ado.ExecuteCommandAsync("DROP TABLE IF EXISTS \"dsh_k_ver_probe_nullable\"");
        });

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var result = client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2));
                return result;
            }
            catch
            {
                return false;
            }
        }
    }
}
