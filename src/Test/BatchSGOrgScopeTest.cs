using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Extensions;
using AdminCore.Core.Interfaces;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.File;
using AdminCore.Core.Services.File.Dto;
using AdminCore.Core.Services.Notice;
using AdminCore.Core.Services.Notice.Dto;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Print;
using AdminCore.Core.Services.Print.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 D1.3(S-G 纵深·机构数据权限真正生效)验证:三域生产实体(sys_file/sys_notice/sys_print)纳管后,
/// SqlSugarFilter 行级机构过滤对【真实业务表】生效(批 B 机制测试用探针实体,对生产实体此前零生效——
/// 22 个 EntityTenant + 17 个 EntityBase 无一继承数据权限基类,门槛收敛为 IOrgIdFilter 后方纳管)。
/// 断言矩阵(uA=Org(3) 非超管 / 超管 999 / uAll=All(1) 非超管):
/// · uA 视角三域 PageAsync 只见本机构(A)行——A1 行/他行 CreateOrgId 均滤除;
/// · CreateOrgId=NULL 探针行(创建人=uA,服务层"仅本人"过滤不滤它)在 uA 视角不可见 = fail-closed 实证;
/// · 超管见全部(豁免路径);uAll(All 范围)机构过滤不装配,file/print 见全部;
///   notice 域 uAll 仅见自己发布的行(既有服务层"非超管仅本人发布"过滤,与本臂无关——
///   该行 CreateOrgId=A1 在 uA 视角不可见而 uAll 可见,同时证明 All 范围未装配机构过滤)。
/// 环境:真实 PG + 真实服务 PageAsync(生产链路同型:GetUserOrgScopeAsync → SetOrgEntityFilter);
/// 探针(双机构 A→A1 + 4 用户 + 2 角色 + 三域各 4 行)finally 物理清理;PG 不可达 Assert.Fail 禁跳过。
/// </summary>
public class BatchSGOrgScopeTest
{
    // 开发库连接串(与批 B/CG 同源,仅开发用;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

    private const int AdminAccountType = 999;
    private const int NormalAccountType = 777;

