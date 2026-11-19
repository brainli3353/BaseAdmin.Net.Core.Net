#region License
// ====================================================================
//  copyright © `` AdminCore 基础 Admin 框架.
//  创建时间:2026-10-04
//  文件名:BatchCISortTest.cs
//  备　　注:批 CI(表头点击排序,全栈)测试
// ====================================================================
#endregion
using System.Net.Sockets;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.User;
using AdminCore.Core.Services.User.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 CI「表头点击排序(全栈)」测试:被测主体 = <see cref="DynamicQueryExtensions.ApplyDynamicSort{T}"/>
/// (两形式:单默认序表达式 / 自定义默认序链)+ SysUserService.PageAsync 端点级接线。
/// </summary>
/// <remarks>
/// WHY 真实 PG:排序语义的断言对象是"用户输入 → SQL ORDER BY"的合成结果——方向是否真生效、
/// 非法字段是否真被白名单拦(且表还在)、默认序是否真保持——只有真库验得出(同批 AG 口径)。
/// PG 不可达按既有口径 Assert.Fail 诚实降级(禁止冒充通过)。
/// <para>
/// 探针纪律:99_000_000_009_xx 段(批 CI 专用),固定 Id 幂等重建;finally 物理清理
/// (sys_user 探针行;探针不绑角色/OTP,无关联行)。
/// </para>
/// </remarks>
public class BatchCISortTest
{
    private readonly ITestOutputHelper _output;

    public BatchCISortTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 Batch CH/AG 同源,dev only;Timeout=10 让不可达场景快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    // 探针固定 Id(99_000_000_009_xx 段 = 批 CI);Account 前缀 ci_ 便于断言相对序
    private const long ProbeA = 99_000_000_009_01;   // Account=ci_a
    private const long ProbeB = 99_000_000_009_02;   // Account=ci_b
    private const long ProbeC = 99_000_000_009_03;   // Account=ci_c

    private const int NormalAccountType = 777;

    /// <summary>① 默认序保持:orderBy 为空 ⇒ 按默认表达式(Id 降序)排,行为与接线前等价。</summary>
    [Fact(DisplayName = "默认序保持orderBy为空")]
    public async Task 默认序保持orderBy为空()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ids = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort(null, null, u => u.Id, OrderByType.Desc)
            .Select(u => u.Id)
            .ToListAsync();

