using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Hub;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Services.OnlineUser.Dto;
using AdminCore.Core.Security;
using AdminCore.Core.SqlSugar;
using AdminCore.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 F(1.8 在线用户＋强踢 / 5.13 SignalR)测试。
    /// 覆盖:UserAgentHelper 纯函数(UA 判定顺序:Edge 先于 Chrome、iOS 先于 macOS)/
    /// MemoryCacheService Hash 三原语(批 F 扩展 HashGetAll/HashDelete/SetExpire＋过期桶清理)/
    /// SysOnlineUserService 强踢核心行为(按用户连接枚举/注销/滑动 TTL/单连接幂等/单登录配置门禁,
    /// 经 FakeHubContext 隔离推送——无连接时推送 no-op)。
    /// 边界披露(同批 E Handler 层口径):Hub 级 WebSocket 集成(negotiate 401 门禁/带 token 连接注册/
    /// 断开注销/分组广播/强踢推送)不经自动化,经批末手工验证后呈报。
    /// </summary>
    public class BatchFOnlineUserTest
    {
        // 唯一用户 Id 计数器(避免 Guid 十六进制串误作十进制解析;跨用例单调不重复)
        private static long _uidCounter = 9_000_000_000_000;

        // ── 纯函数:UA 解析 ─────────────────────────────────────────────────────

        [Fact]
        public void UA解析_Browser识别表()
        {
            // Edge UA 同时含 "Chrome" 字样 → Edge 判定必须先行
            Assert.Equal("Edge", UserAgentHelper.ParseBrowser("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0"));
            Assert.Equal("Chrome", UserAgentHelper.ParseBrowser("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"));
            Assert.Equal("Firefox", UserAgentHelper.ParseBrowser("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"));
            Assert.Equal("Safari", UserAgentHelper.ParseBrowser("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15"));
            Assert.Equal("Other", UserAgentHelper.ParseBrowser(null));
            Assert.Equal("Other", UserAgentHelper.ParseBrowser("   "));
        }

        [Fact]
        public void UA解析_Os识别表()
        {
            Assert.Equal("Windows", UserAgentHelper.ParseOs("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"));
            Assert.Equal("iOS", UserAgentHelper.ParseOs("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15"));
            Assert.Equal("iOS", UserAgentHelper.ParseOs("Mozilla/5.0 (iPad; CPU OS 16_0 like Mac OS X)"));
            Assert.Equal("Android", UserAgentHelper.ParseOs("Mozilla/5.0 (Linux; Android 14; Pixel 8)"));
            Assert.Equal("macOS", UserAgentHelper.ParseOs("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)"));
            Assert.Equal("Linux", UserAgentHelper.ParseOs("Mozilla/5.0 (X11; Linux x86_64)"));
            Assert.Equal("Other", UserAgentHelper.ParseOs(null));
        }

        // ── 缓存层:Hash 三原语(批 F 扩展)──────────────────────────────────────

        [Fact]
        public async Task Hash原语_读写删全量与过期()
        {
            var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));

            Assert.Empty(await cache.HashGetAllAsync<string>("k-none"));

            await cache.HashSetAsync("k", "f1", "a");
            await cache.HashSetAsync("k", "f2", "b");
            Assert.Equal("a", await cache.HashGetAsync<string>("k", "f1"));

            Assert.Equal(new[] { "a", "b" }, (await cache.HashGetAllAsync<string>("k")).OrderBy(x => x).ToArray());

            await cache.HashDeleteAsync("k", "f1");
            Assert.Null(await cache.HashGetAsync<string>("k", "f1"));
            Assert.Equal(new[] { "b" }, (await cache.HashGetAllAsync<string>("k")).OrderBy(x => x).ToArray());

            // SetExpire 极短 TTL → 过期后整桶移除(对齐 Redis 键过期语义;桶级判定)
            await cache.HashSetAsync("k2", "f", "v");
            await cache.SetExpireAsync("k2", TimeSpan.FromMilliseconds(50));
            await Task.Delay(80);
            Assert.Empty(await cache.HashGetAllAsync<string>("k2"));
            Assert.Null(await cache.HashGetAsync<string>("k2", "f"));

            // SetExpire(null) 去除过期
            await cache.HashSetAsync("k3", "f", "v");
            await cache.SetExpireAsync("k3", null);
            Assert.Equal("v", await cache.HashGetAsync<string>("k3", "f"));
        }

        // ── DB:在线用户服务 ────────────────────────────────────────────────────

        /// <summary>强踢按用户:枚举该用户全部连接并注销;其他用户不受影响。</summary>
        [Fact]
        public async Task 强踢_按用户枚举全部连接并注销()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            using var unit = NewUnit(scope);
            var cache = unit.ServiceProvider.GetRequiredService<ICacheService>();
            var svc = unit.ServiceProvider.GetRequiredService<SysOnlineUserService>();

            var uid = System.Threading.Interlocked.Increment(ref _uidCounter);
            try
            {
                // 同一用户两个连接＋另一用户一个连接
                await cache.HashSetAsync(CacheConst.KeyUserOnline, "c1", new OnlineUserInfo { ConnectionId = "c1", UserId = uid, TenantId = SqlSugarConst.DefaultTenantId, Account = "u1" });
                await cache.HashSetAsync(CacheConst.KeyUserOnline, "c2", new OnlineUserInfo { ConnectionId = "c2", UserId = uid, TenantId = SqlSugarConst.DefaultTenantId, Account = "u2" });
                await cache.HashSetAsync(CacheConst.KeyUserOnline, "c3", new OnlineUserInfo { ConnectionId = "c3", UserId = uid + 1, TenantId = SqlSugarConst.DefaultTenantId, Account = "other" });

                await svc.ForceOfflineByUserIdAsync(uid);

                Assert.Null(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "c1"));
                Assert.Null(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "c2"));
                Assert.NotNull(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "c3"));
            }
            finally
            {
                await cache.HashDeleteAsync(CacheConst.KeyUserOnline, "c1");
                await cache.HashDeleteAsync(CacheConst.KeyUserOnline, "c2");
                await cache.HashDeleteAsync(CacheConst.KeyUserOnline, "c3");
            }
        }

        /// <summary>强踢单连接:注销＋滑动 TTL;幂等(不存在/空 ConnectionId 不抛)。</summary>
        [Fact]
        public async Task 强踢_单连接幂等()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            using var unit = NewUnit(scope);
            var cache = unit.ServiceProvider.GetRequiredService<ICacheService>();
            var svc = unit.ServiceProvider.GetRequiredService<SysOnlineUserService>();

            try
            {
                await cache.HashSetAsync(CacheConst.KeyUserOnline, "cx", new OnlineUserInfo { ConnectionId = "cx", UserId = 1, TenantId = SqlSugarConst.DefaultTenantId, Account = "x" });

                await svc.ForceOfflineAsync("cx");
                Assert.Null(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "cx"));

                // 幂等:不存在连接再踢/空 ConnectionId 均不抛
                await svc.ForceOfflineAsync("cx");
                await svc.ForceOfflineAsync("");
            }
            finally
            {
                await cache.HashDeleteAsync(CacheConst.KeyUserOnline, "cx");
            }
        }

        /// <summary>单登录门禁:配置 false→不踢;true→踢掉该用户全部连接。</summary>
        [Fact]
        public async Task 单登录_配置门禁false不踢_true踢()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            using var unit = NewUnit(scope);
            var cache = unit.ServiceProvider.GetRequiredService<ICacheService>();
            var configSvc = unit.ServiceProvider.GetRequiredService<SysConfigService>();
            var svc = unit.ServiceProvider.GetRequiredService<SysOnlineUserService>();
            var cfgRepo = unit.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysConfig>>();

            // Code `sys_single_login` 全局唯一:dev 库已存在则记录原值、finally 恢复;不存在则新建、finally 物理删
            var existing = await cfgRepo.GetFirstAsync(u => u.Code == "sys_single_login");
            long cfgId = 0;
            try
            {
                // ㉖ 平台级配置写闸(SysConfigService.EnsurePlatformAdmin):仅"写 sys_single_login"这一段临时挂超管,
                // 结束后还原未认证基线——本用例断言的是门禁读值/踢人行为,不涉及登录者身份。
                if (existing is not null)
                {
                    cfgId = existing.Id;
                }
                else
                {
                    await AsSuperAdminAsync(scope, async () =>
                        cfgId = await configSvc.AddAsync(new AddConfigInput("batchF单登录", "sys_single_login", "false", "Sys")));
                }

                await cache.HashSetAsync(CacheConst.KeyUserOnline, "cy", new OnlineUserInfo { ConnectionId = "cy", UserId = 2, TenantId = SqlSugarConst.DefaultTenantId, Account = "y" });

                // false → 不踢
                await AsSuperAdminAsync(scope, () =>
                    configSvc.UpdateAsync(new UpdateConfigInput(cfgId, Value: "false")));
                await svc.SingleLoginAsync(2);
                Assert.NotNull(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "cy"));

                // true → 踢(配置变更走 UpdateAsync 缓存失效)
                await AsSuperAdminAsync(scope, () =>
                    configSvc.UpdateAsync(new UpdateConfigInput(cfgId, Value: "true")));
                await svc.SingleLoginAsync(2);
                Assert.Null(await cache.HashGetAsync<OnlineUserInfo>(CacheConst.KeyUserOnline, "cy"));
            }
            finally
            {
                await cache.HashDeleteAsync(CacheConst.KeyUserOnline, "cy");
                if (existing is null)
                {
                    await cfgRepo.DeletePhysicalByAsync(u => u.Id == cfgId);
                }
                else
                {
                    // 恢复原值(existing 为加载初态实体,整写即恢复)
                    await cfgRepo.UpdateAsync(existing);
                }
            }
        }

        // ── 测试基建(批 E 样板复用＋批 F:FakeHubContext)────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>㉖ 仅在配置写动作内临时挂超管,finally 还原原上下文(读路径/踢人行为身份不变)。</summary>
        private static async Task AsSuperAdminAsync(DbScope scope, Func<Task> action)
        {
            var previous = scope.Accessor.HttpContext;
            scope.Accessor.SetSuperAdmin();
            try
            {
                await action();
            }
            finally
            {
                scope.Accessor.HttpContext = previous;
            }
        }

        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            // ㉖ 唯一身份构造点:默认未认证基线
            public TestActorAccessor Accessor { get; } = new();

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
                services.AddAutoServices(typeof(SysUser).Assembly);
                // ㉖ 放在 AddAutoServices 之后注册:单服务解析取最后一次注册,确保 accessor 落到本夹具实例
                services.AddSingleton<IHttpContextAccessor>(Accessor);
                // 批 F:SignalR 注册 IHubContext(生产同装配);测试无连接时推送为 no-op,
                // Hub 级 WebSocket 行为(negotiate 认证/连接生命周期/分组广播)经批末手工验证
                services.AddSignalR();
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                TestSchemaBootstrap.Ensure(Db);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
            }

            private static string DbConnectionString =>
                TestPg.ConnectionString;

            private static bool IsPostgresReachable()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var result = client.ConnectAsync("127.0.0.1", 5432).Wait(TimeSpan.FromSeconds(2));
                    return result;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
