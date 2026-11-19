using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.Log.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 X 单测:5.4 库表差异日志(SqlSugar 217 = 每次操作显式 opt-in + DbSettings:EnableDiffLog 总闸;
    /// INSERT/UPDATE/DELETE 三档/软删默认 opt-in/自回环防护/查询侧不载大字段/CreateTime 闭区间)。
    /// 探针:dsh_x_diff_probe 表(手工 InitTables)。
    /// </summary>
    public class BatchXDiffLogTest
    {
        [Fact]
        public async Task 插入_显式optIn_落一条INSERT差异日志()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            await scope.Db.InsertWithDiffLogAsync(new ProbeDiffEntity { Name = "x", Value = 1 }, true);
            var rows = await WaitDiffCountAsync(scope, 1);

            Assert.Equal(1, rows.Count);
            Assert.Equal("INSERT", rows[0].DiffType);
            Assert.Equal("dsh_x_diff_probe", rows[0].BusinessData);   // 未手动传 BusinessData → 表名
            Assert.DoesNotContain("\"x\"", rows[0].BeforeData ?? ""); // 插入无前值
            Assert.Contains("dsh_x_diff_probe", rows[0].AfterData);
            Assert.Contains("\"x\"", rows[0].AfterData);
            Assert.False(string.IsNullOrEmpty(rows[0].Sql));
            Assert.True(rows[0].Elapsed >= 0);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 更新_显式optIn_落一条UPDATE差异日志含前后值()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            var entity = await scope.Db.Insertable(new ProbeDiffEntity { Name = "before", Value = 1 }).ExecuteReturnEntityAsync();
            await Task.Delay(50);                                     // fire-and-forget 落库让位
            await ClearDiffLogsAsync(scope);

            entity.Name = "after";
            entity.Value = 2;
            await scope.Db.UpdateWithDiffLogAsync(entity, true);
            var rows = await WaitDiffCountAsync(scope, 1);

            Assert.Equal(1, rows.Count);
            Assert.Equal("UPDATE", rows[0].DiffType);
            Assert.Contains("\"before\"", rows[0].BeforeData);        // 操作前快照
            Assert.Contains("\"after\"", rows[0].AfterData);          // 操作后快照
            Assert.True(rows[0].Elapsed >= 0);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 软删默认optIn走UPDATE档_硬删显式optIn落DELETE档()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            var entity = await scope.Db.Insertable(new ProbeDiffEntity { Name = "s1", Value = 1 }).ExecuteReturnEntityAsync();
            await Task.Delay(50);
            await ClearDiffLogsAsync(scope);

            // 软删(仓储 DeleteByIdAsync = IsDelete 更新,按开关自动 opt-in,对齐参照 FakeDelete)→ UPDATE 档
            var repo = scope.Rep();
            Assert.True(await repo.DeleteByIdAsync(entity.Id));
            var soft = await WaitDiffCountAsync(scope, 1);
            Assert.Equal("UPDATE", soft[0].DiffType);
            Assert.Contains("IsDelete", soft[0].AfterData);           // 软删列在操作后快照

            // 硬删(条件物理删,显式 opt-in)→ DELETE 档
            await ClearDiffLogsAsync(scope);
            await scope.Db.DeleteWithDiffLogAsync<ProbeDiffEntity>(p => p.Id == entity.Id, true);
            var hard = await WaitDiffCountAsync(scope, 1);
            Assert.Equal("DELETE", hard[0].DiffType);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 未optIn的写操作_不落差异日志()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            // 开关开但走普通 CRUD(未 opt-in)→ SqlSugar 217 不触发钩子 → 0 行
            await scope.Db.Insertable(new ProbeDiffEntity { Name = "plain", Value = 1 }).ExecuteCommandAsync();
            await Task.Delay(800);

            var total = await scope.Db.Queryable<SysLogDiff>().CountAsync();
            Assert.Equal(0, total);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 开关关_显式optIn也不落差异日志()
        {
            using var scope = new DbScope(enableDiffLog: false);
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            // 总闸关 = 全局钩子未注册 → EnableDiffLogEvent 空转 → 0 行
            await scope.Db.InsertWithDiffLogAsync(new ProbeDiffEntity { Name = "off", Value = 1 }, true);
            await Task.Delay(800);

            var total = await scope.Db.Queryable<SysLogDiff>().CountAsync();
            Assert.Equal(0, total);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 自回环防护_差异日志写入自身不再生成新差异()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            await scope.Db.InsertWithDiffLogAsync(new ProbeDiffEntity { Name = "loop", Value = 1 }, true);
            await WaitDiffCountAsync(scope, 1);
            await Task.Delay(800);                                     // 给"回环"充足时间(若防护失配会再写)

            var total = await scope.Db.Queryable<SysLogDiff>().CountAsync();
            Assert.Equal(1, total);                                    // 只 1 条:差异日志写入自身未触发新差异
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 分页_不载大字段_详情全量()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            await scope.Db.InsertWithDiffLogAsync(new ProbeDiffEntity { Name = "page", Value = 1 }, true);
            var rows = await WaitDiffCountAsync(scope, 1);
            var svc = scope.GetDiffService();

            var page = await svc.PageAsync(new PageLogDiffInput(null, null, 1, 10));
            var item = page.List.Single(u => u.Id == rows[0].Id);
            // 列表不载大字段由 record 投影结构保证(PageLogDiffOutput 无 BeforeData/AfterData 成员——
            // 类型层面即排除,SQL 不查这两列;e2e 侧另验响应体无字段)
            Assert.Equal("INSERT", item.DiffType);

            var detail = await svc.GetDetailAsync(rows[0].Id);
            Assert.NotNull(detail.BeforeData);
            Assert.NotNull(detail.AfterData);
            Assert.Contains("dsh_x_diff_probe", detail.AfterData);
            await ClearProbes(scope);
        }

        [Fact]
        public async Task 分页_CreateTime闭区间过滤()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            await ClearProbes(scope);

            await scope.Db.InsertWithDiffLogAsync(new ProbeDiffEntity { Name = "filter", Value = 1 }, true);
            await WaitDiffCountAsync(scope, 1);
            var svc = scope.GetDiffService();

            var hit = await svc.PageAsync(new PageLogDiffInput(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5), 1, 10));
            Assert.True(hit.Total >= 1);

            // 过去区间 → 0
            var miss = await svc.PageAsync(new PageLogDiffInput(new DateTime(2000, 1, 1), new DateTime(2000, 1, 2), 1, 10));
            Assert.Equal(0, miss.Total);
            await ClearProbes(scope);
        }

        /// <summary>探针实体:差异日志钩子捕获对象(表 dsh_x_diff_probe,手工 InitTables)。</summary>
        [SugarTable("dsh_x_diff_probe")]
        public class ProbeDiffEntity : EntityTenant
        {
            [SugarColumn(Length = 64, IsNullable = true)]
            public string? Name { get; set; }

            [SugarColumn(IsNullable = true)]
            public int? Value { get; set; }
        }

        sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public bool PgReachable { get; }

            public DbScope(bool enableDiffLog = true)
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
                        ["DbSettings:EnableDiffLog"] = enableDiffLog ? "true" : "false"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysNotice).Assembly);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                // 进程级闸门(2026-10-26):HealColumnCase+InitTables 实测 7.4s/次且幂等,只跑一次
                TestSchemaBootstrap.Ensure(Db);
                Db.CodeFirst.InitTables(typeof(ProbeDiffEntity));
                PgReachable = true;
            }

            // 具体类型(软删 DeleteByIdAsync 重写在具体类)
            public SqlSugarRepository<ProbeDiffEntity> Rep()
                => _provider!.GetRequiredService<SqlSugarRepository<ProbeDiffEntity>>();

            public SysLogDiffService GetDiffService()
                => _provider!.GetRequiredService<SysLogDiffService>();

            public void Dispose()
            {
                _provider?.Dispose();
            }

            private static string DbConnectionString =>
                TestPg.ConnectionString;

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

        // ── 辅助 ──

        /// <summary>轮询等差异日志条数(fire-and-forget 落库有延迟;≤5s 超时即返回当前列表)。</summary>
        private static async Task<List<SysLogDiff>> WaitDiffCountAsync(DbScope scope, int expected)
        {
            for (var i = 0; i < 50; i++)
            {
                var list = await scope.Db.Queryable<SysLogDiff>().OrderByDescending(u => u.Id).ToListAsync();
                if (list.Count >= expected)
                {
                    return list;
                }

                await Task.Delay(100);
            }

            return await scope.Db.Queryable<SysLogDiff>().OrderByDescending(u => u.Id).ToListAsync();
        }

        private static async Task ClearDiffLogsAsync(DbScope scope)
            => await scope.Db.Ado.ExecuteCommandAsync("TRUNCATE sys_log_diff");

        private static async Task ClearProbes(DbScope scope)
        {
            // 差异日志是 fire-and-forget 异步写入:上一用例发出的那条可能在本 TRUNCATE 之后才落库,
            // 从而污染本用例的「只剩本次记录」断言。原先靠每用例 ~7s 的 DbScope 初始化窗口隐式让位,
            // 初始化去重后该窗口消失 ⇒ 改为显式让位(2026-10-26 性能实验暴露的隐藏时序耦合)。
            await Task.Delay(200);
            await scope.Db.Ado.ExecuteCommandAsync("TRUNCATE dsh_x_diff_probe, sys_log_diff");
        }
    }
}