        Assert.True(ids.Count >= 2, $"需至少 2 行才能验排序方向,实际 {ids.Count} 行");
        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] < ids[i - 1], $"orderBy 为空应 Id 降序,但第 {i} 项 {ids[i]} >= 前项 {ids[i - 1]}");
        }
    }

    /// <summary>② 合法字段方向生效:Account 升/降序严格单调,且两序集合一致(全量非取样)。</summary>
    [Fact(DisplayName = "合法字段方向生效")]
    public async Task 合法字段方向生效()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var asc = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort("Account", false, u => u.Id, OrderByType.Desc)
            .Select(u => u.Account)
            .ToListAsync();
        var desc = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort("account", true, u => u.Id, OrderByType.Desc)
            .Select(u => u.Account)
            .ToListAsync();

        Assert.True(asc.Count >= 2, $"需至少 2 行才能验排序方向,实际 {asc.Count} 行");
        var onlyAsc = asc.Except(desc).ToList();
        var onlyDesc = desc.Except(asc).ToList();
        Assert.True(asc.OrderBy(x => x).SequenceEqual(desc.OrderBy(x => x).ToList()),
            $"两序应覆盖同一集合(同集合,序相反):asc={asc.Count} desc={desc.Count} 仅asc={string.Join(",", onlyAsc)} 仅desc={string.Join(",", onlyDesc)}");
        for (var i = 1; i < asc.Count; i++)
        {
            Assert.True(string.CompareOrdinal(asc[i], asc[i - 1]) >= 0,
                $"Account asc 应单调不减,但第 {i} 项 {asc[i]} < 前项 {asc[i - 1]}");
        }
        for (var i = 1; i < desc.Count; i++)
        {
            Assert.True(string.CompareOrdinal(desc[i], desc[i - 1]) <= 0,
                $"Account desc 应单调不增,但第 {i} 项 {desc[i]} > 前项 {desc[i - 1]}");
        }

        _output.WriteLine($"合法字段排序 OK:asc {asc.Count} 行 / desc {desc.Count} 行,首项 asc={asc[0]} desc={desc[0]}");
    }

    /// <summary>③ desc=null 默认降序(参照口径):仅传 orderBy ⇒ 降序。</summary>
    [Fact(DisplayName = "desc缺省按降序")]
    public async Task desc缺省按降序()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ids = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort("Id", null, u => u.Id, OrderByType.Desc)
            .Select(u => u.Id)
            .ToListAsync();

        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] < ids[i - 1], $"desc=null 应降序,但第 {i} 项 {ids[i]} >= 前项 {ids[i - 1]}");
        }
    }

    /// <summary>④ 非法字段拒绝:注入串 ⇒ AppException(errCode=dynamic_query.order_by_not_allowed),且表仍在(防"注入其实成功"假绿)。</summary>
    [Fact(DisplayName = "非法字段拒绝且表仍在")]
    public async Task 非法字段拒绝且表仍在()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var injection = "Id; DROP TABLE sys_user--";
        var ex = await Assert.ThrowsAsync<AppException>(() => scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort(injection, true, u => u.Id, OrderByType.Desc)
            .Select(u => u.Id)
            .ToListAsync());

        Assert.Equal(ErrorCodes.DynamicQueryOrderByNotAllowed, ex.ErrorCode);
        _output.WriteLine($"非法字段拒绝 OK:errCode={ex.ErrorCode}");

        // 注入串之后表仍在、仍可查
        var count = await scope.Db.Queryable<SysUser>().CountAsync();
        Assert.True(count > 0, "注入串异常后 sys_user 应仍可查");
    }

    /// <summary>⑤ 默认序链形式:orderBy 为空 ⇒ 原样执行默认链(OrderNo asc + Id desc 形态,以 RealName/Id 模拟双列)。</summary>
    [Fact(DisplayName = "默认序链形式orderBy为空走链")]
    public async Task 默认序链形式orderBy为空走链()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var ids = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort(null, null, q => q.OrderBy(u => u.Account).OrderByDescending(u => u.Id))
            .Select(u => u.Id)
            .ToListAsync();

        // 全量验证:按 (Account asc, Id desc) 投影单调
        var pairs = await scope.Db.Queryable<SysUser>()
            .Where(u => !u.IsDelete)
            .ApplyDynamicSort(null, null, q => q.OrderBy(u => u.Account).OrderByDescending(u => u.Id))
            .Select(u => new { u.Account, u.Id })
            .ToListAsync();
        for (var i = 1; i < pairs.Count; i++)
        {
            var cmp = string.CompareOrdinal(pairs[i].Account, pairs[i - 1].Account);
            Assert.True(cmp >= 0 || (cmp == 0 && pairs[i].Id < pairs[i - 1].Id),
                $"默认序链应 Account asc + Id desc,但第 {i} 项 ({pairs[i].Account},{pairs[i].Id}) 违反");
        }
        Assert.NotEmpty(ids);
    }

    /// <summary>⑥ 端点级:SysUserService.PageAsync 接 orderBy=Account 升序 ⇒ 探针相对序 a&lt;b&lt;c;无 orderBy ⇒ Id 降序(探针 c,b,a)。</summary>
    [Fact(DisplayName = "端点级排序与默认序")]
    public async Task 端点级排序与默认序()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        await EnsureProbesAsync(scope);
        var svc = scope.Provider.GetRequiredService<SysUserService>();

        try
        {
            // 升序:探针相对序 a < b < c
            // S-G B1c:分页上限 200 后,PageSize=500 会被钳制 ⇒ 不能靠"一页拉全表"再找探针;
            // 用 Account 模糊收窄到探针集(ci_),三探针必在页内,排序语义不变。
            var ascPage = await svc.PageAsync(new PageUserInput("ci_", 1, 50, null, "Account", false));
            var ascIdx = ascPage.List.Select(u => u.Id).ToList();
            var ia = ascIdx.IndexOf(ProbeA);
            var ib = ascIdx.IndexOf(ProbeB);
            var ic = ascIdx.IndexOf(ProbeC);
            Assert.True(ia >= 0 && ib > ia && ic > ib,
                $"orderBy=Account asc 应探针序 a<b<c,实际索引 a={ia} b={ib} c={ic}");

            // 降序:探针相对序 c < b < a
            var descPage = await svc.PageAsync(new PageUserInput("ci_", 1, 50, null, "Account", true));
            var descIdx = descPage.List.Select(u => u.Id).ToList();
            var da = descIdx.IndexOf(ProbeA);
            var db2 = descIdx.IndexOf(ProbeB);
            var dc = descIdx.IndexOf(ProbeC);
            Assert.True(dc >= 0 && db2 > dc && da > db2,
                $"orderBy=Account desc 应探针序 c<b<a,实际索引 a={da} b={db2} c={dc}");

            // 无 orderBy:默认 Id 降序 ⇒ 探针相对序 c(03)<b(02)<a(01) 的反向 = 索引 a<b<c
            var defaultPage = await svc.PageAsync(new PageUserInput("ci_", 1, 50));
            var defIdx = defaultPage.List.Select(u => u.Id).ToList();
            var ea = defIdx.IndexOf(ProbeA);
            var eb = defIdx.IndexOf(ProbeB);
            var ec = defIdx.IndexOf(ProbeC);
            Assert.True(ec >= 0 && eb > ec && ea > eb,
                $"无 orderBy 应 Id 降序(列表序 c,b,a),实际索引 a={ea} b={eb} c={ec}");
        }
        finally
        {
            await CleanupAsync(scope);
        }
    }

    /// <summary>⑦ 端点级非法字段:PageAsync(orderBy 注入串) ⇒ AppException(errCode=dynamic_query.order_by_not_allowed)。</summary>
    [Fact(DisplayName = "端点级非法字段拒绝")]
    public async Task 端点级非法字段拒绝()
    {
        await using var scope = await DbScope.CreateAsync();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var svc = scope.Provider.GetRequiredService<SysUserService>();

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.PageAsync(new PageUserInput(null, 1, 10, null, "Account; DROP TABLE sys_user--", true)));
        Assert.Equal(ErrorCodes.DynamicQueryOrderByNotAllowed, ex.ErrorCode);
        _output.WriteLine($"端点级非法字段拒绝 OK:errCode={ex.ErrorCode}");
    }

    // ─────────────────────────────────────────────────────────────
    // 探针
    // ─────────────────────────────────────────────────────────────

    private static async Task EnsureProbesAsync(DbScope scope)
    {
        var db = scope.Db;
        var probes = new List<SysUser>
        {
            new() { Id = ProbeA, Account = "ci_a", Password = "probe-dummy-never-login", RealName = "CI探针A", Status = 0, AccountType = NormalAccountType, TenantId = SqlSugarConst.DefaultTenantId },
            new() { Id = ProbeB, Account = "ci_b", Password = "probe-dummy-never-login", RealName = "CI探针B", Status = 0, AccountType = NormalAccountType, TenantId = SqlSugarConst.DefaultTenantId },
            new() { Id = ProbeC, Account = "ci_c", Password = "probe-dummy-never-login", RealName = "CI探针C", Status = 0, AccountType = NormalAccountType, TenantId = SqlSugarConst.DefaultTenantId }
        };
        var existing = new HashSet<long>(await db.Queryable<SysUser>().Where(u => u.Id >= ProbeA && u.Id <= ProbeC).Select(u => u.Id).ToListAsync());
        var toInsert = probes.Where(p => !existing.Contains(p.Id)).ToList();
        if (toInsert.Count > 0)
        {
            await db.Insertable(toInsert).ExecuteCommandAsync();
        }
    }

    private static async Task CleanupAsync(DbScope scope)
    {
        await scope.Db.Deleteable<SysUser>().Where(u => u.Id >= ProbeA && u.Id <= ProbeC).ExecuteCommandAsync();
        var residual = await scope.Db.Queryable<SysUser>().CountAsync(u => u.Id >= ProbeA && u.Id <= ProbeC);
        Assert.Equal(0, residual);
    }

    // ─────────────────────────────────────────────────────────────
    // DbScope(与 BatchCHMenuStatusTest/BatchAGDynamicQueryTest 同口径:不可达诚实降级)
    // ─────────────────────────────────────────────────────────────
    private sealed class DbScope : IAsyncDisposable
    {
        public ISqlSugarClient Db { get; private set; } = null!;

        public ServiceProvider Provider { get; private set; } = null!;

        public bool PgReachable { get; private set; }

        public static async Task<DbScope> CreateAsync()
        {
            var scope = new DbScope();
            if (!await IsPostgresReachableAsync())
            {
                scope.Provider = new ServiceCollection().BuildServiceProvider();
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
            services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor()); // 无 Claims ⇒ 未登录口径(机构范围不过滤)
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config); // 无 ConnectionStrings:Redis → 内存实现
            services.AddSignalR(); // SysUserService → SysOnlineUserService → IHubContext(无连接时推送 no-op,同批 AF/AS 口径)
            services.AddAutoServices(typeof(SysUserService).Assembly);

            scope.Provider = services.BuildServiceProvider();
            scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
            TestSchemaBootstrap.Ensure(scope.Db);
            scope.PgReachable = true;
            return scope;
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 Batch CH 口径)。</summary>
        private static async Task<bool> IsPostgresReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", 5432, timeout.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
