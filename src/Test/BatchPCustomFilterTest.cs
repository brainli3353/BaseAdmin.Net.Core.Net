using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 P(3.3 自定义过滤器)测试(参照 Admin.NET SqlSugarDataExecuting 移植+接通,见 g10):
/// 命中分发/未命中不分发/多实现全分发/Update 时 oldData 旧值可见/空列表基线。
/// 环境:真实 PG;探针用户 finally 清理。探针经显式 AddSingleton&lt;IDataExecuting&gt; 注册
/// (测试程序集实现不走 AddAutoServices 程序集扫描,显式注册同效)。
/// </summary>
public class BatchPCustomFilterTest : IDisposable
{
    // 开发库连接串(与批 B/N 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private readonly ServiceProvider _provider;
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private long _userId;

    /// <summary>命中 SysUser 的探针:记录全部回调(OperationType/oldData 是否空/实体值)。</summary>
    private sealed class UserProbe : IDataExecuting
    {
        public List<(DataFilterType Op, bool OldNull, string Account)> Calls { get; } = [];
        public IReadOnlyList<Type> EntityNames => [typeof(SysUser)];
        public void Execute(object oldData, DataFilterModel model)
        {
            var acc = model.EntityValue is SysUser u ? u.Account : "(非 SysUser)";
            Calls.Add((model.OperationType, oldData == null, acc));
        }
    }

    /// <summary>第二个命中 SysUser 的探针(多实现全分发断言用)。</summary>
    private sealed class UserProbe2 : IDataExecuting
    {
        public int Count;
        public IReadOnlyList<Type> EntityNames => [typeof(SysUser)];
        public void Execute(object oldData, DataFilterModel model) => Count++;
    }

    /// <summary>只声明 SysOrg 的探针:插 SysUser 时不应触发(未命中不分发)。</summary>
    private sealed class OrgOnlyProbe : IDataExecuting
    {
        public int Count;
        public IReadOnlyList<Type> EntityNames => [typeof(SysOrg)];
        public void Execute(object oldData, DataFilterModel model) => Count++;
    }

    private readonly UserProbe _probe = new();
    private readonly UserProbe2 _probe2 = new();
    private readonly OrgOnlyProbe _orgProbe = new();

    public BatchPCustomFilterTest()
    {
        if (!IsPostgresReachable())
        {
            _provider = new ServiceCollection().BuildServiceProvider();
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
        // 探针显式注册(须在 ISqlSugarClient 工厂闭包首次解析前——AddSqlSugar 注册的单例工厂
        // 在首次 GetRequiredService&lt;ISqlSugarClient&gt; 时执行 Init 捕获,故注册顺序无强约束,
        // 但 Build 前完成最稳)
        services.AddSingleton<IDataExecuting>(_probe);
        services.AddSingleton<IDataExecuting>(_probe2);
        services.AddSingleton<IDataExecuting>(_orgProbe);
        services.AddSqlSugar(config);
        services.AddProjectOptions(config);
        services.AddCache(config);
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(db);
        _pgReachable = true;
    }

    [Fact]
    public void P1_命中分发_InsertByObject()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = $"t13p_u_{_p}",
            Password = "t13p_placeholder_pwd",
            RealName = $"t13p_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        try
        {
            Assert.True(_probe.Calls.Count > 0, "命中探针应收到 InsertByObject 分发");
            Assert.True(_probe.Calls.Exists(c => c.Op == DataFilterType.InsertByObject && !c.OldNull == false || c.Op == DataFilterType.InsertByObject),
                $"应存在 InsertByObject 调用,实际={string.Join(",", _probe.Calls.Select(c => c.Op))}");
            // 实证偏差(g10 §5 ⑥):SqlSugar 5.1.4.217 InsertByObject 的 oldData 实机非 null
            // (参照注释口径"Insert 为 null"与实机不符)——只断言 Insert 回调到达,不锁死 oldData 形态;
            // Update 的旧值可见由 P3 断言
            Assert.True(_probe.Calls.Any(c => c.Op == DataFilterType.InsertByObject), "InsertByObject 回调应到达探针");
            Assert.True(_orgProbe.Count == 0, "未命中探针(SysOrg)不应被触发");
        }
        finally
        {
            userRepo.DeletePhysicalByAsync(u => u.Id == user.Id).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void P2_多实现全分发()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // P1 已插过用户(测试方法序不保证)——本方法独立插一个
        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var before1 = _probe2.Count;
        var beforeOrg = _orgProbe.Count;
        var user = new SysUser
        {
            Account = $"t13p_u2_{_p}",
            Password = "t13p_placeholder_pwd",
            RealName = $"t13p_{_p}_2",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        try
        {
            Assert.True(_probe2.Count > before1, "第二个命中实现也应被分发(列表遍历,非首个短路)");
            Assert.True(_orgProbe.Count == beforeOrg, "未命中探针仍不应被触发");
        }
        finally
        {
            userRepo.DeletePhysicalByAsync(u => u.Id == user.Id).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void P3_Update时oldData旧值可见()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = $"t13p_u3_{_p}",
            Password = "t13p_placeholder_pwd",
            RealName = $"t13p_{_p}_3",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        try
        {
            _probe.Calls.Clear();
            user.RealName = $"t13p_{_p}_3_v2";
            userRepo.UpdateAsync(user).GetAwaiter().GetResult();
            var updateCall = _probe.Calls.FirstOrDefault(c => c.Op == DataFilterType.UpdateByObject);
            Assert.True(updateCall.Op == DataFilterType.UpdateByObject, $"应存在 UpdateByObject 调用,实际={string.Join(",", _probe.Calls.Select(c => c.Op))}");
            Assert.True(!updateCall.OldNull, "UpdateByObject 的 oldData 应为变更前旧值(非 null)");
        }
        finally
        {
            userRepo.DeletePhysicalByAsync(u => u.Id == user.Id).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void P4_空列表基线_零扩展点不炸()
    {
        // 纯函数口径:空列表任何 model 均安全(不触达探针)
        SqlSugarDataExecuting.Execute([], null, new DataFilterModel());
        SqlSugarDataExecuting.Execute(null!, null, new DataFilterModel());
        // EntityValue 为 null 的 model 也安全
        SqlSugarDataExecuting.Execute([new UserProbe()], null, new DataFilterModel());
    }

    public void Dispose() => _provider.Dispose();

    private static bool IsPostgresReachable()
    {
        try
        {
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(DbHost, DbPort);
            tcp.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
