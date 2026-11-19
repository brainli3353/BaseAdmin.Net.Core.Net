using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Cache;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Serial;
using AdminCore.Core.Services.Serial.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 W 单测:4.3 流水号(序列状态机/取号/重置/上限/并发)+ 3.4 [BindSerial] AOP 填充。
    /// 探针:sys_serial 行(Type=test 内置注册类型)+ [BindSerial] 探针实体(dsh_w 前缀,表手工 InitTables)。
    /// </summary>
    public class BatchWSerialTest
    {
        [Fact]
        public async Task 类型列表_内置test类型已注册()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var list = svc.GetTypeList();
            Assert.Contains(list, u => u.Value == "test");
        }

        [Fact]
        public async Task 新增详情分页_配置往返一致()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);

            var id = await svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Day, "{yyyy}{MM}{dd}-{SEQ}"));
            Assert.True(id > 0);

            var detail = await svc.DetailAsync(id);
            Assert.Equal("test", detail.Type);
            Assert.Equal("{yyyy}{MM}{dd}-{SEQ}", detail.Formater);
            Assert.Equal(1, detail.Seq);          // 初值
            Assert.Equal(0, detail.Status);       // 缺省启用
            Assert.Equal(1, detail.Min);
            Assert.Equal(1_000_000_000, detail.Max);

            var page = await svc.PageAsync(new PageSerialInput(1, 10, "test", null, null));
            Assert.Contains(page.List, u => u.Id == id);

            // 同租户重复 → 400
            await Assert.ThrowsAsync<AppException>(() =>
                svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Never, "{SEQ}")));

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public async Task 取号_格式渲染与递增()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{yyyy}{MM}{dd}-{SEQ}", 1, 9999);

            var no1 = await svc.NextSeqNoAsync(new GetNextSeqInput("test"));
            var today = DateTime.Now.ToString("yyyyMMdd");
            Assert.Equal($"{today}-0002", no1);   // 参照口径:Seq 初值 1(占位)+后置自增 → 首号 = Min+1;{SEQ} 按 Max(9999) 4 位零填充

            var no2 = await svc.NextSeqNoAsync(new GetNextSeqInput("test"));
            Assert.Equal($"{today}-0003", no2);

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public async Task 取号_超出上限拒绝()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 2);

            await svc.NextSeqNoAsync(new GetNextSeqInput("test"));   // Seq 1→2(未超)
            var ex = await Assert.ThrowsAsync<AppException>(() =>
                svc.NextSeqNoAsync(new GetNextSeqInput("test")));    // Seq 2→3 > Max=2
            Assert.Equal(400, ex.Code);

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public async Task 取号_跨日重置回最小值()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 9999);
            Assert.EndsWith("0002", await svc.NextSeqNoAsync(new GetNextSeqInput("test"))); // 首号 = Min+1(参照口径)

            // 手工把 Expy 拨回昨天(模拟跨日)
            await scope.Db.Updateable<SysSerial>()
                .SetColumns(u => u.Expy == DateTime.Now.Date.AddDays(-1))
                .Where(u => u.Id == id).ExecuteCommandAsync();

            Assert.EndsWith("0002", await svc.NextSeqNoAsync(new GetNextSeqInput("test"))); // 重置回 Min 再取(参照口径)

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public async Task 取号_停用或类型未建拒绝()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 9999);

            await svc.SetStatusAsync(id, 1);
            await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync(new GetNextSeqInput("test")));

            await CleanupSerialAsync(scope, id);
            await Assert.ThrowsAsync<AppException>(() => svc.NextSeqNoAsync(new GetNextSeqInput("test"))); // 已删
        }

        [Fact]
        public async Task 更新删除_只改配置列()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 9999);
            await svc.NextSeqNoAsync(new GetNextSeqInput("test"));   // Seq 1→2
            await svc.NextSeqNoAsync(new GetNextSeqInput("test"));   // Seq 2→3

            await svc.UpdateAsync(new UpdateSerialInput(id, "test", ResetIntervalEnum.Month, "{yyyy}{SEQ}", 1, 5555));
            var d = await svc.DetailAsync(id);
            Assert.Equal(5555, d.Max);
            Assert.Equal(ResetIntervalEnum.Month, d.ResetInterval);
            Assert.Equal(3, d.Seq);   // 运行时状态不受配置更新影响

            await svc.DeleteAsync(id);
            await Assert.ThrowsAsync<AppException>(() => svc.DetailAsync(id));
        }

        [Fact]
        public async Task 预览_不消耗序列()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 9999);

            var pv = svc.PreviewAsync(new PreviewSerialInput("{yyyy}{SEQ}", 7, 9999));
            Assert.Equal(DateTime.Now.Year.ToString() + "0007", pv);
            Assert.Equal(1, (await svc.DetailAsync(id)).Seq); // 预览不消耗序列(Seq 仍为初值 1)

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public void 插槽列表_默认集齐全()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var slots = svc.GetSlotList();
            foreach (var s in new[] { "{SEQ}", "{yyyy}", "{yy}", "{MM}", "{dd}", "{HH}", "{mm}", "{ss}", "{TenantId}", "{UserId}", "{OrgId}" })
            {
                Assert.Contains(s, slots);
            }
        }

        [Fact]
        public async Task 并发取号_锁串行且号不重复()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "{SEQ}", 1, 9999);

            var results = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => svc.NextSeqNoAsync(new GetNextSeqInput("test"))));
            Assert.Equal(4, results.Distinct().Count());   // 4 个不同号
            Assert.DoesNotContain("0000", results);        // 无越界

            await CleanupSerialAsync(scope, id);
        }

        [Fact]
        public async Task AOP_BindSerial插入自动填充_显式值不覆盖()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = GetService(scope);
            var id = await EnsureTestSerialAsync(scope, svc, "W{SEQ}", 1, 9999);

            // 空属性 → 自动填充
            await scope.Db.Insertable(new ProbeSerialEntity { OrderNo = null }).ExecuteCommandAsync();
            // 显式值 → 不覆盖
            await scope.Db.Insertable(new ProbeSerialEntity { OrderNo = "EXPLICIT-1" }).ExecuteCommandAsync();

            var rows = await scope.Db.Queryable<ProbeSerialEntity>().ToListAsync();
            Assert.Contains(rows, u => u.OrderNo == "W0002");   // 首号 = Min+1(参照口径)
            Assert.Contains(rows, u => u.OrderNo == "EXPLICIT-1");

            // 清理
            await scope.Db.Ado.ExecuteCommandAsync("TRUNCATE dsh_w_serial_probe");
            await CleanupSerialAsync(scope, id);
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static SysSerialService GetService(DbScope scope)
            => (SysSerialService)scope.Provider.GetRequiredService<SysSerialService>();

        /// <summary>幂等建 test 类型配置行(已存在则复用并重置状态,保证用例间隔离)。</summary>
        private static async Task<long> EnsureTestSerialAsync(DbScope scope, SysSerialService svc, string formater, long min, long max)
        {
            await scope.Db.Deleteable<SysSerial>().Where(u => u.Type == "test").ExecuteCommandAsync(); // 物理清残留(用例失败不留状态)
            return await svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Day, formater, min, max));
        }

        private static async Task CleanupSerialAsync(DbScope scope, long id)
        {
            await scope.Db.Deleteable<SysSerial>().Where(u => u.Id == id).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// [BindSerial] AOP 填充探针实体(批 W 3.4):表 dsh_w_serial_probe 手工 InitTables(测试程序集实体不入库建表扫描)。
    /// </summary>
    [SugarTable("dsh_w_serial_probe")]
    public class ProbeSerialEntity : EntityTenant
    {
        /// <summary>业务单号(绑定 test 类型流水号)。</summary>
        [BindSerial("test")]
        public string? OrderNo { get; set; }
    }
                sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

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
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysNotice).Assembly);
                services.AddSignalR();
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                TestSchemaBootstrap.Ensure(Db);
                Db.CodeFirst.InitTables(typeof(ProbeSerialEntity));   // 批 W 探针表(测试程序集实体不入库建表扫描)
                PgReachable = true;
            }

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
}
