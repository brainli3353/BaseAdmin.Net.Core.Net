using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Print;
using AdminCore.Core.Services.Print.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AF(4.6 打印模板)测试:被测主体 = <see cref="SysPrintService"/> 六方法
/// (新增名称/内容校验＋同名拒绝、部分更新、复制名称递增、分页投影、详情、软删)。
/// </summary>
/// <remarks>
/// WHY 全部用例需真实 PG:断言的正是"服务层校验/唯一性/软删 × 库层唯一索引 i_sys_print_name × 全局软删过滤器"
/// 的合成结果——内存替身只能验证"我喂了什么就返回什么",证明不了同名拒绝在**库层**也有兜底、软删后查询真的不可见。
/// PG 不可达时 Assert.Fail 显式降级(禁止冒充通过,口径同 BatchADDatabaseTest)。
/// 用例零污染:探针名称统一前缀 <c>afp_{guid}</c>(每次测试实例唯一,防与残留行/库层唯一索引撞名),
/// finally 以**原生 SQL 物理删**(含软删行——软删行 IsDelete=true 会被仓储 Deleteable 的全局过滤器漏掉,
/// 残留后会与唯一索引冲突致下轮同名探针直接失败)。
/// </remarks>
public class BatchAFPrintTest
{
    private readonly ITestOutputHelper _output;

    public BatchAFPrintTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchADDatabaseTest/BatchGNoticeMessageTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchADDatabaseTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // 探针模板内容(自由格式,约定为打印引擎可消费的 JSON/文本;此处用可辨识字符串即可断言往返)
    private const string ProbeTemplateA = "{\"afp\":\"模板甲\",\"rows\":[{\"field\":\"name\"}]}";
    private const string ProbeTemplateB = "{\"afp\":\"模板乙\",\"rows\":[{\"field\":\"org\"}]}";

    // ─────────────────────────────────────────────────────────────
    // 1. 新增:返回 Id > 0,且详情回读六字段全等
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 新增_成功且返回Id()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var name = Prefix(prefix, "add");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();
            var id = await svc.AddAsync(new AddSysPrintInput(
                Name: name, Template: ProbeTemplateA, PrintType: 2, Status: 1, OrderNo: 7, Remark: "afp 备注"));

            Assert.True(id > 0, $"新增应返回雪花 Id,实际 {id}");

            var detail = await svc.GetDetailAsync(id);
            Assert.Equal(name, detail.Name);
            Assert.Equal(ProbeTemplateA, detail.Template);
            Assert.Equal(2, detail.PrintType);   // 2=客户端(仅存储/展示语义)
            Assert.Equal(1, detail.Status);      // 1=启用
            Assert.Equal(7, detail.OrderNo);
            Assert.Equal("afp 备注", detail.Remark);
            Assert.NotNull(detail.CreateTime);   // AOP 插入自动填创建时间

            _output.WriteLine($"判定:新增 Id={id},详情回读 Name/Template/PrintType/Status/OrderNo/Remark 全等");
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 新增:同名拒绝 + 空名称/空内容必填校验(校验先于插入:不落半成品行)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 新增_同名拒绝与必填校验()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var name = Prefix(prefix, "dup");
        var blankName = Prefix(prefix, "blank");
        var blankTemplate = Prefix(prefix, "notpl");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();
            var id = await svc.AddAsync(new AddSysPrintInput(Name: name, Template: ProbeTemplateA, PrintType: null, Status: null, OrderNo: null, Remark: null));
            Assert.True(id > 0);

            // 同名再建:应用层拒绝(消息含"同名"且含"已存在"),库层唯一索引是并发穿透时的最终防线
            var dup = await Assert.ThrowsAsync<AppException>(() =>
                svc.AddAsync(new AddSysPrintInput(Name: name, Template: ProbeTemplateB, PrintType: null, Status: null, OrderNo: null, Remark: null)));
            Assert.Equal(ErrorCodes.PrintNameExists, dup.ErrorCode); // 批 AR-4 口径(合并两条文案断言)

            // 空名称(纯空白同样按空处理)
            var emptyName = await Assert.ThrowsAsync<AppException>(() =>
                svc.AddAsync(new AddSysPrintInput(Name: "   ", Template: ProbeTemplateB, PrintType: null, Status: null, OrderNo: null, Remark: null)));
            Assert.Equal(ErrorCodes.PrintNameRequired, emptyName.ErrorCode);

