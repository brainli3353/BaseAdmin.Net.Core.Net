using System.Net.Sockets;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.SeedData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 种子执行语义回归(2026-10-30 上游缺陷《种子冲突语义缺陷》第一步验收件):
/// 种子执行＝**按主键缺行插入、已有跳过**——种子只是初始引导,不得再 upsert 覆写库内既有行。
/// <para>背景:旧语义每次启动按主键整行 upsert,把运行期/租户的合法修改静默回滚(下游实证 sys_otp_enabled
/// 被部署重启覆回 True)。正确职责切分:种子＝初始引导;结构演进＝版本化升级件(第二步,计划书见
/// docs/业务/规划/计划_版本化升级件机制_2026-10-30.md)。</para>
/// <para>覆盖:① 真实菜单种子行(10001)被运行期改名后,重启种子路径不覆写;
/// ② 通用路径(测试程序集内探针种子,落真实 sys_menu 表):缺行插入 → 已有跳过 → 行缺失再补插。</para>
/// <para>真实 PG(与 BatchAS/BatchD 同 DbScope 口径);PG 不可达一律 Assert.Fail 显式降级,禁止冒充通过。
/// 零污染:①的改动在 finally 逐字复还并断言已复还;②的探针行在 finally 物理删除并断言残留=0。</para>
/// </summary>
public class BatchSeedInsertOnlyTest
{
    private const string Downgrade = "降级:PostgreSQL 127.0.0.1:5432 不可达——本用例未实跑,禁止冒充通过";

    /// <summary>SysMenuSeedData 顶级目录「系统管理」行 Id(种子 Title=系统管理)。</summary>
    private const long RealSeedMenuId = 10001;

    private const string RuntimeTitle = "运行时改名的菜单(种子执行语义回归探针)";

    /// <summary>实体程序集(与 Program.cs/StartupSchemaInitializer 同口径传 Core + Business)。</summary>
    private static Assembly[] EntityAssemblies { get; } =
        [typeof(SqlSugarSetup).Assembly, typeof(AdminCore.Business.Class1).Assembly];

    // ── ① 真实菜单种子行:重启(再跑种子)不得覆写运行期修改 ─────────────────────────────────
    [Fact]
    public async Task 菜单种子_重启后运行时修改不被覆写()
    {
        await using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var db = scope.Db;
        var before = await QueryRealSeedMenuAsync(db);
        Assert.NotNull(before);

        try
        {
            // 模拟管理端在运行期改菜单标题(菜单管理口可编辑,行带 UpdateTime)
            await db.Updateable(new SysMenu { Id = RealSeedMenuId, Title = RuntimeTitle })
                .UpdateColumns(m => m.Title)
                .ExecuteCommandAsync();
            Assert.Equal(RuntimeTitle, (await QueryRealSeedMenuAsync(db)).Title);

            // 模拟重启:与每次应用启动同一条种子路径(InitSeedData 扫全部 [IncreSeed] 数据种子)
            db.InitSeedData(EntityAssemblies);

            // 旧 upsert 语义会把 Title 覆回种子值「系统管理」;新语义必须原样保留运行期修改
            var after = await QueryRealSeedMenuAsync(db);
            Assert.Equal(RuntimeTitle, after.Title);
            Assert.Equal(before.Pid, after.Pid);   // 未被覆写的旁证(种子行其他列也一字未动)
            Assert.Equal(before.Name, after.Name);
        }
        finally
        {
            await db.Updateable(new SysMenu { Id = RealSeedMenuId, Title = before.Title })
                .UpdateColumns(m => m.Title)
                .ExecuteCommandAsync();
        }

        var restored = await QueryRealSeedMenuAsync(db);
        Assert.Equal(before.Title, restored.Title);
    }

