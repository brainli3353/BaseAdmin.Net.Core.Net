using System.Globalization;
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.Database;
using AdminCore.Core.Services.Database.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AD「数据库管理只读(3.8)」测试:被测主体 = <see cref="SysDatabaseService"/> 三只读查询
/// (<see cref="SysDatabaseService.ListTablesAsync"/> / <see cref="SysDatabaseService.ListColumnsAsync"/> /
/// <see cref="SysDatabaseService.ListIndexesAsync"/>)。
/// </summary>
/// <remarks>
/// WHY 全部用例需真实 PG:三个方法都是 pg_catalog/information_schema 原生 SQL,断言的正是
/// "SQL 语义 × 实际 schema" 的合成结果(排序、注释、主键标记、非法入参的 fail-loud)——
/// 内存替身只能验证"我喂了什么就返回什么",证明不了目录查询写对了。PG 不可达时 Assert.Fail 显式降级(禁止冒充通过)。
/// WHY 断言"以实际数据为准"不写死表名排序:大小随运行期写入变化,写死序即脆弱测试;
/// 故排序断言 = ①pg_size_pretty 单位感知解析后非递增(弱断言) ②与独立原生 SQL 的序**逐名相同**(强断言)。
/// 用例零污染:本批只读,不写库、不建表、不改 schema(缺表由容器 InitTables 幂等增量补齐)。
/// </remarks>
public class BatchADDatabaseTest
{
    private readonly ITestOutputHelper _output;

    public BatchADDatabaseTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchABTenantDbTest/BatchCTenantTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchABTenantDbTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // 本仓框架用户表(环境实证存在;不用它做排序断言——排序以运行期实际大小为准)
    private const string KnownTable = "sys_user";

    // ─────────────────────────────────────────────────────────────
    // 1. 表清单:非空 + 含已知表 + 按占用大小倒序 + 每行字段齐备
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 表清单_含已知表且按大小倒序()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();
        var tables = await svc.ListTablesAsync(null);

        Assert.NotEmpty(tables);
        Assert.Contains(tables, t => t.TableName == KnownTable);

