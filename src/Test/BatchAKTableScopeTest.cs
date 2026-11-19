using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Services.RoleTable.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 AK/G13「字段权限表级开关」验证(真实 PG;仓库内存缓存档):
    /// 受管表从"任一角色有授权行即全局受管"(隐式推导→跨角色跨租户爆炸半径)改为
    /// <see cref="SysTableFieldScope"/> 显式声明——未声明/关闭的表零影响;授权自动入册;开关即时生效。
    /// 全部用例走 <see cref="SysRoleTableService"/> 公开读口(与强制点同一判定,防"测了 A 裁了 B")。
    /// WHY 真实 PG:受管判定与授权/开关行都只能由真实库给出;PG 不可达一律显式降级(禁止冒充通过)。
    /// 零污染:探针角色/授权行/开关行 finally 物理清理 + 清缓存键;探针固定 Id 走 99_000_000_004_xx 段。
    /// </remarks>
    public class BatchAKTableScopeTest
    {
        private readonly ITestOutputHelper _output;

        public BatchAKTableScopeTest(ITestOutputHelper output) => _output = output;

        private const string DbConnectionString =
            TestPg.ConnectionString;

        // 探针 Id 段 004_(AH 占 003_):内联于表达式树(私有字段 SqlSugar 拒读,同 BatchAH 口径)
        private const long ProbeRole = 99_000_000_004_001;

        private const long ProbeRole2 = 99_000_000_004_002;

        private const string ProbeTable = "sys_user";

        /// <summary>受管表集合缓存键(与 Service 内常量同值;用例清缓存用)。</summary>
        private const string ManagedTablesKey = CacheConst.KeyRoleTable + "__managed";

        [Fact]
        public async Task 用例1_授权自动入册_受管集含表_开关行Enabled()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            await ClearProbeScopeAsync(scope);
            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            try
            {
                await EnsureProbeRoleAsync(scope, ProbeRole, "ak_probe_role1", "ak探针角色1");
                await svc.GrantAsync(new RoleTableInput(ProbeRole, [$"{ProbeTable}:Account", $"{ProbeTable}:RealName"]));

                var managed = await svc.GetManagedTablesAsync();
                Assert.Contains(ProbeTable, managed);

                var scopeRow = await scope.Db.Queryable<SysTableFieldScope>()
                    .FirstAsync(s => s.TableName == ProbeTable);
                Assert.NotNull(scopeRow);
                Assert.True(scopeRow!.Enabled);
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
            }
        }

        [Fact]
        public async Task 用例2_手动关闭_受管集立即移除_全列放行()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            await ClearProbeScopeAsync(scope);
            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            try
            {
                await EnsureProbeRoleAsync(scope, ProbeRole, "ak_probe_role2", "ak探针角色2");
                await svc.GrantAsync(new RoleTableInput(ProbeRole, [$"{ProbeTable}:Account"]));
                await svc.SetEnabledAsync(ProbeTable, false);

                var managed = await svc.GetManagedTablesAsync();
                Assert.DoesNotContain(ProbeTable, managed);

                _output.WriteLine($"判定②:关闭开关后受管集不含 {ProbeTable}");
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
            }
        }

        [Fact]
        public async Task 用例3_重启用_受管集恢复()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            await ClearProbeScopeAsync(scope);
            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            try
            {
                await EnsureProbeRoleAsync(scope, ProbeRole, "ak_probe_role3", "ak探针角色3");
                await svc.GrantAsync(new RoleTableInput(ProbeRole, [$"{ProbeTable}:Account"]));
                await svc.SetEnabledAsync(ProbeTable, false);
                await svc.SetEnabledAsync(ProbeTable, true);

                var managed = await svc.GetManagedTablesAsync();
                Assert.Contains(ProbeTable, managed);
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
            }
        }

        [Fact]
        public async Task 用例4_迁移_开关表全空且历史授权存在_自动入册()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            var cache = scope.Provider.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
            try
            {
                // 前置:授权行存在(模拟升级库的历史授权)
                await EnsureProbeRoleAsync(scope, ProbeRole, "ak_probe_role4", "ak探针角色4");
                await svc.GrantAsync(new RoleTableInput(ProbeRole, [$"{ProbeTable}:Account"]));

                // 模拟升级:清空开关表 + 清受管缓存 ⇒ 显式迁移钩子把历史授权表批量入册
                await scope.Db.Deleteable<SysTableFieldScope>().ExecuteCommandAsync();
                await cache.RemoveAsync(ManagedTablesKey);

                await svc.MigrateLegacyScopesAsync();

                var managed = await svc.GetManagedTablesAsync();
                Assert.Contains(ProbeTable, managed);

                var scopeRow = await scope.Db.Queryable<SysTableFieldScope>()
                    .FirstAsync(s => s.TableName == ProbeTable);
                Assert.NotNull(scopeRow);
                Assert.True(scopeRow!.Enabled);
                _output.WriteLine("判定④:开关表全空 + 历史授权存在 → 读侧自动入册(升级兼容)");
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
            }
        }

        [Fact]
        public async Task 用例5_非法表名_拒绝()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            await ClearProbeScopeAsync(scope);
            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            try
            {
                var ex = await Assert.ThrowsAsync<AppException>(() => svc.SetEnabledAsync("no_such_table_x", true));
                Assert.Equal(400, ex.Code);
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
            }
        }

        [Fact]
        public async Task 用例6_清单_全表含开关状态与已授权角色数()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            await ClearProbeScopeAsync(scope);
            var svc = scope.Provider.GetRequiredService<SysRoleTableService>();
            try
            {
                await EnsureProbeRoleAsync(scope, ProbeRole, "ak_probe_role6a", "ak探针角色6a");
                await EnsureProbeRoleAsync(scope, ProbeRole2, "ak_probe_role6b", "ak探针角色6b");
                await svc.GrantAsync(new RoleTableInput(ProbeRole, [$"{ProbeTable}:Account"]));
                await svc.GrantAsync(new RoleTableInput(ProbeRole2, [$"{ProbeTable}:RealName"]));

                var list = await svc.GetTableScopesAsync();
                var row = list.FirstOrDefault(n => string.Equals(n.TableName, ProbeTable, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(row);
                Assert.True(row!.Enabled, "授权已自动入册");
                Assert.Equal(2, row.GrantedRoleCount);

                // 关闭后:清单里 enabled=false 且角色数保留(提示"有授权但已停管")
                await svc.SetEnabledAsync(ProbeTable, false);
                var list2 = await svc.GetTableScopesAsync();
                var row2 = list2.First(n => string.Equals(n.TableName, ProbeTable, StringComparison.OrdinalIgnoreCase));
                Assert.False(row2.Enabled);
                Assert.Equal(2, row2.GrantedRoleCount);
            }
            finally
            {
                await CleanupAsync(scope, ProbeRole);
                await CleanupAsync(scope, ProbeRole2);
            }
        }

        /// <summary>探针角色幂等重建(固定 Id;Name/Code 走 ak_ 前缀防撞名;同 BatchAH 口径)。</summary>
        private static async Task EnsureProbeRoleAsync(DbScope scope, long roleId, string code, string name)
        {
            if (await scope.Db.Queryable<SysRole>().AnyAsync(r => r.Id == roleId))
            {
                return;
            }

            await scope.Db.Insertable(new SysRole
            {
                Id = roleId,
                Name = name,
                Code = code,
                Remark = "批 AK 探针",
                DataScope = 4,
                SysFlag = false,
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();
        }

        /// <summary>前置清理:删探针表名开关行 + 清受管缓存(防 BatchAH 等共库测试经 Grant 残留的开关行干扰断言)。</summary>
        private static async Task ClearProbeScopeAsync(DbScope scope)
        {
            await scope.Db.Deleteable<SysTableFieldScope>()
                .Where(s => s.TableName == ProbeTable).ExecuteCommandAsync();
            var cache = scope.Provider.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
            await cache.RemoveAsync(ManagedTablesKey);
        }

        private static async Task CleanupAsync(DbScope scope, params long[] roleIds)
        {
            await scope.Db.Deleteable<SysRoleTable>()
                .Where(r => roleIds.Contains(r.RoleId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysRole>()
                .Where(r => roleIds.Contains(r.Id)).ExecuteCommandAsync();
            // 开关行:只清探针表名自己的(不碰其它数据)
            if (roleIds.Contains(ProbeRole))
            {
                await scope.Db.Deleteable<SysTableFieldScope>()
                    .Where(s => s.TableName == ProbeTable).ExecuteCommandAsync();
            }

            var cache = scope.Provider.GetRequiredService<AdminCore.Core.Cache.ICacheService>();
            foreach (var roleId in roleIds)
            {
                await cache.RemoveAsync($"{CacheConst.KeyRoleTable}{roleId}");
            }

            await cache.RemoveAsync(ManagedTablesKey);
        }

        /// <summary>DB 测试容器(与 BatchAH 同一构造路径:AddSqlSugar + AddCache + AddAutoServices + InitTables)。</summary>
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
                        ["Yit:WorkerId"] = "1"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor());
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysRoleTableService).Assembly);

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
                    using var client = new System.Net.Sockets.TcpClient();
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
}