            // 空内容
            var emptyTemplate = await Assert.ThrowsAsync<AppException>(() =>
                svc.AddAsync(new AddSysPrintInput(Name: blankTemplate, Template: null, PrintType: null, Status: null, OrderNo: null, Remark: null)));
            Assert.Equal(ErrorCodes.PrintContentRequired, emptyTemplate.ErrorCode);

            // 校验在插入之前:两条被拒的入参都不应留下任何行(含软删行)
            Assert.False(await scope.Db.Queryable<SysPrint>().AnyAsync(p => p.Name == blankTemplate),
                "空内容被拒后不应落行");
            Assert.Equal(1, await CountRawAsync(scope.Db, prefix)); // 只有一条合法行

            _output.WriteLine("判定:同名抛 AppException(已存在同名打印模板)、空名称/空内容各抛 AppException 且不落行");
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 更新:部分更新(null=不改),同名拒绝排除自身
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 更新_部分更新只改指定字段()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var nameA = Prefix(prefix, "a");
        var nameB = Prefix(prefix, "b");
        var renamed = Prefix(prefix, "a2");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();
            var idA = await svc.AddAsync(new AddSysPrintInput(
                Name: nameA, Template: ProbeTemplateA, PrintType: 1, Status: 1, OrderNo: 11, Remark: "甲备注"));
            var idB = await svc.AddAsync(new AddSysPrintInput(
                Name: nameB, Template: ProbeTemplateB, PrintType: 1, Status: 1, OrderNo: 22, Remark: "乙备注"));

            // ① 只传 Status=2 → 仅 Status 变,其余四个字段原样
            await svc.UpdateAsync(new UpdateSysPrintInput(Id: idA, Name: null, Template: null, PrintType: null, Status: 2, OrderNo: null, Remark: null));
            var afterStatus = await svc.GetDetailAsync(idA);
            Assert.Equal(2, afterStatus.Status);
            Assert.Equal(nameA, afterStatus.Name);
            Assert.Equal(ProbeTemplateA, afterStatus.Template);
            Assert.Equal(11, afterStatus.OrderNo);
            Assert.Equal("甲备注", afterStatus.Remark);

            // ② 只传 Name(新名)→ 名称变,Status=2 与其余字段保持
            await svc.UpdateAsync(new UpdateSysPrintInput(Id: idA, Name: renamed, Template: null, PrintType: null, Status: null, OrderNo: null, Remark: null));
            var afterName = await svc.GetDetailAsync(idA);
            Assert.Equal(renamed, afterName.Name);
            Assert.Equal(2, afterName.Status);
            Assert.Equal(ProbeTemplateA, afterName.Template);
            Assert.Equal(11, afterName.OrderNo);
            Assert.Equal("甲备注", afterName.Remark);

            // ③ 改成他行已存在的名称 → 拒绝(应用层同名拒绝;排除自身是关键,见 ④)
            var dup = await Assert.ThrowsAsync<AppException>(() =>
                svc.UpdateAsync(new UpdateSysPrintInput(Id: idA, Name: nameB, Template: null, PrintType: null, Status: null, OrderNo: null, Remark: null)));
            Assert.Equal(ErrorCodes.PrintNameExists, dup.ErrorCode);

            // ④ 把自己的名称原样再传一次 → 成功(excludeId 生效,不得把自身判成重名)
            await svc.UpdateAsync(new UpdateSysPrintInput(Id: idA, Name: renamed, Template: null, PrintType: null, Status: null, OrderNo: null, Remark: null));
            Assert.Equal(renamed, (await svc.GetDetailAsync(idA)).Name);

            // ⑤ 他行 B 未被任何操作波及
            var b = await svc.GetDetailAsync(idB);
            Assert.Equal(nameB, b.Name);
            Assert.Equal(ProbeTemplateB, b.Template);
            Assert.Equal(22, b.OrderNo);
            Assert.Equal("乙备注", b.Remark);

            _output.WriteLine("判定:只传 Status 仅 Status 变;只传 Name 仅 Name 变;撞他行名抛异常;自身同名放行");
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 复制:新 Id、名称 <原名> - copy(N) 递增、其余字段随源
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 复制_新Id且名称递增()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var name = Prefix(prefix, "src");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();
            var srcId = await svc.AddAsync(new AddSysPrintInput(
                Name: name, Template: ProbeTemplateB, PrintType: 2, Status: 2, OrderNo: 33, Remark: "源备注"));

