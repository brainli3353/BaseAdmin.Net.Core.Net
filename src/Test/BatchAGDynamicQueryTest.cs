using System.Net.Sockets;
using System.Reflection;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AG 3.11「动态查询防注入(白名单式)」测试:被测主体 = <see cref="DynamicQueryExtensions.ApplyDynamic{T}"/>
/// ——排序/搜索**字段名经实体元数据白名单校验**、方向由 bool 枚举化、关键字只作**参数值**参与 LIKE。
/// </summary>
/// <remarks>
/// WHY 全部用例需真实 PG:本能力的断言对象是"用户输入 → SQL 语义"的合成结果——
/// 排序是否真的生效(序对不对)、非法字段/注入串是否真的被拦(且**表还在**)、关键字是否真的参数化模糊匹配,
/// 只有真库真表验得出;内存替身只能证明"我喂了什么就返回什么",对注入面零证明力。
/// PG 不可达时按既有口径 Assert.Fail 显式降级(禁止冒充通过,同 BatchADDatabaseTest)。
/// <para>
/// 用例零污染:全程只读(不 Insert/Update/Delete,不建表),唯一的"写入嫌疑"是注入串本身——
/// 故用例 5 在断言异常之后**再查一次表是否还在 / 是否仍可查**(防"注入其实成功、异常只是恰好也抛了"的假绿)。
/// </para>
/// <para>
/// 敏感列口径:Password 只用于"证明该列可被搜索",投影仅取 Id,**任何断言消息/输出都不含密码值**。
/// </para>
/// </remarks>
public class BatchAGDynamicQueryTest
{
    private readonly ITestOutputHelper _output;

    public BatchAGDynamicQueryTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchADDatabaseTest/BatchABTenantDbTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchADDatabaseTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // 环境实证(本次测试前用 psql 核实):sys_user 共 186 行,其中 "IsDelete"=false 170 行;
    // Account/RealName 中模糊含 "admin" 的恰 1 行(Account=admin)
    private const string KnownTable = "sys_user";

