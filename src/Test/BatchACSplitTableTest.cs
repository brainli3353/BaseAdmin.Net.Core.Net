using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AC ORM 分表(81 功能点 3.1 收尾)测试:真实 PostgreSQL(huluyeye_admin)。
/// 判定对象 = 分表建表(SqlSugarSetup.InitTables 的分表分支) + 仓储分表入口
/// (<c>InsertSplitAsync</c>/<c>AsSplitQueryable</c>) + 非分表实体的 fail-loud 边界,
/// 全部以 information_schema 的子表实物为证(逐子表 count 取证,而非只看框架返回)。
/// </summary>
/// <remarks>
/// WHY 探针实体表名带 {year}{month}{day} 模板(见 AcProbeEntities 的 WHY):SqlSugar 5.1.4.217 的分表
/// 子表名=模板占位符替换,裸表名调分表入口会直接抛「分表表名需要占位符」,建不出子表也无从取证。
/// WHY 每用例 finally 物理 DROP 全部 ac_split_probe% 子表:探针表按月生成,表名随运行时间变化,
/// 不清理会在真实库留下随时间增殖的残留表(清理按 information_schema 实测名逐个 DROP,幂等可重跑)。
/// WHY 建表走 <c>db.InitTables(typeof(AcSplitProbeEntity).Assembly)</c>:被测机制是生产建表入口
/// (SqlSugarSetup.InitTables 内按 [SplitTable] 分流到 CodeFirst.SplitTables()),不是框架外的手工建表。
/// PG 不可达时诚实降级(Assert.Fail,禁止冒充通过),口径同 BatchABTenantDbTest。
/// </remarks>
public class BatchACSplitTableTest
{
    private readonly ITestOutputHelper _output;