            var copy1Id = await svc.CopyAsync(srcId);
            Assert.NotEqual(srcId, copy1Id); // 雪花口径必须显式新建行(不沿用源 Id)
            var copy1 = await svc.GetDetailAsync(copy1Id);
            Assert.Equal($"{name} - copy(1)", copy1.Name);
            Assert.Equal(ProbeTemplateB, copy1.Template);
            Assert.Equal(2, copy1.PrintType);
            Assert.Equal(2, copy1.Status);
            Assert.Equal(33, copy1.OrderNo);
            Assert.Equal("源备注", copy1.Remark);

            var copy2Id = await svc.CopyAsync(srcId);
            Assert.NotEqual(srcId, copy2Id);
            Assert.NotEqual(copy1Id, copy2Id);
            Assert.Equal($"{name} - copy(2)", (await svc.GetDetailAsync(copy2Id)).Name);

            // 源行未被复制动作改动
            var src = await svc.GetDetailAsync(srcId);
            Assert.Equal(name, src.Name);
            Assert.Equal(ProbeTemplateB, src.Template);

            _output.WriteLine($"判定:copy1={copy1Id}({copy1.Name})、copy2={copy2Id}(copy(2)),字段随源");
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 分页:名称模糊过滤命中探针行,且列表输出类型不含 Template 属性
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 分页_模糊过滤且列表不含模板大字段()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var name1 = Prefix(prefix, "p1");
        var name2 = Prefix(prefix, "p2");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();
            var id1 = await svc.AddAsync(new AddSysPrintInput(Name: name1, Template: ProbeTemplateA, PrintType: null, Status: 1, OrderNo: 1, Remark: "r1"));
            var id2 = await svc.AddAsync(new AddSysPrintInput(Name: name2, Template: ProbeTemplateB, PrintType: null, Status: 2, OrderNo: 2, Remark: null));

