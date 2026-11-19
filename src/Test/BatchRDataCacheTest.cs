using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.User;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 R(3.5 SqlSugar 二级缓存)测试(参照 Admin.NET SqlSugarCache 移植,见 g12):
/// 数据缓存适配器闭环——.WithCache() 查询走适配器/增删改自动失效(RemoveDataCache 经
/// GetAllKey 枚举+逐键 Remove)/前缀隔离。框架层零默认启用(业务查询 opt-in),
/// 本测试经探针 .WithCache() 证明机制接通(与批 P 同型)。
/// 真实 PG 探针用户 finally 清理;PG 不可达时用例降级 Assert.Fail。
/// </summary>
public class BatchRDataCacheTest : IDisposable
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    private readonly ServiceProvider _provider;
    private readonly ISqlSugarClient _db;
    private readonly ICacheService _cache;
    private readonly bool _pgReachable;
    private readonly string _p = Guid.NewGuid().ToString("N");
    private long _userId;

    public BatchRDataCacheTest()
    {
        if (!IsPostgresReachable())
        {
            _provider = new ServiceCollection().BuildServiceProvider();
            _db = null!;
            _cache = null!;
            return;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "t13r_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "t13r-test",
                ["Jwt:Audience"] = "t13r-test-api",
                ["Cryptogram:Key"] = "dDEzbl90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM="
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSqlSugar(config);
        services.AddCache(config);
        services.AddProjectOptions(config);
        services.AddAutoServices(typeof(SysUserService).Assembly);
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(_db);

        _cache = _provider.GetRequiredService<ICacheService>();

        var userRepo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var user = new SysUser
        {
            Account = $"t13r_u_{_p}",
            Password = Pbkdf2Helper.HashPassword("T13r_Old!pass1"),
            RealName = $"t13r_{_p}",
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        userRepo.InsertAsync(user).GetAwaiter().GetResult();
        _userId = user.Id;
        _pgReachable = true;
    }

    [Fact]
    public void R1_WithCache查询_首查触发适配器写缓存()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 挂接实证:scope 默认连接的 DataCache.Servie 是适配器(挂点接通)
        var scope = (SqlSugarScope)_db;
        Assert.True(scope.DataCache.Servie is AdminCore.Core.SqlSugar.SqlSugarDataCacheService,
            $"DataCache.Servie 应为适配器,实际={scope.DataCache.Servie?.GetType().Name ?? "null"}");

        // 首查 .WithCache():DB 查询 + 写数据缓存键(经适配器 GetOrCreate)
        var first = _db.Queryable<SysUser>().WithCache().First(u => u.Id == _userId);
        Assert.True(first is not null && first.Id == _userId, "首查返回探针行");

        // 缓存键已写入底层(前缀 sys_sqlSugar: + SqlSugar 内部键形态 SqlSugarDataCache.<表名>...)。
        // 用 scope 挂载的适配器实例取登记表(登记表是实例级,新建实例登记表为空)
        var adapter = (AdminCore.Core.SqlSugar.SqlSugarDataCacheService)scope.DataCache.Servie;
        var keys = adapter.GetAllKey<object>().ToList();
        Assert.True(keys.Count > 0, $"首查后数据缓存键登记应非空,实际={keys.Count}");
        Assert.True(keys.All(k => k.StartsWith("sys_sqlSugar:")),
            $"所有数据缓存键须带 sys_sqlSugar: 前缀,实际={string.Join(",", keys)}");
        // 键在底层真实存在(经业务 ICacheService 直读)
        Assert.True(_cache.ExistKeyAsync(keys[0]).GetAwaiter().GetResult(), "登记键在底层缓存真实存在");
    }

    [Fact]
    public void R2_Update自动失效_再查见新值()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var repo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var u = repo.GetFirstAsync(u => u.Id == _userId).GetAwaiter().GetResult();
        Assert.True(u is not null, "探针行存在");

        // 缓存预热(.WithCache() 写数据缓存)
        _db.Queryable<SysUser>().WithCache().First(x => x.Id == _userId);

        // 更新(IsAutoRemoveDataCache=true → 自动 RemoveDataCache:经 GetAllKey 枚举 + 逐键 Remove)
        u.RealName = $"t13r_updated_{_p}";
        repo.UpdateAsync(u).GetAwaiter().GetResult();

        // 再查 .WithCache():缓存已失效 → 回源 DB 见新值
        var after = _db.Queryable<SysUser>().WithCache().First(x => x.Id == _userId);
        Assert.True(after is not null && after.RealName == $"t13r_updated_{_p}",
            $"更新后 .WithCache() 查询应见新值(自动失效),实际={after?.RealName}");
    }

    [Fact]
    public void R3_Insert自动失效_新增行可见()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var repo = _provider.GetRequiredService<SqlSugarRepository<SysUser>>();
        var missKey = $"t13r_miss_{_p}";
        // 预热"按 Account 查不存在"的空结果缓存
        _db.Queryable<SysUser>().WithCache().First(u => u.Account == missKey);

        // 插入新行(IsAutoRemoveDataCache → 该实体查询缓存失效)
        var nu = new SysUser
        {
            Account = missKey,
            Password = Pbkdf2Helper.HashPassword("T13r_New!pass1"),
            RealName = missKey,
            Status = 0,
            AccountType = 777,
            TenantId = SqlSugarConst.DefaultTenantId
        };
        repo.InsertAsync(nu).GetAwaiter().GetResult();
        var newId = nu.Id;

        // 再查:见新行(缓存已失效回源)
        var hit = _db.Queryable<SysUser>().WithCache().First(u => u.Account == missKey);
        Assert.True(hit is not null && hit.Id == newId, "插入后 .WithCache() 查询应见新行(自动失效)");

        // 清理新行
        repo.DeletePhysicalByAsync(x => x.Id == newId).GetAwaiter().GetResult();
    }

    [Fact]
    public void R4_缓存命中_二查不回源()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 预热缓存
        _db.Queryable<SysUser>().WithCache().First(u => u.Id == _userId);

        // 二查 .WithCache():缓存命中(经适配器 Get 路径),结果一致
        var cached = _db.Queryable<SysUser>().WithCache().First(u => u.Id == _userId);
        Assert.True(cached is not null && cached.Id == _userId, "二查返回探针行");

        // 命中语义实证:原生 SQL 直改源行(不经 SqlSugar 更新路径 → 不触发 IsAutoRemoveDataCache 自动失效)
        // → 缓存仍载预热值 → .WithCache() 返回缓存值(不回源)= 命中证明
        var directValue = $"t13r_direct_{_p}";
        _db.Ado.ExecuteCommand("UPDATE sys_user SET \"RealName\" = @v WHERE id = @id",
            new SugarParameter("@v", directValue), new SugarParameter("@id", _userId));
        var fromCache = _db.Queryable<SysUser>().WithCache().First(u => u.Id == _userId);
        Assert.True(fromCache is not null && fromCache.RealName != directValue,
            $"原生 SQL 改源后 .WithCache() 仍返回缓存值(命中不回源),实际={fromCache?.RealName}");

        // 手工清数据缓存(scope 挂载实例的登记表)→ 再查回源 → 见直改值
        var scope2 = (SqlSugarScope)_db;
        var adapter = (AdminCore.Core.SqlSugar.SqlSugarDataCacheService)scope2.DataCache.Servie;
        foreach (var k in adapter.GetAllKey<object>().ToList())
        {
            adapter.Remove<object>(k);
        }
        var afterClear = _db.Queryable<SysUser>().WithCache().First(u => u.Id == _userId);
        Assert.True(afterClear is not null && afterClear.RealName == directValue,
            $"清缓存后回源,应见原生 SQL 直改值,实际={afterClear?.RealName}");
    }

    public void Dispose()
    {
        if (_pgReachable)
        {
            _db.Deleteable<SysUser>().Where(u => u.Account == $"t13r_u_{_p}").ExecuteCommand();
        }
        _provider.Dispose();
    }

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
