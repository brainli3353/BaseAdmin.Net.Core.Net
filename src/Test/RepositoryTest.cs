using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions; // ITestOutputHelper(xunit 输出助手,降级声明写入测试输出)
using AdminCore.Core;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;

namespace AdminCore.Test;

/// <summary>
/// 通用仓储测试:CRUD 全流程(经 DI 注册路径)+ 分页。
/// 环境说明:依赖 PostgreSQL(连接串与 Business/Configuration/Database.Development.json 开发配置同源,仅开发用);
/// 沙箱不可达 PG 时用例诚实降级(Assert.Fail 显式声明,不静默冒充通过),验收环境(docker postgres)走真实断言。
/// WHY 测试解析走 DI 而非 new:验证 SqlSugarSetup 的 Scoped 注册真实生效(生产同一条注册路径)。
/// </summary>
public class RepositoryTest
{
    private readonly ITestOutputHelper _output;

    /// <summary>构造函数注入 xunit 输出助手(降级声明写入测试输出,可追溯)。</summary>
    public RepositoryTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 Business/Configuration/Database.Development.json 同源,仅开发用;生产密钥环境变量注入)。
    // Timeout=10:沙箱 PG 黑洞时快速失败,防用例挂死(可达环境无感知)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>
    /// CRUD 全流程:Insert → GetFirstAsync 查回 → Update → IsAnyAsync → DeleteById(软删)→ 查询为空。
    /// 软删语义判别:删除后物理行仍在且 IsDelete=true(若退化为物理删除,此断言失败)。
    /// </summary>
    [Fact]
    public async Task CRUD_插入查回更新存在软删后查询为空()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            // 沙箱:PG 不可达 → 诚实降级,输出中显式声明本用例未实跑(不静默冒充通过)
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestRepoProbe>>();

        var name = $"task_004_crud_{Guid.NewGuid():N}";
        var config = new TestRepoProbe { Name = name };

        // 1) Insert(AOP 自动填雪花主键/CreateTime)
        Assert.True(await repo.InsertAsync(config));
        Assert.True(config.Id > 0, "AOP 应在插入时填雪花主键");

        var id = config.Id;
        try
        {
            // 2) GetFirstAsync 查回
            var fetched = await repo.GetFirstAsync(u => u.Id == id);
            Assert.NotNull(fetched);
            Assert.Equal(name, fetched.Name);

            // 3) Update(改业务字段;AOP 自动填 UpdateTime)
            fetched.Name = name + "_updated";
            Assert.True(await repo.UpdateAsync(fetched!));
            var afterUpdate = await repo.GetFirstAsync(u => u.Id == id);
            Assert.Equal(name + "_updated", afterUpdate!.Name);
            Assert.NotNull(afterUpdate.UpdateTime);

            // 4) IsAnyAsync
            Assert.True(await repo.IsAnyAsync(u => u.Id == id));

            // 5) DeleteById = 软删(更新 IsDelete=true;物理行保留)
            Assert.True(await repo.DeleteByIdAsync(id));

            // 6) 查询为空(全局软删过滤器自动追加 IsDelete=false)
            Assert.Null(await repo.GetFirstAsync(u => u.Id == id));
            Assert.False(await repo.IsAnyAsync(u => u.Id == id));

            // 7) 软删语义判别:物理行仍在且 IsDelete=true(AOP 应同步填 DeleteTime/UpdateTime);
            //    原生 SQL 绕全局过滤器读物理行(过滤器只作用于查询构造器);
            //    列名口径(task_008 fixa):主键小写 id 裸写命中,其余列 PascalCase 须引号
            var raw = db.Ado.SqlQuery<TestRepoProbe>("select * from test_repo_probe where id = @id", new { id }).FirstOrDefault();
            Assert.NotNull(raw);
            Assert.True(raw.IsDelete);
            Assert.NotNull(raw.DeleteTime);
        }
        finally
        {
            // 清理:该行已 IsDelete=true 被过滤,物理删除需原生 SQL
            db.Ado.ExecuteCommand("delete from test_repo_probe where id = @id", new { id });
        }
    }

    /// <summary>
    /// 分页:插入 25 条 → ToPagedListAsync(2, 10) → Total=25、List 10 条。
    /// 实体用独立探针表 test_repo_probe(批 D 起 sys_config 被多个测试类并发写,Total=全表行数断言
    /// 对并发/残留行敏感 flake 实证;探针表本用例独占,Total 确定性成立;继承 EntityBase 保 AOP/软删同口径)。
    /// </summary>
    [Fact]
    public async Task 分页_插入25条_第2页10条且Total为25()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        var db = dbScope.Db;
        using var diScope = dbScope.Provider.CreateScope();
        var repo = diScope.ServiceProvider.GetRequiredService<SqlSugarRepository<TestRepoProbe>>();

        // 幂等前置清理:上次运行崩溃残留会使 Total 虚高(用例须可重入)
        db.Ado.ExecuteCommand("delete from test_repo_probe where \"Name\" like 'task_004_page_%'");

        // 逐条经仓储插入(单对象路径,AOP 逐条填雪花主键;批量 List 插入不在 AOP 契约范围)
        var names = new List<string>(25);
        for (var i = 0; i < 25; i++)
        {
            var name = $"task_004_page_{Guid.NewGuid():N}_{i:D2}";
            var config = new TestRepoProbe { Name = name };
            Assert.True(await repo.InsertAsync(config), $"第 {i} 条插入失败");
            names.Add(name);
        }

        try
        {
            var page = await repo.ToPagedListAsync(2, 10);
            Assert.Equal(25, page.Total);
            Assert.Equal(10, page.List.Count);
            // 返回行必须全部来自本用例插入集(防串读其他测试残留行)
            Assert.All(page.List, item => Assert.Contains(item.Name!, names));
        }
        finally
        {
            db.Ado.ExecuteCommand("delete from test_repo_probe where \"Name\" like 'task_004_page_%'");
        }
    }

    /// <summary>
    /// DB 测试客户端:走与生产同一条 AddSqlSugar 注册路径(雪花+AOP+软删过滤器+仓储 Scoped 注册全生效)。
    /// 构造时做一次 CodeFirst 增量建表(兼作 PG 可达性检查);不可达则标记,用例降级并声明。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        /// <summary>DI 容器(测试从作用域解析仓储,验证 Scoped 注册路径)。</summary>
        public IServiceProvider Provider { get; } = null!;

        /// <summary>PG 可达且 CodeFirst 建表成功。</summary>
        public bool PgReachable { get; }

        public DbScope()
        {
            if (!IsPostgresReachable())
            {
                return; // 沙箱:不建容器不连库,用例侧降级并声明
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
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            // 列名大小写自愈(task_008 fixa):存量小写列更名 PascalCase,须先于建表(口径同 Program.cs);
            // CodeFirst 增量:表不存在则建、存在则仅补新增列;PG 可达时若此步抛异常=真实 DDL 问题,必须让测试失败(不吞)
            Db.HealColumnCase();
            Db.InitTables(typeof(RepositoryTest).Assembly);
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>
        /// 短超时 TCP 探测 PG 端口(2 秒)。
        /// WHY 不用 TcpClient.ConnectAsync:其无连接超时参数,沙箱 PG 黑洞(无 RST)会挂死数分钟。
        /// </summary>
        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
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