    public BatchACSplitTableTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 连接串照抄 BatchABTenantDbTest(开发库;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 探针表名前缀(子表=前缀 + 年月日,见 AcProbeEntities)
    private const string ProbeTablePrefix = "ac_split_probe";

    // 诚实降级文案(与 BatchABTenantDbTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // ─────────────────────────────────────────────────────────────
    // 1. 分表建表:生产建表入口应产出子表实物
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 分表建表_子表存在()
    {
        if (!await TcpReachableAsync(DbHost, DbPort))
        {
            Assert.Fail(Downgrade);
        }

        using var dbScope = new DbScope();
        var db = dbScope.Db;
        try
        {
            db.InitTables(typeof(AcSplitProbeEntity).Assembly);

            var tables = await ListSplitTablesAsync(db);
            Assert.True(tables.Count >= 1,
                $"分表建表后 information_schema 应命中至少 1 张 ac_split_probe% 子表,实际 {tables.Count} 张");
            _output.WriteLine($"分表建表实测子表({tables.Count} 张):{string.Join(", ", tables)}");
        }
        finally
        {
            await DropSplitTablesAsync(db);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 分表插入与查询:InsertSplitAsync 落子表、AsSplitQueryable 查得回
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 分表插入与查询_命中()
    {
        if (!await TcpReachableAsync(DbHost, DbPort))
        {
            Assert.Fail(Downgrade);
        }

        using var dbScope = new DbScope();
        using var diScope = dbScope.Provider.CreateScope();
        var db = dbScope.Db;
        // 仓储由容器解析(Scoped,与生产调用路径一致),而非手工 new
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<AcSplitProbeEntity>>();
        try
        {
            db.InitTables(typeof(AcSplitProbeEntity).Assembly);

            var tag = NewTag("ac-split");
            var entity = new AcSplitProbeEntity { Name = tag, SplitTime = DateTime.Now };
            var rows = await repo.InsertSplitAsync(entity);
            Assert.Equal(1, rows);
            // 主键应由 AOP(DataExecuting)自动填雪花——分表插入路径亦须经 AOP(否则 PK 空)
            Assert.True(entity.Id > 0, $"分表插入应自动填雪花主键,实际 Id={entity.Id}");

            var hit = await repo.AsSplitQueryable().Where(e => e.Name == tag).CountAsync();
            Assert.Equal(1, hit);

            // 独立取证:逐子表 count,确认该行物理落在哪张子表(而非只看联合查询的返回值)
            var evidence = await PerTableEvidenceAsync(db, tag);
            _output.WriteLine($"插入查询判定:InsertSplitAsync={rows} 行,AsSplitQueryable 命中 {hit} 行;逐子表取证:{evidence}");
            Assert.Contains("=1", evidence); // 至少一张子表命中该行
        }
        finally
        {
            await DropSplitTablesAsync(db);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 跨月分表:两个月各一行,两个子表各持一行
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 跨月分表_两个子表各一行()
    {
        if (!await TcpReachableAsync(DbHost, DbPort))
        {
            Assert.Fail(Downgrade);
        }

        using var dbScope = new DbScope();
        using var diScope = dbScope.Provider.CreateScope();
        var db = dbScope.Db;
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<AcSplitProbeEntity>>();
        try
        {
            db.InitTables(typeof(AcSplitProbeEntity).Assembly);

            var thisMonth = DateTime.Now;
            var lastMonth = thisMonth.AddMonths(-1);
            var tagThis = NewTag("ac-this");
            var tagLast = NewTag("ac-last");

            // 本月行:建表已产出本月子表;上月行:子表不存在 → 由 SplitInsertable 按需建表(见报告实测)
            Assert.Equal(1, await repo.InsertSplitAsync(new AcSplitProbeEntity { Name = tagThis, SplitTime = thisMonth }));
            Assert.Equal(1, await repo.InsertSplitAsync(new AcSplitProbeEntity { Name = tagLast, SplitTime = lastMonth }));

            var rows = await repo.AsSplitQueryable()
                .Where(e => e.Name == tagThis || e.Name == tagLast)
                .ToListAsync();
            Assert.Equal(2, rows.Count);

            var tables = await ListSplitTablesAsync(db);
            Assert.True(tables.Count >= 2, $"跨月分表应产生 ≥2 张子表,实际 {tables.Count} 张:{string.Join(", ", tables)}");

            // 子表命名口径(反编译实测):Month 粒度下 day 恒 01 → 前缀_yyyyMM01
            var expectThis = $"{ProbeTablePrefix}_{thisMonth:yyyyMM}01";
            var expectLast = $"{ProbeTablePrefix}_{lastMonth:yyyyMM}01";
            Assert.Contains(expectThis, tables);
            Assert.Contains(expectLast, tables);

            // 逐子表取证:各持一行(证「按 SplitField 值路由到对应月子表」)
            Assert.Equal(1, await CountInTableAsync(db, expectThis, tagThis));
            Assert.Equal(0, await CountInTableAsync(db, expectThis, tagLast));
            Assert.Equal(1, await CountInTableAsync(db, expectLast, tagLast));
            Assert.Equal(0, await CountInTableAsync(db, expectLast, tagThis));
            _output.WriteLine($"跨月判定:本月 {expectThis}=1(本月行),上月 {expectLast}=1(上月行);子表清单:{string.Join(", ", tables)}");
        }
        finally
        {
            await DropSplitTablesAsync(db);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 非分表实体调分表入口:SqlSugar 明确拒绝(能力边界不静默退化)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 非分表实体调用分表入口_明确失败()
    {
        if (!await TcpReachableAsync(DbHost, DbPort))
        {
            Assert.Fail(Downgrade);
        }

        using var dbScope = new DbScope();
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<SysUser>>();

        // 断言点:非分表实体(SysUser 无 [SplitTable])调分表入口 → 抛异常,不静默退化成单表查询
        var ex = Assert.ThrowsAny<Exception>(() => (object)repo.AsSplitQueryable());
        _output.WriteLine($"非分表入口拒绝:类型={ex.GetType().FullName};消息={ex.Message.Replace(Environment.NewLine, " / ")}");
        // 拒绝来自 SqlSugar 的表名校验(表名缺 {year} 占位符即非分表实体),文案含 {year} 可稳定检索
        Assert.Contains("{year}", ex.Message);
        // 仓储侧能力探针应与特性一致(非分表实体=false)
        Assert.False(SqlSugarRepository<SysUser>.IsSplitTableEntity);
        Assert.True(SqlSugarRepository<AcSplitProbeEntity>.IsSplitTableEntity);
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 清理:物理 DROP 后库内零残留(本次探针表不留任何残骸)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 清理_物理零残留()
    {
        if (!await TcpReachableAsync(DbHost, DbPort))
        {
            Assert.Fail(Downgrade);
        }

        using var dbScope = new DbScope();
        var db = dbScope.Db;

        // 前置:清掉任何历史残留(前次运行被强杀等),使本用例自证不依赖用例执行顺序
        var residueBefore = await DropSplitTablesAsync(db);
        if (residueBefore > 0)
        {
            _output.WriteLine($"注意:进入清理用例时发现 {residueBefore} 张历史残留子表(前次运行未善终),已清除");
        }

        try
        {
            db.InitTables(typeof(AcSplitProbeEntity).Assembly);
            var created = await ListSplitTablesAsync(db);
            Assert.True(created.Count >= 1, "清理用例前置:应先建出子表实物,否则「零残留」无从证起");

            var dropped = await DropSplitTablesAsync(db);
            Assert.True(dropped >= created.Count, $"应 DROP 掉全部 {created.Count} 张子表,实际 {dropped}");

            var after = await ListSplitTablesAsync(db);
            Assert.Empty(after);
            _output.WriteLine($"清理判定:建 {created.Count} 张 → DROP {dropped} 张 → 残留 {after.Count} 张(零残留)");
        }
        finally
        {
            await DropSplitTablesAsync(db);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助:探针表清单 / 清理 / 取证 / 探测 / 容器
    // ─────────────────────────────────────────────────────────────

    /// <summary>用例唯一探针标记(防与其它用例/残留行串读)。</summary>
    private static string NewTag(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    /// <summary>当前 schema 内全部探针子表名(按名排序;用于清单打印与清理)。</summary>
    private static async Task<List<string>> ListSplitTablesAsync(ISqlSugarClient db)
        => await db.Ado.SqlQueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_name LIKE @p ORDER BY table_name",
            new SugarParameter("@p", ProbeTablePrefix + "%"));

    /// <summary>
    /// 物理 DROP 全部探针子表(幂等;返回实际 DROP 掉的张数)。
    /// 表名取自 information_schema 实测值(非外部输入),双引号精确大小写引用。
    /// </summary>
    private static async Task<int> DropSplitTablesAsync(ISqlSugarClient db)
    {
        var tables = await ListSplitTablesAsync(db);
        foreach (var table in tables)
        {
            await db.Ado.ExecuteCommandAsync($"DROP TABLE IF EXISTS \"{table}\"");
        }

        return tables.Count;
    }

    /// <summary>单张子表内按 Name 精确匹配的行数(独立取证:绕开框架的联合查询,直查子表实物)。</summary>
    private static async Task<int> CountInTableAsync(ISqlSugarClient db, string table, string name)
    {
        var rows = await db.Ado.SqlQueryAsync<int>(
            $"SELECT count(*) FROM \"{table}\" WHERE \"Name\" = @n",
            new SugarParameter("@n", name));
        return rows.Count > 0 ? rows[0] : 0;
    }

    /// <summary>逐子表取证串(形如 <c>ac_split_probe_20260901=1, ac_split_probe_20260801=0</c>)。</summary>
    private static async Task<string> PerTableEvidenceAsync(ISqlSugarClient db, string name)
    {
        var parts = new List<string>();
        foreach (var table in await ListSplitTablesAsync(db))
        {
            parts.Add($"{table}={await CountInTableAsync(db, table, name)}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,口径同 BatchABTenantDbTest)。</summary>
    private static async Task<bool> TcpReachableAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(2000));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径(雪花/多库/逐连接 AOP) + AddAutoServices(Core 程序集)。
    /// 改自 BatchABTenantDbTest.DbScope,差异 = 去掉租户库/加密开关(本批判定不涉及),也不预建框架表:
    /// 探针表由各用例显式经生产建表入口 InitTables 建(建表动作本身就是被测对象)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public DbScope()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥/令牌段(照抄 BatchABTenantDbTest):AddAutoServices 装配 [Service] 时需要
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                    ["Jwt:Secret"] = "BaseAdminCoreTestJwtSecret0123456789abcdef0123456789abcdef",
                    ["Jwt:Issuer"] = "huluyeye-admin",
                    ["Jwt:Audience"] = "huluyeye-admin-api"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            // WHY 必须有缓存实现:插入路径 AOP 会经 GetService 解析流水号服务(SysSerialService 依赖 ICacheService),
            // 容器缺缓存实现时该解析直接抛 InvalidOperationException(见报告「生产代码疑问」);生产宿主必注册缓存(与 BatchAB 同口径)
            services.AddCache(config);
            services.AddAutoServices(typeof(SqlSugarSetup).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }
    }
}