    // ── ② 通用路径:缺行插入、已有跳过、行缺失再补插(探针种子,零污染) ─────────────────────────
    [Fact]
    public async Task 种子执行_缺行插入_已有跳过()
    {
        await using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var db = scope.Db;

        // 预清:上一轮中断可能留下的探针行(物理删,避免唯一索引撞车)
        DeleteProbeRow(db);
        try
        {
            // ① 缺行 ⇒ 插入
            db.InitSeedData(typeof(BatchSeedInsertOnlyTest).Assembly);
            var inserted = await QueryProbeAsync(db);
            Assert.NotNull(inserted);
            Assert.Equal(BatchSeedInsertOnlyProbeSeed.SeedTitle, inserted.Title);

            // ② 已有(被运行期改名)⇒ 跳过,不覆写
            await db.Updateable(new SysMenu { Id = BatchSeedInsertOnlyProbeSeed.ProbeId, Title = RuntimeTitle })
                .UpdateColumns(m => m.Title)
                .ExecuteCommandAsync();
            db.InitSeedData(typeof(BatchSeedInsertOnlyTest).Assembly);
            var skipped = await QueryProbeAsync(db);
            Assert.NotNull(skipped);
            Assert.Equal(RuntimeTitle, skipped.Title); // 旧 upsert 会覆回 SeedTitle

            // ③ 行缺失(物理删后)⇒ 再跑种子补回(初始引导语义仍成立)
            DeleteProbeRow(db);
            db.InitSeedData(typeof(BatchSeedInsertOnlyTest).Assembly);
            var reinserted = await QueryProbeAsync(db);
            Assert.NotNull(reinserted);
            Assert.Equal(BatchSeedInsertOnlyProbeSeed.SeedTitle, reinserted.Title);
        }
        finally
        {
            DeleteProbeRow(db);
        }

        Assert.Null(await QueryProbeAsync(db));
    }

    // ── 探针种子:落真实 sys_menu 表(现成表,不另造 DDL),显式 Id + 默认租户 ──────────────────
    [IncreSeed]
    [SeedData(99)] // 仅测试程序集内扫描时执行(生产实体程序集不含本类)
    public sealed class BatchSeedInsertOnlyProbeSeed : ISqlSugarEntitySeedData<SysMenu>
    {
        public const long ProbeId = 99_000_000_009_01;
        public const string SeedTitle = "种子探针项(初始引导值)";

        public IEnumerable<SysMenu> HasData()
        {
            return new[]
            {
                new SysMenu
                {
                    Id = ProbeId,
                    Pid = 0,
                    Name = "seedProbe",
                    Title = SeedTitle,
                    Type = 3,
                    OrderNo = 99,
                    TenantId = SqlSugarConst.DefaultTenantId
                }
            };
        }
    }

    private static Task<SysMenu> QueryRealSeedMenuAsync(ISqlSugarClient db)
        => db.Queryable<SysMenu>().ClearFilter().FirstAsync(m => m.Id == RealSeedMenuId);

    private static Task<SysMenu> QueryProbeAsync(ISqlSugarClient db)
        => db.Queryable<SysMenu>().ClearFilter().FirstAsync(m => m.Id == BatchSeedInsertOnlyProbeSeed.ProbeId);

    /// <summary>物理删探针行(种子行是物理行,软删会留唯一键残留;故走 Ado 原生 SQL 物理 DELETE)。</summary>
    private static void DeleteProbeRow(ISqlSugarClient db)
        => db.Ado.ExecuteCommand(
            "DELETE FROM sys_menu WHERE id = @id",
            new SugarParameter("@id", BatchSeedInsertOnlyProbeSeed.ProbeId));

    /// <summary>DB 容器(与生产同一条 AddSqlSugar 路径;同 BatchAS/BatchD 的 DbScope 口径)。</summary>
    private sealed class DbScope : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public ISqlSugarClient Db { get; }

        public bool PgReachable { get; }

        public DbScope()
        {
            if (!IsPostgresReachable())
            {
                _provider = null!;
                Db = null!;
                return;
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = TestPg.ConnectionString,
                    ["Yit:WorkerId"] = "1"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddCache(config);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;
        }

        public ValueTask DisposeAsync()
        {
            (Db as IDisposable)?.Dispose();
            _provider?.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死;同 BatchAS 口径)。</summary>
        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
