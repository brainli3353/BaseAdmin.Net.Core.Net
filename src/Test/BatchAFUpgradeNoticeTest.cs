using System.Net.Sockets;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Extensions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.Notice;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit.Abstractions;

namespace AdminCore.Test;

/// <summary>
/// 批 AF(4.11 升级公告)测试:被测主体 = <see cref="SysNoticeService.GetUnReadListAsync"/> 与
/// <see cref="SysNoticeService.GetLastAsync"/> 对配置开关 <c>sys_upgrade</c>(<see cref="SysConfigService.SysUpgrade"/>)的过滤口径
/// ——升级公告复用 SysNotice(NoticeTypeEnum.Upgrade=3),开关关 = 该类型不参与"登录后未读提示",其余类型照常。
/// </summary>
/// <remarks>
/// WHY 全部用例需真实 PG:断言的正是"通知接收行 × 通知类型 × 全局软删过滤器 × 配置缓存(KeyConfig+code)"的合成结果,
/// 内存替身证明不了 SqlSugar 生成的 LeftJoin/WhereIF 真把 Upgrade 排除掉了。
/// PG 不可达时 Assert.Fail 显式降级(禁止冒充通过,口径同 BatchADDatabaseTest)。
/// 用例零污染:探针标题前缀 <c>afn_{guid}</c>、探针用户固定 Id(dsh_af 前缀账号)、finally 物理删公告/接收行/用户;
/// **配置开关按初值还原**(初值不存在 → 删掉本用例新增的临时行,不留软删残行)。
/// </remarks>
public class BatchAFUpgradeNoticeTest
{
    private readonly ITestOutputHelper _output;

    public BatchAFUpgradeNoticeTest(ITestOutputHelper output)
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

    // 探针用户(固定 Id,幂等重建;Account 带 dsh_af 前缀防与存量撞名)
    private const long ReaderId = 99_000_000_003_02;         // 未读列表的"当前用户"
    private const string ReaderAccount = "dsh_af_notice_reader";
    private const long PublisherId = 99_000_000_003_01;      // 公告发布人(仅快照字段,无归属校验依赖)
    private const string PublisherName = "探针升级发布者";

    // 被开关控制的配置 Code(与 SysNoticeService 内读取的常量同源,避免写错字面量)
    private const string SwitchCode = SysConfigService.SysUpgrade;

