using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions; // ITestOutputHelper(xunit 输出助手,降级声明写入测试输出)
using AdminCore.Core;
using AdminCore.Core.Entities;

namespace AdminCore.Test;

/// <summary>
/// 实体基类体系测试:雪花主键并发唯一 / AOP 审计自动填充 / 软删全局过滤器。
/// 环境说明:两个 DB 用例依赖 PostgreSQL(连接串与 Business/Configuration/Database.Development.json 开发配置同源,仅开发用);
/// 沙箱不可达 PG 时用例诚实降级为 no-op 并在测试输出中显式声明(不静默冒充通过),
/// 验收环境(docker postgres)下走真实断言。
/// </summary>
public class EntityBaseTest
{
    private readonly ITestOutputHelper _output;

    /// <summary>构造函数注入 xunit 输出助手(降级声明写入测试输出,可追溯)。</summary>
    public EntityBaseTest(ITestOutputHelper output)
    {
        _output = output;
    }
    // 开发库连接串(与 Business/Configuration/Database.Development.json 一致,仅开发用;生产密钥环境变量注入)。
    // Timeout=10:沙箱 PG 黑洞时快速失败,防用例挂死(可达环境无感知)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>
    /// 雪花主键:并发 1 万次 NextId 无重复(不依赖 DB)。
    /// </summary>
    [Fact]
    public void 雪花主键_并发1万次_无重复()
    {
        YitIdHelper.Init(1); // 幂等:与 DB 用例中 AddSqlSugar 的初始化互不影响

        // 任务书口径:Parallel.For + HashSet 计数(HashSet 非线程安全,加锁)
        var ids = new HashSet<long>();
        var syncRoot = new object();
        Parallel.For(0, 10000, _ =>
        {
            lock (syncRoot)
            {
                ids.Add(YitIdHelper.NextId());
            }
        });

        Assert.Equal(10000, ids.Count);
    }

    /// <summary>
    /// 审计填充:插入 SysConfig(业务代码只给 Name)→ AOP 自动填雪花主键与 CreateTime。
    /// </summary>
    [Fact]
    public void 审计自动填充_插入SysConfig_主键与CreateTime已填()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            // 沙箱:PG 不可达 → 诚实降级 no-op,输出中显式声明本用例未实跑(不静默冒充通过);
            // 验收环境(docker postgres)探测可达,继续走下方真实断言
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            return;
        }

        var db = scope.Db;
        var name = $"task_003_audit_{Guid.NewGuid():N}";
        var config = new SysConfig { Name = name }; // 业务代码只管理业务字段:Id/CreateTime 留空,框架填
        db.Insertable(config).ExecuteCommand();

        try
        {
            Assert.True(config.Id > 0, "AOP 应在主键为空时填雪花主键");
            Assert.NotNull(config.CreateTime); // AOP 应在插入时填 CreateTime
        }
        finally
        {
            // 清理测试数据:本行刚插入(IsDelete=false),物理删除不留痕
            db.Deleteable<SysConfig>().Where(u => u.Id == config.Id).ExecuteCommand();
        }
    }

    /// <summary>
    /// 软删:软删操作后 AsQueryable 查不到该行(全局过滤器生效),数据库物理行仍在且 IsDelete=true。
    /// </summary>
    [Fact]
    public void 软删_查询被过滤且数据库保留IsDelete标记()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            // 沙箱:PG 不可达 → 诚实降级 no-op,输出中显式声明本用例未实跑(不静默冒充通过);
            // 验收环境(docker postgres)探测可达,继续走下方真实断言
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            return;
        }

        var db = scope.Db;
        var name = $"task_003_softdel_{Guid.NewGuid():N}";
        var config = new SysConfig { Name = name };
        db.Insertable(config).ExecuteCommand();
        var id = config.Id;

        try
        {
            // 1) 删除前:查询可见
            Assert.NotNull(db.Queryable<SysConfig>().First(u => u.Id == id));

            // 2) 软删 = 更新 IsDelete 为 true(框架口径,非物理 DeleteById):
            //    AOP UpdateByObject 自动填 UpdateTime 与 DeleteTime(IsDelete=true 时)
            var toDelete = db.Queryable<SysConfig>().First(u => u.Id == id);
            toDelete.IsDelete = true;
            db.Updateable(toDelete).ExecuteCommand();

            // 3) 删除后:AsQueryable 查不到(全局过滤器自动追加 IsDelete=false)
            Assert.Null(db.Queryable<SysConfig>().First(u => u.Id == id));

            // 4) 数据库物理行仍在且 IsDelete=true——5.1.4.217 无 SqlWith.NoFilter,
            //    绕过全局过滤器改用 Ado 原生 SQL 读物理行验证(过滤器只作用于查询构造器)
            //    列名口径(task_008 fixa):主键小写 id(裸写折小写命中),其余列 PascalCase 须引号精确引用
            var rawList = db.Ado.SqlQuery<SysConfig>("select * from sys_config where id = @id", new { id });
            Assert.NotNull(rawList);
            var raw = rawList.FirstOrDefault();
            Assert.NotNull(raw);
            Assert.True(raw.IsDelete);
            Assert.NotNull(raw.DeleteTime); // AOP 应在 IsDelete=true 时填 DeleteTime
            Assert.NotNull(raw.UpdateTime);
        }
        finally
        {
            // 清理:物理删除需原生 SQL(该行已 IsDelete=true,被过滤后 Deleteable 的 where 不命中)
            db.Ado.ExecuteCommand("delete from sys_config where id = @id", new { id });
        }
    }

    /// <summary>
    /// DB 测试客户端:走与生产同一条 AddSqlSugar 注册路径(雪花+AOP+软删过滤器全生效)。
    /// 构造时做一次 CodeFirst 增量建表(兼作 PG 可达性检查);不可达则标记,用例降级 no-op 并声明。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        /// <summary>PG 可达且 CodeFirst 建表成功。</summary>
        public bool PgReachable { get; }

        public DbScope()
        {
            if (!IsPostgresReachable())
            {
                return; // 沙箱:不建容器不连库,用例侧降级 no-op 并声明
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
            // 列名大小写自愈(task_008 fixa):存量小写列更名 PascalCase,须先于建表(口径同 Program.cs);
            // CodeFirst 增量:表不存在则建、存在则仅补基类新增列(已有数据不丢);
            // PG 可达时若此步抛异常=真实 DDL 问题,必须让测试失败(不吞)
            Db.HealColumnCase();
            Db.InitTables();
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
