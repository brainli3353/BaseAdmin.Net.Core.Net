using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.CodeGen;
using AdminCore.Core.Services.CodeGen.Dto;
using AdminCore.Core.Services.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 BF 代码生成器基类裁决测试:被测主体 = <see cref="CodeGenService.PreviewAsync"/> 的
/// **基类推断 + 失配 warning + 敏感列(密码族)warning** 三条行为(批 AI 的 8/8 回归面不变,此处只测增量)。
/// </summary>
/// <remarks>
/// WHY 需真实 PG:三条行为的全部输入都是**真实表的 TenantId 列有无 / 列名形态**(sys_user 带 TenantId、
/// sys_notice_user 不带)——内存替身喂什么就断言什么,测不到"按表结构推断"。PG 不可达时 Assert.Fail 显式降级(禁止冒充通过)。
/// <para>
/// WHY 期望值从 information_schema **独立反推**:推断逻辑(列名→基类)的输入若与实现走同一条 ListColumnsAsync,
/// 两边同错同绿;本用例的 TenantId 有无 / 密码族列有无 一律直查 information_schema(独立 SQL),与实现形成差分核对。
/// </para>
/// <para>
/// <b>零污染裁决</b>:与批 AI 同源——只读(列结构查询 + 预览生成均不写盘不写库),DbScope 不调用
/// InitTables/HealColumnCase。
/// </para>
/// </remarks>
public class BatchBFCodeGenTest
{
    private readonly ITestOutputHelper _output;

    public BatchBFCodeGenTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 主库连接串(与 BatchAICodeGenTest 同源,仅开发用;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    // 诚实降级文案(与 BatchAICodeGenTest 完全同源)
    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private const string TenantTable = "sys_user"; // 真机取证:带 TenantId 列
    private const string NonTenantTable = "sys_role_table"; // 真机取证:无 TenantId 列（t846 批 1 后 sys_notice_user 已纳管 ⇒ 样本换成这张经复评**维持不纳管**的表，其实体注释已立"勿再加 TenantId"反例）

    /// <summary>直查 information_schema 取某表列名(独立于实现的 ListColumnsAsync,差分核对用)。</summary>
    private static async Task<List<string>> QueryColumnNamesAsync(ISqlSugarClient db, string table)
    {
        const string sql =
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position";
        var rows = await db.Ado.SqlQueryAsync<string>(sql, new SugarParameter("@t", table));
        return rows ?? new List<string>();
    }

    private static bool HasAny(List<string> columns, Func<string, bool> predicate)
        => columns.Any(predicate);

    // ─────────────────────────────────────────────────────────────
    // 1. 缺省推断:带 TenantId 列的表 → EntityTenant(框架约定:业务实体一律继承 EntityTenant)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 缺省基类_表带TenantId列_推断为EntityTenant()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        // 前置取证(独立 SQL):sys_user 必须带 TenantId 列(库结构变化时本用例语义变化,先显式断言)
        var cols = await QueryColumnNamesAsync(scope.Db, TenantTable);
        Assert.True(HasAny(cols, c => string.Equals(c, "TenantId", StringComparison.OrdinalIgnoreCase)),
            $"前置不成立:{TenantTable} 应带 TenantId 列(实查 {string.Join(",", cols)})");

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();

        // 缺省(不传 baseType)→ 推断 EntityTenant
        var preview = await codeGen.PreviewAsync(new CodeGenInput(TenantTable));
        Assert.Contains("public class User : EntityTenant", preview.EntityCode);
        // 推断命中 = 无失配 warning(其余 warning 如敏感列与本断言正交)
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("TenantId"));
        // TenantId 属性不重复生成(基类提供;再生成会 CS0108 遮蔽 + 双映射)
        Assert.DoesNotContain("TenantId { get; set; }", preview.EntityCode);

        // 显式选 EntityTenant(与结构一致)→ 同样无 warning
        var explicitOk = await codeGen.PreviewAsync(new CodeGenInput(TenantTable, BaseType: "EntityTenant"));
        Assert.Contains("public class User : EntityTenant", explicitOk.EntityCode);
        Assert.DoesNotContain(explicitOk.Warnings, w => w.Contains("TenantId"));

        _output.WriteLine($"判定:{TenantTable} 缺省/显式 EntityTenant 均生成 ': EntityTenant',无 TenantId 失配 warning");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 缺省推断:无 TenantId 列的表 → EntityBase(行为与批 AI 前一致,防推断误伤)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 缺省基类_表无TenantId列_推断为EntityBase()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var cols = await QueryColumnNamesAsync(scope.Db, NonTenantTable);
        Assert.False(HasAny(cols, c => string.Equals(c, "TenantId", StringComparison.OrdinalIgnoreCase)),
            $"前置不成立:{NonTenantTable} 应无 TenantId 列(实查 {string.Join(",", cols)})");

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var preview = await codeGen.PreviewAsync(new CodeGenInput(NonTenantTable));