        // 每行字段齐备:TotalSize 非空(人类可读串)、IndexCount 非负、TableName 非空
        foreach (var t in tables)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.TableName), "TableName 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(t.TotalSize), $"{t.TableName} 的 TotalSize 不应为空");
            Assert.True(t.IndexCount >= 0, $"{t.TableName} 的 IndexCount 应非负,实际 {t.IndexCount}");
            Assert.True(t.RowEstimate >= 0, $"{t.TableName} 的 RowEstimate 应非负,实际 {t.RowEstimate}");
        }

        // 弱断言(单位感知):TotalSize 是 pg_size_pretty 串("8192 bytes"/"1616 kB"/"16 MB"),
        // 直接字符串比较跨单位即错 → 解析成字节后逐对判非递增。
        var sizes = tables.Select(t => ParsePgSize(t.TotalSize)).ToList();
        for (var i = 1; i < sizes.Count; i++)
        {
            Assert.True(sizes[i] <= sizes[i - 1],
                $"表清单应按 TotalSize 降序:{tables[i - 1].TableName}({tables[i - 1].TotalSize}) < {tables[i].TableName}({tables[i].TotalSize})");
        }

        // 强断言:与独立原生 SQL(pg_total_relation_size 排序,含同名次 tie-break)逐名比对。
        // WHY 用原生 SQL 复核而非只信弱断言:pg_size_pretty 是**取整**后的字符串,弱断言漏得掉"取整后相等的乱序"。
        var rawOrder = await dbScope.Db.Ado.SqlQueryAsync<string>(
            """
            SELECT c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema()
               AND c.relkind = 'r'
             ORDER BY pg_total_relation_size(c.oid) DESC, c.relname
            """);
        Assert.Equal(rawOrder, tables.Select(t => t.TableName).ToList());

        _output.WriteLine($"判定:{tables.Count} 张表,首行={tables[0].TableName}({tables[0].TotalSize}),与原生 SQL 序逐名一致");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 表清单:keyword 按表名/注释模糊过滤(大小写不敏感);无匹配 → 空集
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 表清单_关键字过滤()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();

        var hit = await svc.ListTablesAsync(KnownTable);
        Assert.NotEmpty(hit);
        Assert.Contains(hit, t => t.TableName == KnownTable);

        // 每行必须"真的含关键字"(表名或注释,大小写不敏感)——防过滤条件被旁路成全量返回
        foreach (var t in hit)
        {
            var matched = t.TableName.Contains(KnownTable, StringComparison.OrdinalIgnoreCase)
                || (t.Comment?.Contains(KnownTable, StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.True(matched, $"行 {t.TableName}(注释 {t.Comment})不含关键字 {KnownTable},过滤条件疑似失效");
        }

        // 大小写不敏感(ILIKE 口径):同一关键字大写命中集合一致
        var upper = await svc.ListTablesAsync(KnownTable.ToUpperInvariant());
        Assert.Equal(hit.Select(t => t.TableName).ToList(), upper.Select(t => t.TableName).ToList());

        // 注释命中路径:sys_user 的表注释("系统用户",环境实证)也参与匹配 → 结果含该表
        var byComment = await svc.ListTablesAsync("系统用户");
        Assert.Contains(byComment, t => t.TableName == KnownTable);

        // 无匹配关键字 → 空集(不是全量、也不是异常)
        var none = await svc.ListTablesAsync("不存在的表名xyz");
        Assert.Empty(none);

        _output.WriteLine($"判定:keyword={KnownTable} 命中 {hit.Count} 张;大写同集合;注释命中含 {KnownTable};无匹配=0");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 列结构:字段齐备 + 主键标记正确 + 序位
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 列结构_字段齐备且主键标记正确()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();
        var columns = await svc.ListColumnsAsync(KnownTable);

        Assert.NotEmpty(columns);

        // 每行 DataType/ColumnName 齐备、序位从 1 起且严格递增(实现按 attnum 排序)
        foreach (var c in columns)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.ColumnName), "ColumnName 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(c.DataType), $"{c.ColumnName} 的 DataType 不应为空");
            Assert.True(c.OrdinalPosition >= 1, $"{c.ColumnName} 的 OrdinalPosition 应 >=1,实际 {c.OrdinalPosition}");
        }
        var positions = columns.Select(c => c.OrdinalPosition).ToList();
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] > positions[i - 1], "列结构应按 OrdinalPosition 升序且无重复");
        }

        // 业务列 Account:非空 + 有注释 + 字符型(环境实证:varchar(64) 非空、注释"账号")
        var account = columns.Find(c => c.ColumnName == "Account");
        Assert.NotNull(account);
        Assert.False(account!.IsNullable, "sys_user.Account 应为非空列");
        Assert.False(string.IsNullOrWhiteSpace(account.Comment), "sys_user.Account 应有列注释");
        Assert.Contains("character varying", account.DataType);
        Assert.Equal(64, account.MaxLength);
        Assert.False(account.IsPrimaryKey);

        // 主键列 id:IsPrimaryKey=true + 序位 1(本仓实体声明口径:主键列小写 id 且首列)
        var id = columns.Find(c => c.ColumnName == "id");
        Assert.NotNull(id);
        Assert.True(id!.IsPrimaryKey, "sys_user.id 应被标记为主键列");
        Assert.Equal(1, id.OrdinalPosition);
        Assert.False(id.IsNullable, "主键列不应可空");

        // 主键列唯一性:仅一列被标记(单列主键)
        Assert.Single(columns, c => c.IsPrimaryKey);

        _output.WriteLine($"判定:{columns.Count} 列;Account 非空+注释\"{account.Comment}\";id 主键序位={id.OrdinalPosition}");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 列结构:表不存在 → 业务异常(不返回空集误导)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 列结构_表不存在_业务异常()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();

        // 前置:该表确实不存在(否则用例退化为"错误断言")
        var exists = await dbScope.Db.Ado.SqlQueryAsync<int>(
            """
            SELECT COUNT(*)::int FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema() AND c.relname = @t
            """,
            new SugarParameter("@t", "no_such_table_ad"));
        Assert.Equal(0, exists[0]);

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ListColumnsAsync("no_such_table_ad"));
        Assert.Equal(ErrorCodes.DatabaseTableNotFound, ex.ErrorCode); // 批 AR-5 口径

        _output.WriteLine($"判定:文案={ex.Message}");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 索引清单:含主键索引 + IsPrimary 行排在前面
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 索引清单_主键索引在前()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();
        var indexes = await svc.ListIndexesAsync(KnownTable);

        Assert.NotEmpty(indexes);
        var pkey = indexes.Find(i => i.IndexName == KnownTable + "_pkey");
        Assert.NotNull(pkey);
        Assert.True(pkey!.IsPrimary, "sys_user_pkey 应被标记为主键索引");
        Assert.True(pkey.IsUnique, "主键索引必然唯一");
        // 定义原文取自 pg_get_indexdef:PG 的主键索引呈现为 "CREATE UNIQUE INDEX <name> ON <table> ..."(无 PRIMARY KEY 字样)
        Assert.Contains("UNIQUE INDEX", pkey.Definition);
        Assert.Contains(KnownTable, pkey.Definition);
        foreach (var i in indexes)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.IndexName), "IndexName 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(i.Definition), $"{i.IndexName} 的 Definition 不应为空");
        }

        // "主键索引在前"的排序分支:只有"同时含主键与非主键索引"的表才验得出(sys_user 仅主键索引时该断言恒真)
        var (mixedTable, mixed) = await FirstMixedIndexTableAsync(svc);
        var lastPrimary = mixed.FindLastIndex(i => i.IsPrimary);
        var firstNonPrimary = mixed.FindIndex(i => !i.IsPrimary);
        Assert.True(lastPrimary >= 0, $"{mixedTable} 应有主键索引");
        Assert.True(firstNonPrimary > lastPrimary,
            $"{mixedTable} 索引清单应主键索引在前:首个非主键索引位于 {firstNonPrimary},末个主键索引位于 {lastPrimary}");

        _output.WriteLine($"判定:sys_user 索引 {indexes.Count} 个(含 sys_user_pkey);排序分支取证表={mixedTable}(主键 {mixed.Count(i => i.IsPrimary)}/非主键 {mixed.Count(i => !i.IsPrimary)})");
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 索引清单:表不存在 → 业务异常(与"表无索引返回空集"区分)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 索引清单_表不存在_业务异常()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();

        var ex = await Assert.ThrowsAsync<AppException>(() => svc.ListIndexesAsync("no_such_table_ad"));
        Assert.Equal(ErrorCodes.DatabaseTableNotFound, ex.ErrorCode); // 批 AR-5 口径

        // 反向取证:存在的表**不抛**(证上面的异常来自"表不存在"判定,而非"一律抛")
        var real = await svc.ListIndexesAsync(KnownTable);
        Assert.NotNull(real); // 空集也是合法结果(实现注释口径),此处只要求不抛
        _output.WriteLine($"判定:文案={ex.Message};存在的表 {KnownTable} 不抛(返回 {real.Count} 个索引)");
    }

    // ─────────────────────────────────────────────────────────────
    // 7. 表名入参校验:空串/null → 业务异常(消息含"表名")
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 表名入参校验()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = dbScope.Provider.GetRequiredService<SysDatabaseService>();

        // null:传 null! 以显式表达"以 null 入参探测校验分支"(参数声明为非空串)
        var nullEx = await Assert.ThrowsAsync<AppException>(() => svc.ListColumnsAsync(null!));
        Assert.Equal(ErrorCodes.DatabaseTableRequired, nullEx.ErrorCode);

        var blankEx = await Assert.ThrowsAsync<AppException>(() => svc.ListColumnsAsync(""));
        Assert.Equal(ErrorCodes.DatabaseTableRequired, blankEx.ErrorCode);

        // 纯空白与超长同样拦在入参校验(不落到 SQL 层)
        var spaceEx = await Assert.ThrowsAsync<AppException>(() => svc.ListColumnsAsync("   "));
        Assert.Equal(ErrorCodes.DatabaseTableRequired, spaceEx.ErrorCode);

        var longEx = await Assert.ThrowsAsync<AppException>(() => svc.ListColumnsAsync(new string('a', 129)));
        Assert.Equal(ErrorCodes.DatabaseTableNameTooLong, longEx.ErrorCode);

        // ListIndexesAsync 共用同一入参校验单点
        var idxEx = await Assert.ThrowsAsync<AppException>(() => svc.ListIndexesAsync(null!));
        Assert.Equal(ErrorCodes.DatabaseTableRequired, idxEx.ErrorCode);

        _output.WriteLine($"判定:null/空/空白/超长 均抛 AppException:{nullEx.Message} / {longEx.Message}");
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 取第一张"同时含主键索引与非主键索引"的表(候选来自 Core 实体 [SugarIndex] 生成的次要索引,CodeFirst 建表即存在)。
    /// WHY 必须有这张表:"主键索引在前"的排序断言在单索引表上恒真 = 永真断言;候选全不满足时**显式失败**,
    /// 宁可红也不静默把断言弱化成空壳。
    /// </summary>
    private static async Task<(string Table, List<DbIndexOutput> Indexes)> FirstMixedIndexTableAsync(SysDatabaseService svc)
    {
        foreach (var table in new[] { "sys_open_access", "sys_log_event", "sys_notice", "sys_serial", "sys_column_custom" })
        {
            List<DbIndexOutput> indexes;
            try
            {
                indexes = await svc.ListIndexesAsync(table);
            }
            catch (AppException)
            {
                continue; // 该表在本库尚未建出 → 换候选
            }

            if (indexes.Any(i => i.IsPrimary) && indexes.Any(i => !i.IsPrimary))
            {
                return (table, indexes);
            }
        }

        Assert.Fail("候选表均无「主键 + 非主键」混合索引 → 「主键索引在前」排序分支无法真验证(禁止弱化成永真断言)");
        return default;
    }

    /// <summary>
    /// 解析 pg_size_pretty 输出("8192 bytes"/"1616 kB"/"16 MB"/"1.2 GB")为字节数:
    /// 带单位的字符串无法直接比大小,必须先归一到同一单位才能验"按大小倒序"。
    /// </summary>
    private static double ParsePgSize(string pretty)
    {
        var parts = pretty.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 1, $"pg_size_pretty 输出无法解析:{pretty}");

        var value = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var unit = parts.Length > 1 ? parts[1] : "bytes";
        var factor = unit switch
        {
            "bytes" => 1d,
            "kB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => throw new InvalidOperationException($"未知 pg_size_pretty 单位 {unit}(输入 {pretty})")
        };
        return value * factor;
    }

    /// <summary>
    /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集,解析 [Service] 的
    /// SysDatabaseService),InitTables 扫 Core 程序集增量建表(幂等,收益是"刚 clone 的库也有表可查")。
    /// 改写自 BatchABTenantDbTest.DbScope;PG 不可达时标记降级(用例侧 Assert.Fail)。
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
            services.AddCache(config);
            services.AddAutoServices(typeof(SysDatabaseService).Assembly); // [Service] 自动发现(与 Program.cs 同口径)
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

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死,同 BatchABTenantDbTest 口径)。</summary>
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