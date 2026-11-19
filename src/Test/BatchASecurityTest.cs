using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Menu;
using AdminCore.Core.Services.Menu.Dto;
using AdminCore.Core.Services.User;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test;

/// <summary>
/// 批 A 安全授权闭环单测(2.10 黑名单扣减 / 2.3 登录菜单树 / 1.11 TokenVersion 强制下线):
/// DB 集成测试(与 RepositoryTest/DataScopeTest 同源连接串);沙箱 PG 不可达时用例诚实降级(Assert.Fail 显式声明)。
/// 口径:临时数据用 Guid marker 隔离,finally 物理清理(中间表物理删、主表物理删),不污染种子数据。
/// </summary>
public class BatchASecurityTest
{
    private readonly ITestOutputHelper _output;

    public BatchASecurityTest(ITestOutputHelper output)
    {
        _output = output;
    }

    // 开发库连接串(与 RepositoryTest/DataScopeTest 同源;Timeout=10 沙箱 PG 黑洞快速失败)
    private const string DbConnectionString =
        TestPg.ConnectionString;

    private const string DbHost = "127.0.0.1";
    private const int DbPort = 5432;

    /// <summary>
    /// 1.11 强制下线闭环:OfflineAndExpireToken 使 TokenVersion++(DB)+ 删版本缓存(Redis)——
    /// 旧 Token 的 TokenVersion Claim ≠ 新版本,JwtAuthHandler 下次请求即拒。
    /// </summary>
    [Fact]
    public async Task TokenVersion_强制下线_版本自增且版本缓存被删()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var userRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var cache = diScope.ServiceProvider.GetRequiredService<ICacheService>();
        var userService = diScope.ServiceProvider.GetRequiredService<SysUserService>();