            // 取证用日志钩子:仅为观察库层真实 SQL,取证失败(钩子未生效)不判红。
            // WHY 直接赋值而非 +=:SqlSugar 的 AopProvider.OnLogExecuting 只有 setter(无 getter),
            // 读不到前一处理器也就无法还原——本用例内临时替换,代价仅是本 scope 内少一条慢 SQL 告警(scope 随用例释放)。
            var sqls = new List<string>();
            scope.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                lock (sqls)
                {
                    sqls.Add(sql);
                }
            };

            var page = await svc.PageAsync(new PageSysPrintInput(prefix, 1, 50));

            Assert.Equal(2, page.Total);
            Assert.Equal(2, page.List.Count);
            Assert.All(page.List, r => Assert.StartsWith(prefix, r.Name, StringComparison.Ordinal));
            Assert.Contains(page.List, r => r.Id == id1 && r.Remark == "r1");
            Assert.Contains(page.List, r => r.Id == id2 && r.Remark == null);

            // 反射断言:列表输出**没有** Template 属性(防"以为不载其实载了")
            var props = typeof(SysPrintOutput)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();
            Assert.DoesNotContain(props, p => string.Equals(p, "Template", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Name", props);

            // 取证(非断言):把库层实际 SELECT 打出来——DTO 不含 Template ≠ 库层省列,
            // 服务是在 ToPageListAsync(整实体)之后做内存投影,二者口径必须分别核。
            string? printSql;
            lock (sqls)
            {
                printSql = sqls.FirstOrDefault(s => s.Contains("sys_print", StringComparison.OrdinalIgnoreCase));
            }

            if (printSql is null)
            {
                _output.WriteLine("取证:未捕获到 sys_print 查询 SQL(日志钩子未生效),本项不作断言");
            }
            else
            {
                _output.WriteLine($"取证:PageAsync 库层 SQL = {printSql}");
                _output.WriteLine(printSql.Contains("Template", StringComparison.OrdinalIgnoreCase)
                    ? "取证结论:库层 SQL **仍然 SELECT 了 Template 列**(列表省列仅在输出 DTO 口径成立)"
                    : "取证结论:库层 SQL 未含 Template 列");
            }
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 详情/删除:不存在抛业务异常;软删后详情不可见、分页不再命中(行仍在库)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 详情与删除_不存在抛异常且软删后不可见()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        var name = Prefix(prefix, "del");
        try
        {
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysPrintService>();

            // 不存在:详情/删除都走统一的业务异常(不泄露内部状态)
            var missDetail = await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(999_999_999_998));
            Assert.Equal(ErrorCodes.PrintNotFound, missDetail.ErrorCode);
            var missDelete = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(999_999_999_997));
            Assert.Equal(ErrorCodes.PrintNotFound, missDelete.ErrorCode);

            var id = await svc.AddAsync(new AddSysPrintInput(Name: name, Template: ProbeTemplateA, PrintType: null, Status: null, OrderNo: 3, Remark: null));

            await svc.DeleteAsync(id);

            // 详情不可见(软删行被全局过滤器过滤 → 等同不存在)
            await Assert.ThrowsAsync<AppException>(() => svc.GetDetailAsync(id));

            // 分页不再命中
            var page = await svc.PageAsync(new PageSysPrintInput(prefix, 1, 50));
            Assert.Equal(0, page.Total);
            Assert.DoesNotContain(page.List, r => r.Id == id);

            // 但行仍在库里(IsDelete=true)——证明是软删而非物理删
            var rawCount = await CountRawAsync(scope.Db, prefix);
            Assert.Equal(1, rawCount);
            var isDeleted = await scope.Db.Ado.SqlQuerySingleAsync<bool>(
                "SELECT \"IsDelete\" FROM sys_print WHERE id = @id", new SugarParameter("@id", id));
            Assert.True(isDeleted, "软删行的 IsDelete 应为 true");

            _output.WriteLine("判定:不存在 id 详情/删除均抛 AppException;软删后详情与分页均不可见,库内行 IsDelete=true");
        }
        finally
        {
            await CleanupPrintAsync(scope.Db, prefix);
        }
    }

    // ── 探针工具 ─────────────────────────────────────────────────────────────

    /// <summary>探针名称前缀(每次测试实例唯一):防与残留探针行、库层唯一索引 i_sys_print_name 撞名。</summary>
    private static string NewProbePrefix() => $"afp_{Guid.NewGuid():N}";

    private static string Prefix(string prefix, string tag) => $"{prefix}_{tag}";

    /// <summary>
    /// 物理清理探针行(**含软删行**)。
    /// WHY 原生 SQL:仓储 DeleteById/Deleteable 要么是软删、要么被全局软删过滤器追加 IsDelete=false,
    /// 软删行会残留并与唯一索引冲突(下轮同名探针直接失败)。
    /// WHY left() 而非 LIKE:前缀含 '_' 时 LIKE 里是通配符,会误伤他行。
    /// </summary>
    private static async Task CleanupPrintAsync(ISqlSugarClient db, string prefix)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_print WHERE left(\"Name\", @len) = @prefix",
            new SugarParameter("@len", prefix.Length), new SugarParameter("@prefix", prefix));
    }

    /// <summary>不受全局软删过滤器影响的物理计数(过滤/软删双口径的对照基线)。</summary>
    private static async Task<int> CountRawAsync(ISqlSugarClient db, string prefix)
        => await db.Ado.SqlQuerySingleAsync<int>(
            "SELECT count(*)::int FROM sys_print WHERE left(\"Name\", @len) = @prefix",
            new SugarParameter("@len", prefix.Length), new SugarParameter("@prefix", prefix));

    // ── 基础设施 ─────────────────────────────────────────────────────────────

    /// <summary>构造模拟登录上下文(AOP 审计字段填充用;本批服务不读用户,仅为与生产写入路径同口径)。</summary>
    private static HttpContext ProbeUserContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, "9900000000101"),
            new Claim(JwtClaimNames.Account, "dsh_af_print"),
            new Claim(JwtClaimNames.RealName, "探针打印"),
            new Claim(JwtClaimNames.AccountType, "777"),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        return context;
    }

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计/租户填充读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集)。
    /// InitTables 扫 Core 程序集 → sys_print 缺表即建(含唯一索引 i_sys_print_name),幂等。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private readonly ServiceProvider? _provider;

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
            services.AddAutoServices(typeof(SysPrint).Assembly);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            FakeAccessor.HttpContext = ProbeUserContext();
            TestSchemaBootstrap.Ensure(Db);
            PgReachable = true;
        }

        /// <summary>新建子作用域(与生产 scoped 解析同口径;调用方 using 释放)。</summary>
        public IServiceScope NewScope() => Provider.CreateScope();

        public void Dispose()
        {
            _provider?.Dispose();
        }

        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new TcpClient();
                // WHY 同步 Connect 而非 ConnectAsync().Wait():构造期判定不能 async,而硬要求禁 .Wait()/.Result()
                // (sync-over-async 死锁面)。回环地址上"有监听即连上、无监听即 ECONNREFUSED",二者都立刻返回,
                // 故无需超时参数即可快速降级(BatchADDatabaseTest/BatchGNoticeMessageTest 的 .Wait(2s) 口径等价)。
                client.Connect(DbHost, DbPort);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}