        Assert.Contains("public class RoleTable : EntityBase", preview.EntityCode);
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("TenantId"));

        _output.WriteLine($"判定:{NonTenantTable} 缺省仍推断 EntityBase(推断未误伤无租户表)");
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 显式失配①:租户表硬选 EntityBase → 照选(人工裁决权)+ warning 点名租户列丢失
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 显式失配_租户表选EntityBase_照选且警告()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var preview = await codeGen.PreviewAsync(new CodeGenInput(TenantTable, BaseType: "EntityBase"));

        // 显式选择被尊重(不静默改判)
        Assert.Contains("public class User : EntityBase", preview.EntityCode);
        // 且 warning 必须点名 TenantId(批 BF 取证:缺省时代这条静默丢失无任何提示)
        Assert.Contains(preview.Warnings, w => w.Contains("TenantId") && w.Contains("EntityBase"));

        _output.WriteLine($"判定:{TenantTable}+EntityBase ⇒ 照选 + warning 点名 TenantId");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 显式失配②:无租户表硬选 EntityTenant → 照选 + warning 点名缺列
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 显式失配_无租户表选EntityTenant_照选且警告()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();
        var preview = await codeGen.PreviewAsync(new CodeGenInput(NonTenantTable, BaseType: "EntityTenant"));

        Assert.Contains("public class RoleTable : EntityTenant", preview.EntityCode);
        Assert.Contains(preview.Warnings, w => w.Contains("TenantId") && w.Contains("EntityTenant"));

        _output.WriteLine($"判定:{NonTenantTable}+EntityTenant ⇒ 照选 + warning 点名缺列");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 敏感列(密码族)进入行输出 → warning 点名(哈希不得回传前端)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 敏感列_密码族进入行输出_警告点名()
    {
        using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var codeGen = scope.Provider.GetRequiredService<CodeGenService>();

        // sys_user 带 Password 列(独立 SQL 前置取证)
        var cols = await QueryColumnNamesAsync(scope.Db, TenantTable);
        Assert.True(HasAny(cols, c => c.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0),
            $"前置不成立:{TenantTable} 应含密码族列(实查 {string.Join(",", cols)})");
        var preview = await codeGen.PreviewAsync(new CodeGenInput(TenantTable));
        Assert.Contains("string Password", preview.DtoCode); // Output record 确实拷了密码列(事实,不替人裁剪)
        Assert.Contains(preview.Warnings, w => w.Contains("Password") && w.Contains("行输出"));
        // 防误判:同表的时间列 LastChangePasswordTime 含 "password" 子串,但不得被点名
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("LastChangePasswordTime"));

        // 对照表 sys_notice_user 无密码族列 → 不得出现该 warning(防误报)
        var otherCols = await QueryColumnNamesAsync(scope.Db, NonTenantTable);
        Assert.False(HasAny(otherCols, c =>
            c.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
            || c.IndexOf("pwd", StringComparison.OrdinalIgnoreCase) >= 0
            || c.IndexOf("salt", StringComparison.OrdinalIgnoreCase) >= 0
            || c.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0),
            $"前置不成立:{NonTenantTable} 应无密码族列(实查 {string.Join(",", otherCols)})");
        var other = await codeGen.PreviewAsync(new CodeGenInput(NonTenantTable));
        Assert.DoesNotContain(other.Warnings, w => w.Contains("敏感列"));

        _output.WriteLine($"判定:{TenantTable} 密码列进 Output ⇒ warning 点名;{NonTenantTable} 无密码族列 ⇒ 不误报");
    }

    /// <summary>
    /// 测试用 DB 容器:与生产同一条 AddSqlSugar 注册路径 + AddAutoServices(Core 程序集,[Service] 自动发现)。
    /// 零污染裁决同 BatchAICodeGenTest.DbScope(不调用 InitTables/HealColumnCase;PG 探测异步超时)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; private set; } = null!;

        public IServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                return scope; // PgReachable=false → 用例侧 Assert.Fail 降级(不静默跳过)
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
            services.AddAutoServices(typeof(CodeGenService).Assembly); // [Service] 自动发现(与 Program.cs 同口径)
            scope._provider = services.BuildServiceProvider();
            scope.Db = scope._provider.GetRequiredService<ISqlSugarClient>();
            scope.Provider = scope._provider;
            scope.PgReachable = true;
            return scope;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchAICodeGenTest 口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
                await client.ConnectAsync(DbHost, DbPort, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