        var marker = $"ba3_tok_{Guid.NewGuid():N}";
        long userId = 0;
        try
        {
            // 临时普通用户(AccountType=777 避免超管穿透;Password 占位,本用例不走登录)
            var user = new SysUser { Account = marker, Password = "x", RealName = marker, Status = 0, AccountType = 777 };
            await userRepo.InsertAsync(user);
            userId = user.Id;

            // 记录初始版本 + 预置版本缓存(模拟已登录态,键口径同 JwtAuthHandler/AuthService)
            var before = await userRepo.GetFirstAsync(u => u.Id == userId);
            var vBefore = before!.TokenVersion;
            var tokenKey = $"{CacheConst.KeyUserToken}{userId}";
            await cache.SetAsync(tokenKey, vBefore);
            Assert.True(await cache.ExistKeyAsync(tokenKey), "前置:版本缓存应已写入");

            // 强制下线
            await userService.OfflineAndExpireToken(userId);

            // 断言① TokenVersion +1(DB 落库)
            var after = await userRepo.GetFirstAsync(u => u.Id == userId);
            Assert.Equal(vBefore + 1, after!.TokenVersion);

            // 断言② 版本缓存被删(键不存在)
            Assert.False(await cache.ExistKeyAsync(tokenKey), "强制下线后版本缓存应被删除");
        }
        finally
        {
            if (userId > 0)
            {
                await userRepo.DeletePhysicalByAsync(u => u.Id == userId);
                await cache.RemoveAsync($"{CacheConst.KeyUserToken}{userId}");
            }
        }
    }

    /// <summary>
    /// 2.10 黑名单扣减:有效权限集 = 角色授权按钮权限 − 角色接口黑名单(忽略大小写)。
    /// 授权 permA+permB,黑名单 permA → 有效集仅 permB。
    /// </summary>
    [Fact]
    public async Task 黑名单_有效权限集_扣减被禁用项()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var roleRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysRole>>();
        var menuRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysMenu>>();
        var roleMenuRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysRoleMenu>>();
        var userRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var userRoleRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysUserRole>>();
        var roleApiRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysRoleApi>>();
        var cache = diScope.ServiceProvider.GetRequiredService<ICacheService>();
        var permService = diScope.ServiceProvider.GetRequiredService<IMenuPermService>();

        var marker = $"ba2_bl_{Guid.NewGuid():N}";
        var permA = marker + "/permA";
        var permB = marker + "/permB";
        long roleId = 0, userId = 0, menuAId = 0, menuBId = 0;
        try
        {
            // 角色 + 两个按钮菜单(Type=3)
            var role = new SysRole { Name = marker, Code = marker, Remark = marker };
            await roleRepo.InsertAsync(role);
            roleId = role.Id;

            var menuA = new SysMenu { Pid = 0, Name = marker + "_btnA", Title = "A", Type = 3, Permission = permA, OrderNo = 1 };
            await menuRepo.InsertAsync(menuA);
            menuAId = menuA.Id;
            var menuB = new SysMenu { Pid = 0, Name = marker + "_btnB", Title = "B", Type = 3, Permission = permB, OrderNo = 2 };
            await menuRepo.InsertAsync(menuB);
            menuBId = menuB.Id;

            // 角色授权两按钮
            await roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = roleId, MenuId = menuAId });
            await roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = roleId, MenuId = menuBId });

            // 临时用户挂该角色
            var user = new SysUser { Account = marker + "_user", Password = "x", RealName = marker, Status = 0, AccountType = 777 };
            await userRepo.InsertAsync(user);
            userId = user.Id;
            await userRoleRepo.InsertAsync(new SysUserRole { UserId = userId, RoleId = roleId });

            // 黑名单:禁用 permA
            await roleApiRepo.InsertAsync(new SysRoleApi { RoleId = roleId, Permission = permA });

            // 解析有效权限集(新用户无缓存 → 走链路查询)
            var perms = (await permService.GetPermissionsAsync(userId)).ToList();

            // 断言:有效集恰为 {permB}(permA 被黑名单扣减、permB 保留)
            var only = Assert.Single(perms);
            Assert.Equal(permB, only);
        }
        finally
        {
            await roleApiRepo.DeletePhysicalByAsync(u => u.RoleId == roleId);
            await userRoleRepo.DeletePhysicalByAsync(u => u.UserId == userId);
            await roleMenuRepo.DeletePhysicalByAsync(u => u.RoleId == roleId);
            await menuRepo.DeletePhysicalByAsync(u => u.Id == menuAId);
            await menuRepo.DeletePhysicalByAsync(u => u.Id == menuBId);
            await userRepo.DeletePhysicalByAsync(u => u.Id == userId);
            await roleRepo.DeletePhysicalByAsync(u => u.Id == roleId);
            if (userId > 0)
            {
                await cache.RemoveAsync($"{CacheConst.KeyUserApiPerm}{userId}");
            }
        }
    }

    /// <summary>
    /// 2.3 登录菜单树(超管口径):AccountType=999 → 全量非按钮菜单(剔除 Type=3),节点集与库内非按钮集一致。
    /// </summary>
    [Fact]
    public async Task 登录菜单树_超管_全量剔按钮()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var menuRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysMenu>>();
        var menuService = diScope.ServiceProvider.GetRequiredService<SysMenuService>();

        // 超管分支不使用 userId(全量),传 0 即可
        var tree = await menuService.GetLoginMenuTreeAsync(0, 999);
        var flat = Flatten(tree).ToList();

        // 断言① 树中无任何按钮节点(Type=3 已剔除)
        Assert.DoesNotContain(flat, n => n.Type == 3);

        // 断言② 节点集 == 库内非按钮菜单集(软删过滤同源,口径一致)
        var expectedIds = (await menuRepo.AsQueryable().Where(u => u.Type != 3).Select(u => u.Id).ToListAsync()).ToHashSet();
        Assert.Equal(expectedIds, flat.Select(n => n.Id).ToHashSet());
    }

    /// <summary>
    /// 2.3 登录菜单树(普通用户口径):按角色授权菜单集裁剪——仅含授权的非按钮节点,且父子结构正确。
    /// </summary>
    [Fact]
    public async Task 登录菜单树_普通用户_按角色裁剪()
    {
        using var dbScope = new DbScope();
        if (!dbScope.PgReachable)
        {
            Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
        }

        using var diScope = dbScope.Provider.CreateScope();
        var roleRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysRole>>();
        var menuRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysMenu>>();
        var roleMenuRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysRoleMenu>>();
        var userRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysUser>>();
        var userRoleRepo = diScope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysUserRole>>();
        var menuService = diScope.ServiceProvider.GetRequiredService<SysMenuService>();

        var marker = $"ba3_tree_{Guid.NewGuid():N}";
        long roleId = 0, userId = 0, dirId = 0, pageId = 0;
        try
        {
            var role = new SysRole { Name = marker, Code = marker, Remark = marker };
            await roleRepo.InsertAsync(role);
            roleId = role.Id;

            // 目录(1)+ 其下菜单(2),不授权按钮
            var dir = new SysMenu { Pid = 0, Name = marker + "_dir", Title = "目录", Type = 1, Permission = null, OrderNo = 1 };
            await menuRepo.InsertAsync(dir);
            dirId = dir.Id;
            var page = new SysMenu { Pid = dirId, Name = marker + "_page", Title = "菜单", Type = 2, Permission = null, OrderNo = 1 };
            await menuRepo.InsertAsync(page);
            pageId = page.Id;

            await roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = roleId, MenuId = dirId });
            await roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = roleId, MenuId = pageId });

            var user = new SysUser { Account = marker + "_u", Password = "x", RealName = marker, Status = 0, AccountType = 777 };
            await userRepo.InsertAsync(user);
            userId = user.Id;
            await userRoleRepo.InsertAsync(new SysUserRole { UserId = userId, RoleId = roleId });

            var tree = await menuService.GetLoginMenuTreeAsync(userId, 777);
            var flat = Flatten(tree).ToList();

            // 断言① 节点集恰好 = 授权的目录 + 菜单(无按钮、无越权节点)
            Assert.Equal(new HashSet<long> { dirId, pageId }, flat.Select(n => n.Id).ToHashSet());

            // 断言② 结构:目录为根,菜单为其子节点
            var dirNode = tree.Single(n => n.Id == dirId);
            Assert.Contains(pageId, dirNode.Children.Select(c => c.Id));
        }
        finally
        {
            await userRoleRepo.DeletePhysicalByAsync(u => u.UserId == userId);
            await roleMenuRepo.DeletePhysicalByAsync(u => u.RoleId == roleId);
            await menuRepo.DeletePhysicalByAsync(u => u.Id == dirId);
            await menuRepo.DeletePhysicalByAsync(u => u.Id == pageId);
            await userRepo.DeletePhysicalByAsync(u => u.Id == userId);
            await roleRepo.DeletePhysicalByAsync(u => u.Id == roleId);
        }
    }

    /// <summary>菜单树展平(递归,供断言节点集/类型)。</summary>
    private static IEnumerable<MenuOutput> Flatten(IEnumerable<MenuOutput> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// DB 测试客户端:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddAutoServices(Core 程序集),
    /// InitTables 扫 Core(建 sys_role_api/sys_user_password_record 等新表,增量幂等)。
    /// PG 不可达时标记降级(用例 Assert.Fail 显式声明)。
    /// </summary>
    private sealed class DbScope : IDisposable
    {
        private ServiceProvider? _provider;

        public ISqlSugarClient Db { get; } = null!;

        public IServiceProvider Provider { get; } = null!;

        public bool PgReachable { get; }

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
            // 空用户上下文访问器(AOP 审计填充需可解析 IHttpContextAccessor;无用户=后台/测试场景)
            services.AddSingleton<IHttpContextAccessor>(new EmptyHttpContextAccessor());
            services.AddSingleton<IConfiguration>(config); // SysUserService 依赖 IConfiguration(批 A 密码策略开关)
            services.AddSqlSugar(config);
            services.AddProjectOptions(config);
            services.AddCache(config);            services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

            services.AddAutoServices(typeof(SysUserService).Assembly); // 与 Program.cs 同口径:[Service] 自动发现
            _provider = services.BuildServiceProvider();
            Db = _provider.GetRequiredService<ISqlSugarClient>();
            Provider = _provider;
            Db.HealColumnCase();
            Db.InitTables(); // 无参扫 Core:建框架表(含批 A 新表 sys_role_api/sys_user_password_record),增量幂等
            PgReachable = true;
        }

        public void Dispose()
        {
            _provider?.Dispose();
            (Db as IDisposable)?.Dispose();
        }

        /// <summary>短超时 TCP 探测(2 秒,沙箱 PG 黑洞防挂死,同 RepositoryTest 口径)。</summary>
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

    /// <summary>空用户上下文访问器(无 HttpContext = 后台/测试场景,AOP 审计字段置空)。</summary>
    private sealed class EmptyHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