    // ─────────────────────────────────────────────────────────────
    // 1. 开关开启:Upgrade 与 Notice 两条都进未读
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关开启_升级公告进未读()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        long upgradeId = 0, noticeId = 0, createdConfigId = 0;
        var initial = await ReadSwitchAsync(scope);
        try
        {
            await EnsureReaderAsync(scope);

            createdConfigId = await SetSwitchAsync(scope, "True");
            (upgradeId, noticeId) = await SeedNoticesAsync(scope, prefix);

            // 置值必须走写路径(Add/Update 变更即失效 KeyConfig 缓存键),此处顺带读出证明确实生效
            using var configUnit = scope.NewScope();
            var configSvc = configUnit.ServiceProvider.GetRequiredService<SysConfigService>();
            Assert.Equal("True", await configSvc.GetConfigValueByCodeAsync(SwitchCode));

            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
            var unread = await svc.GetUnReadListAsync();

            Assert.Contains(unread, n => n.Id == upgradeId);
            Assert.Contains(unread, n => n.Id == noticeId);
            Assert.Equal((int)NoticeTypeEnum.Upgrade, unread.First(n => n.Id == upgradeId).Type);
            Assert.Equal((int)NoticeTypeEnum.Notice, unread.First(n => n.Id == noticeId).Type);

            _output.WriteLine($"判定:开关 True → 未读 {unread.Count} 条,含 Upgrade({upgradeId})与 Notice({noticeId})");
        }
        finally
        {
            await CleanupAsync(scope, initial, createdConfigId, upgradeId, noticeId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 开关关闭:Upgrade 被排除,Notice 保留
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关关闭_升级公告被排除其余保留()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        long upgradeId = 0, noticeId = 0, createdConfigId = 0;
        var initial = await ReadSwitchAsync(scope);
        try
        {
            await EnsureReaderAsync(scope);

            createdConfigId = await SetSwitchAsync(scope, "False");
            (upgradeId, noticeId) = await SeedNoticesAsync(scope, prefix);

            using var configUnit = scope.NewScope();
            var configSvc = configUnit.ServiceProvider.GetRequiredService<SysConfigService>();
            Assert.Equal("False", await configSvc.GetConfigValueByCodeAsync(SwitchCode));

            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();
            var unread = await svc.GetUnReadListAsync();

            Assert.Contains(unread, n => n.Id == noticeId);
            Assert.DoesNotContain(unread, n => n.Id == upgradeId);           // Upgrade 被排除
            Assert.Equal(1, unread.Count(n => n.Id == noticeId || n.Id == upgradeId)); // 探针行恰命中一条

            _output.WriteLine($"判定:开关 False → 未读只剩 Notice({noticeId}),Upgrade({upgradeId})被排除");
        }
        finally
        {
            await CleanupAsync(scope, initial, createdConfigId, upgradeId, noticeId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 开关值非法/空串:fail-open(仍提示),与 sys_oplog 同口径
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 开关非法或空串_failOpen仍提示()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        long upgradeId = 0, noticeId = 0, createdConfigId = 0;
        var initial = await ReadSwitchAsync(scope);
        try
        {
            await EnsureReaderAsync(scope);

            // 先置 False 证明"排除"生效(基线),再置非法值证明开关确实回到了 fail-open 提示
            createdConfigId = await SetSwitchAsync(scope, "False");
            (upgradeId, noticeId) = await SeedNoticesAsync(scope, prefix);

            using var configUnit = scope.NewScope();
            var configSvc = configUnit.ServiceProvider.GetRequiredService<SysConfigService>();

            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();

            Assert.DoesNotContain(await svc.GetUnReadListAsync(), n => n.Id == upgradeId); // 基线:False 排除

            // ① 非法值 "abc":bool.TryParse 失败 → 视为开启(宁可提示不误吞,与 sys_oplog 同口径)
            await SetSwitchAsync(scope, "abc");
            Assert.Equal("abc", await configSvc.GetConfigValueByCodeAsync(SwitchCode)); // 写路径已失效缓存,读到新值
            Assert.Contains(await svc.GetUnReadListAsync(), n => n.Id == upgradeId);

            // ② 空串 "":同样解析失败 → 仍 fail-open
            // (真"配置行缺失"走的是**同一个布尔表达式**的 !bool.TryParse(null,...) 分支,故与空串等效覆盖)
            await SetSwitchAsync(scope, string.Empty);
            Assert.Equal(string.Empty, await configSvc.GetConfigValueByCodeAsync(SwitchCode));
            Assert.Contains(await svc.GetUnReadListAsync(), n => n.Id == upgradeId);

            _output.WriteLine("判定:False 时排除;值 abc / 空串时仍含 Upgrade(fail-open)");
        }
        finally
        {
            await CleanupAsync(scope, initial, createdConfigId, upgradeId, noticeId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. GetLastAsync 同口径:开关关时最新一条若为 Upgrade,则返回下一条非 Upgrade
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task 最新一条_开关关时跳过升级公告()
    {
        using var scope = new DbScope();
        if (!scope.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        var prefix = NewProbePrefix();
        long upgradeId = 0, noticeId = 0, createdConfigId = 0;
        var initial = await ReadSwitchAsync(scope);
        try
        {
            await EnsureReaderAsync(scope);

            createdConfigId = await SetSwitchAsync(scope, "True");
            (upgradeId, noticeId) = await SeedNoticesAsync(scope, prefix);

            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysNoticeService>();

            // 前置取证:开关开启时 GetLastAsync 取的是全表最新一条,而探针的 Upgrade 行正是全表最新(CreateTime 抬到 +5min+1s)
            // ——不先钉住这一点,"开关关时跳过它"的断言就没有对照物(可能只是取到了别的行)。
            var lastOn = await svc.GetLastAsync();
            Assert.NotNull(lastOn);
            Assert.Equal(upgradeId, lastOn!.Id);
            Assert.Equal((int)NoticeTypeEnum.Upgrade, lastOn.Type);

            // 开关关闭:最新一条(Upgrade)被排除 → 落到下一条非 Upgrade(实现为 WhereIF 过滤后取首条,
            // 不是"取最新再判类型后置 null",故此处期望的是 Notice 行而非 null)
            await SetSwitchAsync(scope, "False");
            var lastOff = await svc.GetLastAsync();
            Assert.NotNull(lastOff);
            Assert.Equal(noticeId, lastOff!.Id);
            Assert.Equal((int)NoticeTypeEnum.Notice, lastOff.Type);

            _output.WriteLine($"判定:开关 True→GetLast={upgradeId}(Upgrade);False→GetLast={noticeId}(Notice),跳过最新 Upgrade");
        }
        finally
        {
            await CleanupAsync(scope, initial, createdConfigId, upgradeId, noticeId);
        }
    }

    // ── 造数与清理 ────────────────────────────────────────────────────────────

    private static string NewProbePrefix() => $"afn_{Guid.NewGuid():N}";

    /// <summary>探针读者用户幂等重建(固定 Id;未读列表只认 Claim 的 UserId,用户行本身不参与查询)。</summary>
    private static async Task EnsureReaderAsync(DbScope scope)
    {
        var exists = await scope.Db.Queryable<SysUser>().AnyAsync(u => u.Id == ReaderId);
        if (!exists)
        {
            await scope.Db.Insertable(new SysUser
            {
                Id = ReaderId,
                Account = ReaderAccount,
                RealName = "探针升级读者",
                Password = "probe-not-used",
                Status = 0,
                AccountType = 777,
                TokenVersion = 1
            }).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 造两条已发布公告(Upgrade 一条 + Notice 一条)并给读者各造一条未读接收行。
    /// WHY 直插实体而非走 <see cref="SysNoticeService.AddAsync"/>:服务层 ToType 把非 1/2 的类型**归一为 Notice**
    /// (<c>type is 1 or 2 ? (NoticeTypeEnum)type : NoticeTypeEnum.Notice</c>)⇒ 经服务层**建不出 Type=Upgrade 的公告**,
    /// 而 Upgrade 恰是本用例的被测类型(该可达性缺口在报告中呈报,测试内不改生产代码)。
    /// WHY CreateTime 显式抬到"当前 + 5 分钟":GetLastAsync 取的是**全表**最新一条(不按用户/标题过滤),
    /// 把探针行抬到表内最新,"开关关 → 跳过最新 Upgrade 取下一条"才是确定性断言(不受表内其他行创建时序影响)。
    /// </summary>
    private static async Task<(long UpgradeId, long NoticeId)> SeedNoticesAsync(DbScope scope, string prefix)
    {
        var db = scope.Db;
        var baseTime = DateTime.Now.AddMinutes(5);

        var upgrade = new SysNotice
        {
            Title = $"{prefix}_升级公告",
            Content = "批 AF 升级公告正文",
            Type = NoticeTypeEnum.Upgrade,
            Status = NoticeStatusEnum.Public,
            PublicUserId = PublisherId,
            PublicUserName = PublisherName,
            PublicOrgId = 0,
            PublicTime = baseTime,
            CreateTime = baseTime.AddSeconds(1) // 比 Notice 行更新 ⇒ 全表最新一条恰为 Upgrade
        };
        await db.Insertable(upgrade).ExecuteCommandAsync();

        var notice = new SysNotice
        {
            Title = $"{prefix}_普通通知",
            Content = "批 AF 普通通知正文",
            Type = NoticeTypeEnum.Notice,
            Status = NoticeStatusEnum.Public,
            PublicUserId = PublisherId,
            PublicUserName = PublisherName,
            PublicOrgId = 0,
            PublicTime = baseTime,
            CreateTime = baseTime
        };
        await db.Insertable(notice).ExecuteCommandAsync();

        foreach (var noticeId in new[] { upgrade.Id, notice.Id })
        {
            await db.Insertable(new SysNoticeUser
            {
                NoticeId = noticeId,
                UserId = ReaderId,
                ReadStatus = NoticeUserStatusEnum.Unread
            }).ExecuteCommandAsync();
        }

        return (upgrade.Id, notice.Id);
    }

    /// <summary>读开关行现状(可见行;记录初值供还原)。</summary>
    private static async Task<SwitchSnapshot> ReadSwitchAsync(DbScope scope)
    {
        var row = await scope.Db.Queryable<SysConfig>().FirstAsync(c => c.Code == SwitchCode);
        return row is null ? new SwitchSnapshot(false, 0, null) : new SwitchSnapshot(true, row.Id, row.Value);
    }

    /// <summary>
    /// 置开关值:**走 SysConfigService 写路径**——Update 变更即删缓存键(CacheConst.KeyConfig+code),
    /// Add 新建时缓存本来就没有该键。WHY 不直改 SQL:配置读路径带 7 天 TTL 缓存,直改表不清键会读到旧值,
    /// 用例要么假绿(以为关了其实开着)要么假红,且掩盖"写路径失效"这一被测口径本身。
    /// 返回本次**新建**行的 Id(已存在则 0——初值还原时只删自己建的行)。
    /// </summary>
    private static async Task<long> SetSwitchAsync(DbScope scope, string value)
    {
        using var unit = scope.NewScope();
        var svc = unit.ServiceProvider.GetRequiredService<SysConfigService>();
        var row = await scope.Db.Queryable<SysConfig>().FirstAsync(c => c.Code == SwitchCode);

        // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):仅"写 sys_upgrade"这一段临时挂超管,
        // 结束后还原原上下文——未读/最新公告查询仍以探针读者(777)身份执行,被测语义不变。
        var accessor = scope.FakeAccessor;
        var previous = accessor.HttpContext;
        accessor.SetSuperAdmin();
        try
        {
            if (row is null)
            {
                return await svc.AddAsync(new AddConfigInput(
                    Name: "升级公告提示开关(批 AF 测试临时行)",
                    Code: SwitchCode,
                    Value: value,
                    GroupCode: "Default",
                    SysFlag: false, // 非内置:本用例自建行必须可删(避免被"内置禁删"卡住清理)
                    Remark: "批 AF 测试用例临时行,finally 物理删"));
            }

            await svc.UpdateAsync(new UpdateConfigInput(row.Id, Value: value));
            return 0;
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    /// <summary>
    /// 清理:物理删探针公告/接收行/用户 + **还原配置开关初值**(初值不存在 → 物理删本用例新增行,不留软删残行)。
    /// WHY 原生 SQL 物理删:仓储软删/Deleteable 受全局软删过滤器约束,残留软删行会让后续用例看到脏数据。
    /// </summary>
    private static async Task CleanupAsync(DbScope scope, SwitchSnapshot initial, long createdConfigId, long upgradeId, long noticeId)
    {
        var db = scope.Db;

        foreach (var id in new[] { upgradeId, noticeId })
        {
            if (id <= 0)
            {
                continue;
            }

            await db.Ado.ExecuteCommandAsync(
                "DELETE FROM sys_notice_user WHERE \"NoticeId\" = @id", new SugarParameter("@id", id));
            await db.Ado.ExecuteCommandAsync(
                "DELETE FROM sys_notice WHERE id = @id", new SugarParameter("@id", id));
        }

        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM sys_user WHERE id = @id", new SugarParameter("@id", ReaderId));

        if (initial.Existed)
        {
            // 初值还原(走写路径,顺带失效缓存);㉖ 写闸:仅这一段临时挂超管,finally 还原
            using var unit = scope.NewScope();
            var svc = unit.ServiceProvider.GetRequiredService<SysConfigService>();
            var accessor = scope.FakeAccessor;
            var previous = accessor.HttpContext;
            accessor.SetSuperAdmin();
            try
            {
                await svc.UpdateAsync(new UpdateConfigInput(initial.Id, Value: initial.Value));
            }
            finally
            {
                accessor.HttpContext = previous;
            }
        }
        else if (createdConfigId > 0)
        {
            await db.Ado.ExecuteCommandAsync(
                "DELETE FROM sys_config WHERE id = @id", new SugarParameter("@id", createdConfigId));
        }
    }

    /// <summary>开关行快照(Existed=false 表示本用例开始前该 Code 无可见行 → 清理时删掉自建行)。</summary>
    private sealed record SwitchSnapshot(bool Existed, long Id, string? Value);

    /// <summary>构造模拟登录上下文(UserId/Account/AccountType/TenantId Claims,与生产 JwtClaimNames 同键)。</summary>
    private static HttpContext ReaderContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtClaimNames.UserId, ReaderId.ToString()),
            new Claim(JwtClaimNames.Account, ReaderAccount),
            new Claim(JwtClaimNames.RealName, "探针升级读者"),
            new Claim(JwtClaimNames.AccountType, "777"),
            new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
        }, "test"));
        return context;
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddSignalR + AddAutoServices(Core 程序集),
    /// 容器含通知服务全依赖链(SysNotice/SysNoticeUser/SysUser/SysOrg 仓储 + UserManager + SysOnlineUserService
    /// + **SysConfigService**(批 AF 4.11 新依赖))。InitTables 增量建表(幂等)。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private readonly ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

        // ㉖ 唯一身份构造点:默认读者身份;配置写段临时切超管(见 SetSwitchAsync/CleanupAsync)
        public TestActorAccessor FakeAccessor { get; } = new();

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
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddAutoServices(typeof(SysNotice).Assembly);
            // 批 G 同装配:SignalR 注册 IHubContext(测试无连接时推送 no-op)
            services.AddSignalR();
            // ㉖ 放在 AddAutoServices 之后注册:单服务解析取最后一次注册,确保 accessor 落到本夹具实例
            services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            FakeAccessor.HttpContext = ReaderContext();
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
                // 故无需超时参数即可快速降级(BatchGNoticeMessageTest 的 .Wait(2s) 口径等价)。
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