    // ─────────────────────────────────────────────────────────────
    // 1. 合法排序:方向生效(严格单调)+ 大小写不敏感的列名等价
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "合法排序_生效")]
    public async Task 合法排序_生效()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var desc = await OrderedIdsAsync(scope, "Id", desc: true);
        var asc = await OrderedIdsAsync(scope, "Id", desc: false);

        // 全量(软删过滤后的存续行)而非取样:样本 5 行验不出"整体序"
        Assert.True(desc.Count >= 2, $"需至少 2 行才能验排序方向,实际 {desc.Count} 行");
        for (var i = 1; i < desc.Count; i++)
        {
            Assert.True(desc[i] < desc[i - 1], $"desc:true 应严格降序,但第 {i} 项 {desc[i]} >= 前项 {desc[i - 1]}");
        }

        for (var i = 1; i < asc.Count; i++)
        {
            Assert.True(asc[i] > asc[i - 1], $"desc:false 应严格升序,但第 {i} 项 {asc[i]} <= 前项 {asc[i - 1]}");
        }

        // 同一批行的正反序应互为镜像(证明两方向作用在同一集合上,不是"各查了一批")
        Assert.Equal(desc, Enumerable.Reverse(asc).ToList());
        Assert.NotEqual(desc[0], desc[^1]); // 防"全部相等"这类永真序

        // 大小写不敏感(白名单字典 OrdinalIgnoreCase):"Id"/"id"/"ID" 三种写法结果逐项相同
        var lower = await OrderedIdsAsync(scope, "id", desc: true);
        var upper = await OrderedIdsAsync(scope, "ID", desc: true);
        Assert.Equal(desc, lower);
        Assert.Equal(desc, upper);

        // 独立复核(原生 SQL 不经 SqlSugar 表达式路径):首项即 MAX(id),且逐项同名次
        var raw = await scope.Db.Ado.SqlQueryAsync<long>(
            $"""SELECT id FROM {KnownTable} WHERE "IsDelete" = false ORDER BY id DESC""");
        Assert.Equal(raw, desc);

        _output.WriteLine($"判定:{desc.Count} 行降序/升序严格单调且互为镜像;Id/id/ID 三写法结果一致;与原生 SQL 序逐项相同,MAX(id)={desc[0]}");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 合法搜索:多字段 OR 参数化模糊(命中行必含关键字),Password 可搜但不返回
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "合法搜索_参数化模糊")]
    public async Task 合法搜索_参数化模糊()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var hits = await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput("account,realName", null, false), "admin")
            .Select(u => new { u.Id, u.Account, u.RealName })
            .ToListAsync();

        Assert.NotEmpty(hits);
        foreach (var hit in hits)
        {
            var matched = (hit.Account?.Contains("admin", StringComparison.OrdinalIgnoreCase) ?? false)
                          || (hit.RealName?.Contains("admin", StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.True(matched, $"行 Id={hit.Id} 的 Account/RealName 均不含关键字 ⇒ 搜索条件疑似被旁路");
        }

        // 与"强类型表达式路径"结果集一致(同一白名单语义的等价实现,防"ApplyDynamic 悄悄不过滤")
        var typedIds = await scope.Db.Queryable<SysUser>()
            .Where(u => u.Account!.Contains("admin") || u.RealName!.Contains("admin"))
            .Select(u => u.Id)
            .ToListAsync();
        Assert.Equal(typedIds.OrderBy(x => x).ToList(), hits.Select(h => h.Id).OrderBy(x => x).ToList());

        // 真的过滤了:命中数必须小于存续行总数(否则等于"没加 WHERE 也过")
        var aliveCount = await scope.Db.Queryable<SysUser>().CountAsync();
        Assert.True(aliveCount > hits.Count, $"搜索应真的收窄结果集:命中 {hits.Count} 行 vs 存续 {aliveCount} 行");

        // Password 是 SysUser 的真实列(且列名=属性名)⇒ 白名单应接受它"可搜";
        // 但投影只取 Id:查询不炸即本例的断言点,敏感值绝不进入内存之外的任何地方(消息/日志均不含)
        var stored = await scope.Db.Ado.SqlQueryAsync<string>(
            $"""SELECT "Password" FROM {KnownTable} WHERE "Account" = @a""", new SugarParameter("@a", "admin"));
        Assert.Single(stored);
        Assert.False(string.IsNullOrEmpty(stored[0]), "取到的密码哈希不应为空(空值会让本用例退化为空转)");
        var probe = stored[0][..1]; // 单字符探针:足以命中其自身所在行,又不构成敏感信息泄露
        var pwdHitIds = await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput("password", null, false), probe)
            .Select(u => u.Id)
            .ToListAsync();
        Assert.True(pwdHitIds.Count >= 1, "对 Password 列的 LIKE 应至少命中其自身所在行(查询被旁路则为 0)");
        Assert.All(pwdHitIds, id => Assert.True(id > 0));

        _output.WriteLine($"判定:account,realName 搜 admin 命中 {hits.Count} 行(存续 {aliveCount} 行)且逐行含关键字;" +
                          $"与强类型 Where 结果集一致;Password 列可搜不炸(命中 {pwdHitIds.Count} 行,仅投影 Id)");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 非法排序字段 → 业务异常(可检索文案)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "非法排序字段_业务异常")]
    public async Task 非法排序字段_业务异常()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ex = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput(null, "NotAColumn")); });
        // 批 AR 口径:断言稳定错误码与参数(文案已由资源层渲染,不再断言中文)
        Assert.Equal(ErrorCodes.DynamicQueryOrderByNotAllowed, ex.ErrorCode);
        Assert.Equal("NotAColumn", ex.Args!["field"]);

        // 反向取证:合法字段**不抛**(证明上面的异常来自白名单判定,而非"排序一律抛")
        var legit = await OrderedIdsAsync(scope, "Status", desc: false);
        Assert.NotEmpty(legit);
        _output.WriteLine($"判定:OrderBy=NotAColumn ⇒ AppException(\"{ex.Message}\");合法列 Status 不抛(返回 {legit.Count} 行)");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 非法搜索字段 → 业务异常(注意:关键字为空时实现按"不施加搜索"静默返回,故必须带关键字才进校验分支)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "非法搜索字段_业务异常")]
    public async Task 非法搜索字段_业务异常()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ex = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput("NotAColumn", null, false), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQueryFieldNotAllowed, ex.ErrorCode);
        Assert.Equal("NotAColumn", ex.Args!["field"]);

        // 多字段混入一个非法项:整条请求应被拒(不是"跳过非法项继续搜")
        var mixed = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput("account,NotAColumn", null, false), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQueryFieldNotAllowed, mixed.ErrorCode);

        _output.WriteLine($"判定:SearchBy=NotAColumn ⇒ AppException(\"{ex.Message}\");account,NotAColumn 混合 ⇒ 同样被拒");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 注入串被拒(逐个断言)+ 事后复核表仍在(防假绿)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "注入串被拒")]
    public async Task 注入串被拒()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var rejected = new List<string>();

        // ① 排序位:堆叠语句
        var ex1 = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput(null, "Id; DROP TABLE sys_user--")); });
        Assert.Equal(ErrorCodes.DynamicQueryOrderByNotAllowed, ex1.ErrorCode);
        rejected.Add(ex1.Message);

        // ② 排序位:合法列名 + 注入尾巴(逗号/子查询)
        var ex2 = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput(null, "Id DESC, (SELECT 1)")); });
        Assert.Equal(ErrorCodes.DynamicQueryOrderByNotAllowed, ex2.ErrorCode);
        rejected.Add(ex2.Message);

        // ③ 搜索位:闭合括号 + 恒真条件
        var ex3 = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput("account) OR 1=1--", null, false), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQueryFieldNotAllowed, ex3.ErrorCode);
        rejected.Add(ex3.Message);

        // ④ 搜索位:堆叠 DELETE
        var ex4 = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput("account; DELETE FROM sys_user", null, false), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQueryFieldNotAllowed, ex4.ErrorCode);
        rejected.Add(ex4.Message);

        Assert.Equal(4, rejected.Count); // 四串**逐个**用例断言,防只测一个当全测

        // 事后复核:表还在,且仍可查(若注入真的生效,DROP/DELETE 后此处必红)
        var tableStillThere = await scope.Db.Ado.SqlQueryAsync<int>(
            "SELECT COUNT(*)::int FROM information_schema.tables WHERE table_name='sys_user'");
        Assert.Equal(1, tableStillThere[0]);

        var aliveRows = await scope.Db.Ado.SqlQueryAsync<int>(
            $"""SELECT COUNT(*)::int FROM {KnownTable} WHERE "IsDelete" = false""");
        Assert.True(aliveRows[0] >= 1, $"注入复核:{KnownTable} 应仍可查且有存续行,实际 {aliveRows[0]}");

        _output.WriteLine($"判定:4 条注入串全部 AppException(文案可检索);事后 information_schema 命中 {tableStillThere[0]} 张 {KnownTable},存续 {aliveRows[0]} 行");
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 关键字与搜索字段的配对语义:关键字无字段 → 业务异常;有字段无关键字 → 静默不施加(结果集不变)
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "关键字无字段_业务异常")]
    public async Task 关键字无字段_业务异常()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ex = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput(null, null), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQuerySearchByRequired, ex.ErrorCode);

        // SearchBy 是纯空白 ⇒ Split(RemoveEmptyEntries|TrimEntries) 后为空 ⇒ 同"未指定字段"分支
        var blank = Assert.Throws<AppException>(
            () => { _ = scope.Db.Queryable<SysUser>().ApplyDynamic(new DynamicQueryInput("   ", null, false), "admin"); });
        Assert.Equal(ErrorCodes.DynamicQuerySearchByRequired, blank.ErrorCode);

        var baseline = await scope.Db.Queryable<SysUser>().CountAsync();
        Assert.True(baseline > 0, "基线计数应 >0(否则下面的相等断言退化为 0==0 空转)");

        // 给了字段但关键字为空(含 null/空串/纯空白):不报错、不施加搜索 ⇒ 计数与基线相等
        var withNullKeyword = await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput("account", null, false), null)
            .Select(u => u.Id).ToListAsync();
        var withEmptyKeyword = await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput("account", null, false), "")
            .Select(u => u.Id).ToListAsync();
        var withBlankKeyword = await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput("account", null, false), "   ")
            .Select(u => u.Id).ToListAsync();
        Assert.Equal(baseline, withNullKeyword.Count);
        Assert.Equal(baseline, withEmptyKeyword.Count);
        Assert.Equal(baseline, withBlankKeyword.Count);

        _output.WriteLine($"判定:keyword=admin 无 SearchBy ⇒ AppException(\"{ex.Message}\");有字段无关键字(null/\"\"/空白)三项计数均 = 基线 {baseline}");
    }

    // ─────────────────────────────────────────────────────────────
    // 7. [SugarColumn(ColumnName)] 显式映射:库列名与属性名不同时,两者都应被白名单接受
    // ─────────────────────────────────────────────────────────────
    [Fact(DisplayName = "SugarColumn列名可传")]
    public async Task SugarColumn列名可传()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        // 先核实"本仓确有列名≠属性名 的显式映射":EntityBaseId.Id(属性)→ 列 "id"(task_008 约定:主键列小写)
        var idProp = typeof(SysUser).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(idProp);
        var mapped = idProp!.GetCustomAttributes(typeof(SugarColumn), inherit: true)
            .OfType<SugarColumn>()
            .Select(a => a.ColumnName)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        Assert.Contains("id", mapped);
        Assert.DoesNotContain("Id", mapped); // 显式映射值恰好不是属性名 —— 这正是"按库列名传参"要覆盖的场景

        // 库列名 "id" 与属性名 "Id" 各断言一次:两者都生效,且序与原生 SQL 逐项相同
        var byProperty = await OrderedIdsAsync(scope, "Id", desc: true);
        var byColumn = await OrderedIdsAsync(scope, "id", desc: true);
        Assert.Equal(byProperty, byColumn);
        Assert.NotEmpty(byColumn);

        var raw = await scope.Db.Ado.SqlQueryAsync<long>(
            $"""SELECT id FROM {KnownTable} WHERE "IsDelete" = false ORDER BY id DESC""");
        Assert.Equal(raw, byColumn);

        // 反证:映射的"另一侧"也必须是白名单成员——把属性名当列名用的写法(property-name 映射)照常工作
        var byPlainProperty = await OrderedIdsAsync(scope, "Account", desc: false);
        Assert.Equal(byProperty.Count, byPlainProperty.Count);

        _output.WriteLine($"判定:EntityBaseId.Id ⇒ [SugarColumn(ColumnName=\"id\")] 已核实;库列名 id 与属性名 Id 结果一致且与原生 SQL 序相同({byColumn.Count} 行)");
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按指定列(库列名或属性名)取全部存续用户的 Id 序(不加 Take:样本序验不出整体序)。
    /// 只投影 Id:实体含 Password 等敏感列,投影即"最小读取面"。
    /// </summary>
    private static async Task<List<long>> OrderedIdsAsync(DbScope scope, string column, bool desc)
        => await scope.Db.Queryable<SysUser>()
            .ApplyDynamic(new DynamicQueryInput(null, column, desc))
            .Select(u => u.Id)
            .ToListAsync();

    /// <summary>
    /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache(无 Redis 连接串 ⇒ 内存实现)+ AddAutoServices。
    /// WHY 不 InitTables/HealColumnCase:本批全程只读,建表/改列属写操作且非本能力的证明对象(最小必要)。
    /// WHY 异步工厂而非 BatchAD 的同步 ctor:探测用 await Task.WhenAny 而非 task.Wait(硬要求禁 .Wait()/.Result);
    /// PG 不可达时返回降级实例(PgReachable=false),用例侧 Assert.Fail,同 BatchADDatabaseTest 口径。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        private DbScope()
        {
        }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
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
            services.AddLogging(); // 无 provider ⇒ 不落任何日志(含 SQL 参数),避免敏感列值进入输出
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysUser).Assembly); // [Service] 自动发现(同 BatchADDatabaseTest 口径)
            scope._provider = services.BuildServiceProvider();
            scope.Db = scope._provider.GetRequiredService<ISqlSugarClient>();
            scope.PgReachable = true;
            return scope;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死,超时即降级 = BatchADDatabaseTest 同口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(DbHost, DbPort);
                var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromMilliseconds(2000)));
                return finished == connect && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}