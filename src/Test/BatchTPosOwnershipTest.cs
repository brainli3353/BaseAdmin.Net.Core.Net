using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Pos.Dto;
using AdminCore.Core.Extensions;
using AdminCore.Core.Security;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Pos;
using AdminCore.Core.Services.User;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 T(2.6 职位管理归属校验)测试(参照 Admin.NET D6002 语义,见 g14):
/// 非超管仅可操作自己创建的职位(Update/Delete);超管/匿名放行。
/// Claims 身份模拟(批 B 先例 FakeHttpContextAccessor);真实 PG 探针职位 finally 清理。
/// </summary>
public class BatchTPosOwnershipTest : IAsyncLifetime
{
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;
    private const int SuperAccountType = 999;
    private const int NormalAccountType = 777;

    private static readonly string P = Guid.NewGuid().ToString("N")[..8];

    private ServiceProvider? _provider;
    private FakeHttpContextAccessor? _accessor;
    private ISqlSugarClient? _db;
    private bool _pgReachable;
    private readonly List<long> _createdPosIds = new();

    public async Task InitializeAsync()
    {
        if (!IsPostgresReachable())
        {
            return;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = DbConnectionString,
                ["Yit:WorkerId"] = "1",
                ["Jwt:Secret"] = "t13s_test_secret_key_32bytes____",
                ["Jwt:Issuer"] = "t13s-test",
                ["Jwt:Audience"] = "t13s-test-api",
                ["Cryptogram:Key"] = "dDEzbl90ZXN0X2NyeXB0b2dyYW1fa2V5XzMyYnl0ZXM="
            })
            .Build();

        _accessor = new FakeHttpContextAccessor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHttpContextAccessor>(_accessor);
        services.AddSqlSugar(config);
        services.AddCache(config);
        services.AddProjectOptions(config);
        services.AddAutoServices(typeof(SysUserService).Assembly);
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ISqlSugarClient>();
        TestSchemaBootstrap.Ensure(_db);
        _pgReachable = true;
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
        if (_pgReachable && _db is not null)
        {
            // 原生 SQL 硬删本批探针职位(绕软删过滤器)
            foreach (var id in _createdPosIds)
            {
                _db.Ado.ExecuteCommand("delete from sys_pos where id = @id", new { id });
            }
        }

        _provider?.Dispose();
    }

    [Fact]
    public async Task T1_超管可操作他人职位()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        var posId = await CreatePosAsync("t13t_p1_" + P, "t13t_p1_" + P);
        SetUser(999001, SuperAccountType);
        await UpdatePosOkAsync(posId);
        await DeletePosOkAsync(posId);
    }

    [Fact]
    public async Task T2_创建者可操作自己的职位()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 创建者 A 全程操作(Update+Delete 均创建者本人)
        SetUser(777001, NormalAccountType);
        var posId = await CreatePosAsync("t13t_p2_" + P, "t13t_p2_" + P);
        await UpdatePosOkAsync(posId);
        await DeletePosOkAsync(posId);
    }

    [Fact]
    public async Task T3_非超管越权拒绝_Update与Delete()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 创建者 A 建职位
        SetUser(777003, NormalAccountType);
        var posId = await CreatePosAsync("t13t_p3_" + P, "t13t_p3_" + P);

        // 非超管 B 越权 Update → 400
        SetUser(777004, NormalAccountType);
        var svc = Service();
        var ex = await Assert.ThrowsAsync<AppException>(
            () => svc.UpdateAsync(new UpdatePosInput(posId, "改名", "t13t_p3_" + P, 0, null, 1)));
        Assert.True(ex.Code == 400, $"越权 Update 应业务码 400,实际={ex.Code}");

        // 非超管 B 越权 Delete → 400
        var exDel = await Assert.ThrowsAsync<AppException>(() => svc.DeleteAsync(posId));
        Assert.True(exDel.Code == 400, $"越权 Delete 应业务码 400,实际={exDel.Code}");

        // 职位未被删除(拒绝后原行仍在)
        var still = await _db!.Queryable<SysPos>().Where(p => p.Id == posId).FirstAsync();
        Assert.True(still is not null, "越权拒绝后职位应仍存在");
    }

    [Fact]
    public async Task T4_匿名放行()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 无 Claims(测试基线)→ 放行(与仓内 4 处既有范式一致)
        _accessor!.HttpContext = null;
        var posId = await CreatePosAsync("t13t_p4_" + P, "t13t_p4_" + P);
        await UpdatePosOkAsync(posId);
        await DeletePosOkAsync(posId);
    }

    [Fact]
    public async Task T5_既有契约回归_关联用户拒删()
    {
        if (!_pgReachable) Assert.Fail("PG 不可达,无法执行本批用例");

        // 探针用户挂职位 → 删职位应拒(既有契约不回归)
        var posId = await CreatePosAsync("t13t_p5_" + P, "t13t_p5_" + P);
        var userId = await CreateUserWithPosAsync(posId);
        try
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => Service().DeleteAsync(posId));
            Assert.True(ex.Code == 400, $"关联用户拒删应业务码 400,实际={ex.Code}");
        }
        finally
        {
            _db!.Ado.ExecuteCommand("delete from sys_user where id = @id", new { id = userId });
        }
    }

    // ---------- 工具 ----------

    private SysPosService Service() => _provider!.GetRequiredService<SysPosService>();

    private void SetUser(long userId, int accountType)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, userId.ToString()),
            new Claim(JwtClaimNames.Account, $"t13t_{userId}"),
            new Claim(JwtClaimNames.AccountType, accountType.ToString()),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        _accessor!.HttpContext = context;
    }

    private async Task<long> CreatePosAsync(string name, string code)
    {
        var id = await Service().AddAsync(new AddPosInput(name, code, 0, null, 1));
        _createdPosIds.Add(id);
        return id;
    }

    private async Task UpdatePosOkAsync(long posId)
    {
        await Service().UpdateAsync(new UpdatePosInput(posId, "改名", "t13t_upd_" + P + posId, 0, null, 1));
    }

    private async Task DeletePosOkAsync(long posId)
    {
        await Service().DeleteAsync(posId);
        // 批 BK 泄漏修复:成功软删后**不**从硬删列表移除——DisposeAsync 的原生 SQL 硬删绕软删过滤器,
        // 对已软删行同样生效;旧写法移除后每轮全量漏 3 行软删探针(sys_pos 实测累积 179 行)。
    }

    private async Task<long> CreateUserWithPosAsync(long posId)
    {
        var account = "t13t_user_" + P + posId;
        var user = new SysUser
        {
            Account = account,
            RealName = "批T探针",
            Password = AdminCore.Core.Security.Pbkdf2Helper.HashPassword("x"),
            AccountType = NormalAccountType,
            PosId = posId,
            Status = 1,
            OrgId = 0
        };
        await _db!.Insertable(user).ExecuteCommandAsync();
        return user.Id;
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

// Claims 模拟(批 B 先例)
public sealed class FakeHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