    /// <summary>uA(Org 范围)视角:三域只读本机构行,NULL 探针 fail-closed 不可见。</summary>
    [Fact]
    public async Task 机构纳管_uA仅见本机构行且空CreateOrgId不可见()
    {
        using var f = new Fixture();
        if (!f.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        f.SetUser(f.UserA, NormalAccountType);
        var (maxScope, orgIds) = await f.GetOrgService().GetUserOrgScopeAsync(f.UserA);
        Assert.Equal(DataScopeEnum.Org, maxScope);
        var expected = new[] { f.OrgA };
        Assert.Equal(expected, orgIds);

        // uA 视角:装配机构过滤(三域实体 D1.3 后实现 IOrgIdFilter → 全部命中注册)
        var set = SqlSugarFilter.SetOrgEntityFilter(f.GetDb(), f.UserA, maxScope, orgIds);
        Assert.True(set, "三域实体纳管后 Org 范围应装配机构过滤器");
        try
        {
            var filePage = await f.GetFileService().PageAsync(new FilePageInput(f.Marker, 1, 50));
            Assert.True(filePage.Total == 1 && filePage.List.Single().Id == f.FileA,
                $"sys_file uA(Org) 只应见本机构行(空 CreateOrgId 行 fail-closed 不可见),实际 Total={filePage.Total}");

            var printPage = await f.GetPrintService().PageAsync(new PageSysPrintInput(f.Marker, 1, 50));
            Assert.True(printPage.Total == 1 && printPage.List.Single().Id == f.PrintA,
                $"sys_print uA(Org) 只应见本机构行,实际 Total={printPage.Total}");

            var noticePage = await f.GetNoticeService().PageAsync(new PageNoticeInput(f.Marker, 0, 1, 50));
            Assert.True(noticePage.Total == 1 && noticePage.List.Single().Id == f.NoticeA,
                $"sys_notice uA(Org) 只应见本机构行(NULL 探针行创建人=uA,服务层仅本人过滤不滤它,滤除它的唯一机制=机构过滤 fail-closed),实际 Total={noticePage.Total}");
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(f.GetDb());
        }
    }

    /// <summary>超管豁免:不装配机构过滤,三域见全部探针行。</summary>
    [Fact]
    public async Task 机构纳管_超管见全部()
    {
        using var f = new Fixture();
        if (!f.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        f.SetUser(f.UserSuper, AdminAccountType);
        try
        {
            var filePage = await f.GetFileService().PageAsync(new FilePageInput(f.Marker, 1, 50));
            Assert.True(filePage.Total == 4, $"超管 sys_file 应见全部 4 探针行,实际 Total={filePage.Total}");

            var printPage = await f.GetPrintService().PageAsync(new PageSysPrintInput(f.Marker, 1, 50));
            Assert.True(printPage.Total == 4, $"超管 sys_print 应见全部 4 探针行,实际 Total={printPage.Total}");

            var noticePage = await f.GetNoticeService().PageAsync(new PageNoticeInput(f.Marker, 0, 1, 50));
            Assert.True(noticePage.Total == 4, $"超管 sys_notice 应见全部 4 探针行(超管无服务层仅本人过滤),实际 Total={noticePage.Total}");
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(f.GetDb());
        }
    }

    /// <summary>All 范围(非超管):机构过滤不装配——file/print 见全部(含范围外 A1 行);
    /// notice 仅见自己发布的行(既有服务层"非超管仅本人发布"过滤,与本臂无关),该行属 A1 机构 ⇒ All 未装配的旁证。</summary>
    [Fact]
    public async Task 机构纳管_All范围不装配_file与print见全部()
    {
        using var f = new Fixture();
        if (!f.PgReachable)
        {
            Assert.Fail(Downgrade);
        }

        f.SetUser(f.UserAll, NormalAccountType);
        var (maxScope, orgIds) = await f.GetOrgService().GetUserOrgScopeAsync(f.UserAll);
        Assert.Equal(DataScopeEnum.All, maxScope);
        Assert.False(SqlSugarFilter.SetOrgEntityFilter(f.GetDb(), f.UserAll, maxScope, orgIds),
            "All 范围不装配机构过滤器(契约 4)");

        try
        {
            var filePage = await f.GetFileService().PageAsync(new FilePageInput(f.Marker, 1, 50));
            Assert.True(filePage.Total == 4, $"All 范围 sys_file 应见全部 4 探针行(含 A1 机构行),实际 Total={filePage.Total}");

            var printPage = await f.GetPrintService().PageAsync(new PageSysPrintInput(f.Marker, 1, 50));
            Assert.True(printPage.Total == 4, $"All 范围 sys_print 应见全部 4 探针行,实际 Total={printPage.Total}");

            // notice 域:服务层"非超管仅本人发布"(既有语义)滤 uA/uB 行 → 仅见 uAll 自己的 rowAll;
            // rowAll 的 CreateOrgId=A1(uA 的 Org 范围视角下不可见)⇒ 它可见即证明 All 范围机构过滤未装配。
            var noticePage = await f.GetNoticeService().PageAsync(new PageNoticeInput(f.Marker, 0, 1, 50));
            Assert.True(noticePage.Total == 1 && noticePage.List.Single().Id == f.NoticeAll,
                $"All 范围 sys_notice 应仅见自己发布行(服务层既有语义,非机构过滤),实际 Total={noticePage.Total}");
        }
        finally
        {
            SqlSugarFilter.ClearOrgFilter(f.GetDb());
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Provider { get; }
        public FakeHttpContextAccessor FakeAccessor { get; } = new();
        public bool PgReachable { get; }
        public ISqlSugarClient Db { get; private set; } = null!;
        public string Marker { get; }

        public long OrgA { get; }
        public long OrgA1 { get; }

        public long UserA { get; }
        public long UserB { get; }
        public long UserAll { get; }
        public long UserSuper { get; }

        public long FileA { get; }
        public long FileB { get; }
        public long FileNull { get; }
        public long FileAll { get; }

        public long NoticeA { get; }
        public long NoticeB { get; }
        public long NoticeNull { get; }
        public long NoticeAll { get; }

        public long PrintA { get; }
        public long PrintB { get; }
        public long PrintNull { get; }
        public long PrintAll { get; }

        private readonly IServiceScope _scope;
        private readonly long _roleOrg;
        private readonly long _roleAll;

        public Fixture()
        {
            if (!IsPostgresReachable())
            {
                Provider = new ServiceCollection().BuildServiceProvider();
                return;
            }

            Marker = $"t20sg_{Guid.NewGuid():N}";
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = DbConnectionString,
                    ["Yit:WorkerId"] = "1",
                    // 测试占位密钥(32 字节 Base64):AddSqlSugar 解析 CryptogramHelper 需要(批 UX 实测)
                    ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            // 模拟用户上下文访问器须在 AddSqlSugar 之前注册(TryAdd 不遮蔽已注册实例)
            services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
            services.AddSingleton<IConfiguration>(config);
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);
            services.AddSignalR(); // SysNoticeService → SysOnlineUserService → IHubContext(无连接时推送 no-op)
            services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
            services.AddSingleton<IFileContentStore, FileContentStore>(); // SysFileService 依赖
            services.AddAutoServices(typeof(SysOrgService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            Provider = services.BuildServiceProvider();

            var db = Provider.GetRequiredService<ISqlSugarClient>();
            Db = db;
            TestSchemaBootstrap.Ensure(db);
            // 数据权限实体注册:与生产 Program.cs 同口径扫 Core 程序集 + 测试程序集(既有批次同口径)
            SqlSugarFilter.Init(typeof(SqlSugarSetup).Assembly, typeof(BatchSGOrgScopeTest).Assembly);
            _scope = Provider.CreateScope();
            PgReachable = true;

            var diScope = Provider.CreateScope();
            var sp = diScope.ServiceProvider;
            var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();
            var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
            var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
            var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
            var fileRepo = sp.GetRequiredService<SqlSugarRepository<SysFile>>();
            var noticeRepo = sp.GetRequiredService<SqlSugarRepository<SysNotice>>();
            var printRepo = sp.GetRequiredService<SqlSugarRepository<SysPrint>>();

            // 双机构:A(顶级)→ A1(子);uA 挂 A、uB/uAll 挂 A1
            OrgA = InsertOrg(orgRepo, $"{Marker}_org_a", 0);
            OrgA1 = InsertOrg(orgRepo, $"{Marker}_org_a1", OrgA);

            // 角色:Org(3 本部门)→ uA / All(1)→ uAll
            _roleOrg = InsertRole(roleRepo, $"{Marker}_ro", DataScopeEnum.Org);
            _roleAll = InsertRole(roleRepo, $"{Marker}_rall", DataScopeEnum.All);

            // 用户(探针哑哈希,不登录);UserSuper=999 超管(无角色,豁免路径)
            UserA = InsertUser(userRepo, $"{Marker}_ua", NormalAccountType, OrgA);
            UserB = InsertUser(userRepo, $"{Marker}_ub", NormalAccountType, OrgA1);
            UserAll = InsertUser(userRepo, $"{Marker}_uall", NormalAccountType, OrgA1);
            UserSuper = InsertUser(userRepo, $"{Marker}_usuper", AdminAccountType, null);

            BindUser(userRoleRepo, UserA, _roleOrg);
            BindUser(userRoleRepo, UserAll, _roleAll);

            // 三域探针行各 4:uA/A、uB/A1、uA/NULL(fail-closed 探针)、uAll/A1;
            // 插入上下文复位 null → AOP 不覆盖显式 CreateUserId/CreateOrgId(批 B 同口径)
            FakeAccessor.HttpContext = null;
            (FileA, FileB, FileNull, FileAll) = InsertFileRows(fileRepo);
            (NoticeA, NoticeB, NoticeNull, NoticeAll) = InsertNoticeRows(noticeRepo);
            (PrintA, PrintB, PrintNull, PrintAll) = InsertPrintRows(printRepo);
            diScope.Dispose();
        }

        private (long, long, long, long) InsertFileRows(SqlSugarRepository<SysFile> repo)
        {
            // sys_file 有 NOT NULL 列(FileMd5/SizeKb/FilePath/Suffix/Provider/Url),探针行须补齐才能插入
            SysFile NewFile(string name, long userId, long? orgId) => new()
            {
                FileName = name,
                FileMd5 = Guid.NewGuid().ToString("N"),
                SizeKb = 1,
                FilePath = $"probe/{name}",
                Suffix = ".txt",
                Provider = "Local",
                Url = $"/probe/{name}.txt",
                CreateUserId = userId,
                CreateOrgId = orgId
            };
            InsertRow(repo, NewFile($"{Marker}_fa", UserA, OrgA));
            InsertRow(repo, NewFile($"{Marker}_fb", UserB, OrgA1));
            InsertRow(repo, NewFile($"{Marker}_fnull", UserA, null));
            InsertRow(repo, NewFile($"{Marker}_fall", UserAll, OrgA1));
            return (
                GetId(repo, $"{Marker}_fa"), GetId(repo, $"{Marker}_fb"),
                GetId(repo, $"{Marker}_fnull"), GetId(repo, $"{Marker}_fall"));
        }

        private (long, long, long, long) InsertNoticeRows(SqlSugarRepository<SysNotice> repo)
        {
            InsertRow(repo, new SysNotice
            {
                Title = $"{Marker}_na", Content = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserA, CreateOrgId = OrgA
            });
            InsertRow(repo, new SysNotice
            {
                Title = $"{Marker}_nb", Content = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserB, CreateOrgId = OrgA1
            });
            InsertRow(repo, new SysNotice
            {
                Title = $"{Marker}_nnull", Content = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserA, CreateOrgId = null
            });
            InsertRow(repo, new SysNotice
            {
                Title = $"{Marker}_nall", Content = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserAll, CreateOrgId = OrgA1
            });
            return (
                GetId(repo, $"{Marker}_na"), GetId(repo, $"{Marker}_nb"),
                GetId(repo, $"{Marker}_nnull"), GetId(repo, $"{Marker}_nall"));
        }

        private (long, long, long, long) InsertPrintRows(SqlSugarRepository<SysPrint> repo)
        {
            InsertRow(repo, new SysPrint
            {
                Name = $"{Marker}_pa", Template = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserA, CreateOrgId = OrgA
            });
            InsertRow(repo, new SysPrint
            {
                Name = $"{Marker}_pb", Template = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserB, CreateOrgId = OrgA1
            });
            InsertRow(repo, new SysPrint
            {
                Name = $"{Marker}_pnull", Template = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserA, CreateOrgId = null
            });
            InsertRow(repo, new SysPrint
            {
                Name = $"{Marker}_pall", Template = "d1.3 probe", TenantId = SqlSugarConst.DefaultTenantId,
                CreateUserId = UserAll, CreateOrgId = OrgA1
            });
            return (
                GetId(repo, $"{Marker}_pa"), GetId(repo, $"{Marker}_pb"),
                GetId(repo, $"{Marker}_pnull"), GetId(repo, $"{Marker}_pall"));
        }

        private static void InsertRow<TEntity>(SqlSugarRepository<TEntity> repo, TEntity entity)
            where TEntity : class, new()
        {
            repo.InsertAsync(entity).GetAwaiter().GetResult();
        }

        private long GetId<TEntity>(SqlSugarRepository<TEntity> repo, string marker)
            where TEntity : class, new()
        {
            // 按标记定位:三域探针行的标记字段不同(FileName/Title/Name)——表达式按实体类型分发
            var param = System.Linq.Expressions.Expression.Parameter(typeof(TEntity), "u");
            var prop = typeof(TEntity).GetProperty(FieldName<TEntity>())
                ?? throw new InvalidOperationException($"实体 {typeof(TEntity).Name} 缺标记字段 {FieldName<TEntity>()}");
            var body = System.Linq.Expressions.Expression.Equal(
                System.Linq.Expressions.Expression.Property(param, prop),
                System.Linq.Expressions.Expression.Constant(marker));
            var row = repo.AsQueryable()
                .Where(System.Linq.Expressions.Expression.Lambda<Func<TEntity, bool>>(body, param))
                .First();
            var idProp = typeof(TEntity).GetProperty("Id") ?? throw new InvalidOperationException("实体缺 Id");
            return (long)idProp.GetValue(row)!;
        }

        private static string FieldName<TEntity>() => typeof(TEntity) switch
        {
            _ when typeof(TEntity) == typeof(SysFile) => nameof(SysFile.FileName),
            _ when typeof(TEntity) == typeof(SysNotice) => nameof(SysNotice.Title),
            _ when typeof(TEntity) == typeof(SysPrint) => nameof(SysPrint.Name),
            _ => throw new InvalidOperationException("未支持的三域实体")
        };

        private static long InsertOrg(SqlSugarRepository<SysOrg> repo, string name, long pid)
        {
            var org = new SysOrg { Name = name, Pid = pid, TenantId = SqlSugarConst.DefaultTenantId };
            repo.InsertAsync(org).GetAwaiter().GetResult();
            return org.Id;
        }

        private static long InsertRole(SqlSugarRepository<SysRole> repo, string name, DataScopeEnum scope)
        {
            var role = new SysRole { Name = name, Code = name, DataScope = (int)scope, Remark = "", TenantId = SqlSugarConst.DefaultTenantId };
            repo.InsertAsync(role).GetAwaiter().GetResult();
            return role.Id;
        }

        private static long InsertUser(SqlSugarRepository<SysUser> repo, string account, int accountType, long? orgId)
        {
            // 存量 Password 列 NOT NULL(使用指南坑 12)——探针填哑哈希,不登录
            var user = new SysUser
            {
                Account = account,
                Password = "t20sg_probe_never_login",
                RealName = account,
                AccountType = accountType,
                Status = 0,
                OrgId = orgId,
                TenantId = SqlSugarConst.DefaultTenantId
            };
            repo.InsertAsync(user).GetAwaiter().GetResult();
            return user.Id;
        }

        private static void BindUser(SqlSugarRepository<SysUserRole> repo, long userId, long roleId)
        {
            repo.InsertAsync(new SysUserRole { UserId = userId, RoleId = roleId, TenantId = SqlSugarConst.DefaultTenantId }).GetAwaiter().GetResult();
        }

        /// <summary>设模拟登录身份(与生产 JwtClaimNames 同键,批 B/CG 同口径)。</summary>
        public void SetUser(long userId, int accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, $"t20sg_{userId}"),
                new Claim(JwtClaimNames.AccountType, accountType.ToString()),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            FakeAccessor.HttpContext = context;
        }

        public SysOrgService GetOrgService() => _scope.ServiceProvider.GetRequiredService<SysOrgService>();

        public SysFileService GetFileService() => _scope.ServiceProvider.GetRequiredService<SysFileService>();

        public SysNoticeService GetNoticeService() => _scope.ServiceProvider.GetRequiredService<SysNoticeService>();

        public SysPrintService GetPrintService() => _scope.ServiceProvider.GetRequiredService<SysPrintService>();

        public ISqlSugarClient GetDb() => Db;

        public void Dispose()
        {
            if (!PgReachable)
            {
                Provider.Dispose();
                return;
            }

            try
            {
                var diScope = Provider.CreateScope();
                var sp = diScope.ServiceProvider;
                var fileRepo = sp.GetRequiredService<SqlSugarRepository<SysFile>>();
                var noticeRepo = sp.GetRequiredService<SqlSugarRepository<SysNotice>>();
                var printRepo = sp.GetRequiredService<SqlSugarRepository<SysPrint>>();
                var userRoleRepo = sp.GetRequiredService<SqlSugarRepository<SysUserRole>>();
                var userRepo = sp.GetRequiredService<SqlSugarRepository<SysUser>>();
                var roleRepo = sp.GetRequiredService<SqlSugarRepository<SysRole>>();
                var orgRepo = sp.GetRequiredService<SqlSugarRepository<SysOrg>>();

                var fileIds = new[] { FileA, FileB, FileNull, FileAll };
                var noticeIds = new[] { NoticeA, NoticeB, NoticeNull, NoticeAll };
                var printIds = new[] { PrintA, PrintB, PrintNull, PrintAll };
                var userIds = new[] { UserA, UserB, UserAll, UserSuper };

                fileRepo.DeletePhysicalByAsync(u => fileIds.Contains(u.Id)).GetAwaiter().GetResult();
                noticeRepo.DeletePhysicalByAsync(u => noticeIds.Contains(u.Id)).GetAwaiter().GetResult();
                printRepo.DeletePhysicalByAsync(u => printIds.Contains(u.Id)).GetAwaiter().GetResult();
                userRoleRepo.DeletePhysicalByAsync(u => userIds.Contains(u.UserId)).GetAwaiter().GetResult();
                userRepo.DeletePhysicalByAsync(u => userIds.Contains(u.Id)).GetAwaiter().GetResult();
                roleRepo.DeletePhysicalByAsync(u => new[] { _roleOrg, _roleAll }.Contains(u.Id)).GetAwaiter().GetResult();
                orgRepo.DeletePhysicalByAsync(u => new[] { OrgA, OrgA1 }.Contains(u.Id)).GetAwaiter().GetResult();

                // 范围缓存清理(变更即失效口径):防残留键指向已删用户(7 天 TTL)
                var cache = sp.GetRequiredService<ICacheService>();
                foreach (var uid in userIds)
                {
                    SqlSugarFilter.DeleteUserOrgCacheAsync(cache, uid).GetAwaiter().GetResult();
                }

                diScope.Dispose();
            }
            finally
            {
                _scope.Dispose();
                Provider.Dispose();
                (Db as IDisposable)?.Dispose();
            }
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同批 B/CG 口径)。</summary>
        private static bool IsPostgresReachable()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var task = client.ConnectAsync("127.0.0.1", 5432);
                return task.Wait(TimeSpan.FromMilliseconds(2000)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd;AOP 审计/机构填充读它)。</summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>最小 IWebHostEnvironment:仅 ContentRootPath 有值(DefaultFileProvider 只读它,批 BUSysFile 同口径)。</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment()
        {
            ContentRootPath = Directory.CreateTempSubdirectory("t20sg_").FullName;
        }

        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Production";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }

    /// <summary>空 IFileProvider(.NET 10 无内置公共件,批 BUSysFile 同口径)。</summary>
    private sealed class NullFileProvider : IFileProvider
    {
        public void Dispose() { }
        public IChangeToken Watch(string filter) => throw new NotSupportedException();
        public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
        public IFileInfo GetFileInfo(string subpath) => new NullFileInfo();
    }

    /// <summary>空 IFileInfo(Exists=false)。</summary>
    private sealed class NullFileInfo : IFileInfo
    {
        public bool Exists => false;
        public long Length => 0;
        public string PhysicalPath => string.Empty;
        public string Name => string.Empty;
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => throw new NotSupportedException();
    }
}
