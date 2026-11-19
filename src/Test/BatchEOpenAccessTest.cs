using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Services.OpenAccess;
using AdminCore.Core.Services.OpenAccess.Dto;
using AdminCore.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 E 开放接口测试(1.6/1.7/6.10):签名算法纯函数(4)+ 身份服务 DB(5)。
    /// DB 用例经与生产同一条 AddSqlSugar 装配(仓储/AOP/软删全生效);探针数据 Guid 前缀独占 + finally 物理清理。
    /// 覆盖边界披露:Handler 级集成(真实 HTTP 管线 + scheme 触发)未在此覆盖(管线级验证走手工 curl,
    /// 口径同批 D 探针;纯函数与服务层为本批自动化边界)。
    /// </summary>
    public class BatchEOpenAccessTest
    {
        // ── 纯函数:签名算法 ──────────────────────────────────────────────

        /// <summary>Sign 产出的签名经 Verify 通过;篡改 sign 任意一位 → 拒绝。</summary>
        [Fact]
        public void 签名算法_正确签名通过篡改拒绝()
        {
            var secret = OpenAccessSignHelper.CreateSecret();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var sign = OpenAccessSignHelper.Sign(secret, "POST", "/api/openDemo/hello", "ak.test", ts, nonce);
            Assert.True(OpenAccessSignHelper.Verify(secret, sign, "POST", "/api/openDemo/hello", "ak.test", ts, nonce));

            // 篡改:换方法
            Assert.False(OpenAccessSignHelper.Verify(secret, sign, "GET", "/api/openDemo/hello", "ak.test", ts, nonce));
            // 篡改:换 url
            Assert.False(OpenAccessSignHelper.Verify(secret, sign, "POST", "/api/other", "ak.test", ts, nonce));
            // 篡改:换密钥
            Assert.False(OpenAccessSignHelper.Verify(OpenAccessSignHelper.CreateSecret(), sign, "POST", "/api/openDemo/hello", "ak.test", ts, nonce));
            // 空 sign
            Assert.False(OpenAccessSignHelper.Verify(secret, "", "POST", "/api/openDemo/hello", "ak.test", ts, nonce));
        }

        /// <summary>InWindow:当前时间通过;秒/毫秒格式均接受;超窗(±10min)拒绝;非数字拒绝。</summary>
        [Fact]
        public void 时间窗_秒毫秒自判超窗拒绝()
        {
            var drift = TimeSpan.FromMinutes(5);
            Assert.True(OpenAccessSignHelper.InWindow(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), drift));
            Assert.True(OpenAccessSignHelper.InWindow(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), drift));
            Assert.False(OpenAccessSignHelper.InWindow(
                DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString(), drift));
            Assert.False(OpenAccessSignHelper.InWindow(
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds().ToString(), drift));
            Assert.False(OpenAccessSignHelper.InWindow("not-a-number", drift));
        }

        /// <summary>CreateSecret:22 字符(参照口径)+ 多次生成互异。</summary>
        [Fact]
        public void 密钥生成_22字符且互异()
        {
            var set = new HashSet<string>();
            for (var i = 0; i < 100; i++)
            {
                var secret = OpenAccessSignHelper.CreateSecret();
                Assert.Equal(22, secret.Length);
                Assert.True(set.Add(secret), "100 次生成出现重复密钥");
            }
        }

        /// <summary>秒/毫秒阈值自判:秒级(9e9 以下)与毫秒级(9e9 以上)走各自换算路径且结果一致。</summary>
        [Fact]
        public void 时间戳_秒毫秒换算一致()
        {
            var instant = DateTimeOffset.UtcNow;
            var s = instant.ToUnixTimeSeconds().ToString();
            var ms = instant.ToUnixTimeMilliseconds().ToString();
            // 2026 年秒级已是 10 位(1.79e9)——阈值 9999999999(10 位)自判口径不受位数影响,断数值换算一致
            Assert.True(Math.Abs(long.Parse(ms) - long.Parse(s) * 1000) <= 999, "秒/毫秒换算应一致");
            Assert.True(OpenAccessSignHelper.InWindow(s, TimeSpan.FromSeconds(5)));
            Assert.True(OpenAccessSignHelper.InWindow(ms, TimeSpan.FromSeconds(5)));
        }

        // ── DB:身份服务 ─────────────────────────────────────────────────

        /// <summary>建号 → GetIdentity 缓存命中(两次取值一致)→ 重置密钥 → 旧签拒绝新签通过 → 删除 → 取数 null。</summary>
        [Fact]
        public async Task 身份_建号缓存重置删除()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            var user = await InsertProbeUserAsync(scope, marker, status: 0);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();

                // 建号:Secret 自动生成
                var (output, secret) = await svc.AddAsync(
                    new AddOpenAccessInput(marker + ".ak", user.Id, SqlSugarConst.DefaultTenantId));
                Assert.Equal(22, secret.Length);
                Assert.Equal(marker + ".ak", output.AccessKey);
                Assert.Equal(user.Account, output.BindUserAccount);

                // 取数(走缓存回填)
                var id1 = await svc.GetIdentityAsync(marker + ".ak");
                Assert.NotNull(id1);
                Assert.Equal(secret, id1!.AccessSecret);
                Assert.Equal(user.Id, id1.BindUserId);
                Assert.True(id1.BindUserValid);
                Assert.Equal(user.Account, id1.Account);

                // 重置密钥:旧密钥签拒绝、新密钥签通过 + 缓存失效
                var oldSign = OpenAccessSignHelper.Sign(secret, "GET", "/x", marker + ".ak", "123", "n1");
                var secret2 = await svc.ResetSecretAsync(output.Id);
                Assert.NotEqual(secret, secret2);
                var id2 = await svc.GetIdentityAsync(marker + ".ak");
                Assert.Equal(secret2, id2!.AccessSecret);
                var newSign = OpenAccessSignHelper.Sign(secret2, "GET", "/x", marker + ".ak", "123", "n1");
                Assert.True(OpenAccessSignHelper.Verify(secret2, newSign, "GET", "/x", marker + ".ak", "123", "n1"));
                Assert.False(OpenAccessSignHelper.Verify(secret2, oldSign, "GET", "/x", marker + ".ak", "123", "n1"));

                // 删除(软删)→ 取数 null
                await svc.DeleteAsync(output.Id);
                Assert.Null(await svc.GetIdentityAsync(marker + ".ak"));
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        /// <summary>改绑 → 缓存失效 → GetIdentity 反映新绑定(用户/租户)。</summary>
        [Fact]
        public async Task 身份_改绑后缓存失效反映新绑定()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            // 先造租户、再按目标租户造 user2：片 b 要求"绑定用户与 BindTenantId 同租户"，
            // 原写法拿**默认租户的用户**改绑到**探针租户**——那正是本次要拦的跨租户形态；
            // 此用例的本意是验"改绑后缓存失效"，不是声明"跨租户绑定合法"，故改测试数据使其自洽（不放宽校验）。
            var tenant = await InsertProbeTenantAsync(scope, marker);
            var user1 = await InsertProbeUserAsync(scope, marker, status: 0);
            var user2 = await InsertProbeUserAsync(scope, marker, status: 0, suffix: "b", tenantId: tenant.Id);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();

                var (output, _) = await svc.AddAsync(
                    new AddOpenAccessInput(marker + ".ak2", user1.Id, SqlSugarConst.DefaultTenantId));
                var before = await svc.GetIdentityAsync(marker + ".ak2");
                Assert.Equal(user1.Id, before!.BindUserId);

                await svc.UpdateAsync(new UpdateOpenAccessInput(output.Id, user2.Id, tenant.Id));
                var after = await svc.GetIdentityAsync(marker + ".ak2");
                Assert.Equal(user2.Id, after!.BindUserId);
                Assert.Equal(tenant.Id, after.BindTenantId);
                Assert.Equal(user2.Account, after.Account);
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        /// <summary>绑定用户停用 → BindUserValid=false(Handler 拒绝依据);用户不存在同口径。</summary>
        [Fact]
        public async Task 身份_绑定用户停用或不存在标记无效()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            var disabledUser = await InsertProbeUserAsync(scope, marker, status: 1);
            var okUser = await InsertProbeUserAsync(scope, marker, status: 0, suffix: "ok");
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();

                // ── S-G D1.4 片 a/b 改写后的契约：写侧直接拒绝，不再产出"建得出来却永远认证不过"的半成品号。
                //    （本用例在 2026-10-26 之前断言的是"写侧应宽容放行＋鉴权侧标记无效"——该契约已随片 a/b 作废，
                //      改写发生在**同一批**内，避免中间态出现"实现已收紧、测试还在要求放行"的假红。）
                var exDisabled = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
                    new AddOpenAccessInput(marker + ".ak3", disabledUser.Id, SqlSugarConst.DefaultTenantId)));
                Assert.Equal(ErrorCodes.OpenAccessBindUserDisabled, exDisabled.ErrorCode);

                var (output, _) = await svc.AddAsync(
                    new AddOpenAccessInput(marker + ".ak3ok", okUser.Id, SqlSugarConst.DefaultTenantId));
                var exMissing = await Assert.ThrowsAsync<AppException>(() => svc.UpdateAsync(
                    new UpdateOpenAccessInput(output.Id, long.MaxValue, SqlSugarConst.DefaultTenantId)));
                Assert.Equal(ErrorCodes.OpenAccessBindUserNotFound, exMissing.ErrorCode);

                // ── 鉴权侧 BindUserValid 防御**刻意保留**（纵深不可撤）：绑定行仍可能绕过服务层变坏
                //    （最典型＝建号之后用户才被停用），此时认证必须依然拒绝 ⇒ 直插一条停用绑定行来验。
                var directKey = marker + ".ak3-direct";
                await scope.Db.Insertable(new SysOpenAccess
                {
                    AccessKey = directKey,
                    AccessSecret = OpenAccessSignHelper.CreateSecret(),
                    BindUserId = disabledUser.Id,
                    BindTenantId = SqlSugarConst.DefaultTenantId
                }).ExecuteCommandAsync();

                var identity = await svc.GetIdentityAsync(directKey);
                Assert.NotNull(identity);
                Assert.False(identity!.BindUserValid, "鉴权侧仍须把停用用户标记为无效（写侧拦不住的状态漂移由它兜底）");
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        /// <summary>
        /// S-G D1.4 片 a/b：绑定目标校验四条拒绝**各归其码**（不存在/跨租户/超管/停用已由上一用例覆盖）＋合法路径必须放行。
        /// WHY 值得单独一条：这四条是本次收紧**全部对外可见的行为**，钉住错误码才谈得上"下游可预期"；
        /// 尤其**超管与跨租户两条此前零覆盖**——不写就等于没实现（下一次重构会把它静默删掉而全绿）。
        /// 末段"同租户合法绑定必须放行"是防**校验过严把正常业务打死**，缺它这条用例只证一半。
        /// </summary>
        [Fact]
        public async Task 绑定校验_四条拒绝各归其码()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            var tenant = await InsertProbeTenantAsync(scope, marker);
            var superUser = await InsertProbeUserAsync(scope, marker, status: 0, suffix: "super");
            // 超管判定值取自 UserManager.SuperAdminAccountType（**不在此另写 999**，否则常量失去单点意义）
            superUser.AccountType = UserManager.SuperAdminAccountType;
            await scope.Db.Updateable(superUser).ExecuteCommandAsync();
            var otherTenantUser = await InsertProbeUserAsync(scope, marker, status: 0, suffix: "other", tenantId: tenant.Id);

            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();

                var exNotFound = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
                    new AddOpenAccessInput(marker + ".v1", long.MaxValue, SqlSugarConst.DefaultTenantId)));
                Assert.Equal(ErrorCodes.OpenAccessBindUserNotFound, exNotFound.ErrorCode);

                var exTenant = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
                    new AddOpenAccessInput(marker + ".v2", otherTenantUser.Id, SqlSugarConst.DefaultTenantId)));
                Assert.Equal(ErrorCodes.OpenAccessBindTenantMismatch, exTenant.ErrorCode);

                var exSuper = await Assert.ThrowsAsync<AppException>(() => svc.AddAsync(
                    new AddOpenAccessInput(marker + ".v3", superUser.Id, SqlSugarConst.DefaultTenantId)));
                Assert.Equal(ErrorCodes.OpenAccessBindUserSuperAdmin, exSuper.ErrorCode);

                // 合法路径：绑定用户与目标租户同租户 ⇒ 必须放行（否则片 b 就是把正常业务打死）
                var (output, _) = await svc.AddAsync(
                    new AddOpenAccessInput(marker + ".v4", otherTenantUser.Id, tenant.Id));
                Assert.Equal(otherTenantUser.Id, output.BindUserId);
                Assert.Equal(tenant.Id, output.BindTenantId);
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        /// <summary>分页:AccessKey 包含过滤命中本用例行、不含其他探针行;详情不含密钥字段。</summary>
        [Fact]
        public async Task 分页_AccessKey过滤命中()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            var user = await InsertProbeUserAsync(scope, marker, status: 0);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();
                await svc.AddAsync(new AddOpenAccessInput(marker + ".ak4a", user.Id, SqlSugarConst.DefaultTenantId));
                await svc.AddAsync(new AddOpenAccessInput(marker + ".ak4b", user.Id, SqlSugarConst.DefaultTenantId));

                var page = await svc.PageAsync(new PageOpenAccessInput(marker + ".ak4"));
                Assert.Equal(2, page.Total);
                Assert.All(page.List, u => Assert.Contains(u.AccessKey,
                    new[] { marker + ".ak4a", marker + ".ak4b" }));
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        /// <summary>签名预览:Timestamp=0 服务端填当前秒、Nonce 缺省生成;输出 sign 经 Verify 可复验(与 Handler 同算法)。</summary>
        [Fact]
        public async Task 签名预览_输出可复验()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable)
            {
                Assert.Fail("降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过");
            }

            var marker = $"batche_{Guid.NewGuid():N}";
            var user = await InsertProbeUserAsync(scope, marker, status: 0);
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysOpenAccessService>();
                var (_, secret) = await svc.AddAsync(
                    new AddOpenAccessInput(marker + ".ak5", user.Id, SqlSugarConst.DefaultTenantId));

                var gen = await svc.GenerateSignatureAsync(
                    new GenerateSignatureInput(marker + ".ak5", secret, "GET", "/api/openDemo/hello"));
                Assert.True(gen.Timestamp > 0);
                Assert.False(string.IsNullOrEmpty(gen.Nonce));
                Assert.True(OpenAccessSignHelper.Verify(
                    secret, gen.Signature, "GET", "/api/openDemo/hello", marker + ".ak5", gen.Timestamp.ToString(), gen.Nonce),
                    "预览 sign 必须与 Handler 验签口径一致");
            }
            finally
            {
                await CleanupAsync(scope, marker);
            }
        }

        // ── 辅助 ─────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        private static async Task<SysUser> InsertProbeUserAsync(DbScope scope, string marker, int status, string suffix = "", long? tenantId = null)
        {
            var db = scope.Db;
            var account = $"{marker}{suffix}u";
            var user = new SysUser
            {
                Account = account,
                Password = "probe-not-a-real-hash",
                RealName = "批E探针用户",
                Status = status,
                // S-G D1.4 片 b 后：租户可指入参（改绑测试需要一个"属于目标租户"的用户，
                // 否则绑定目标校验会正确拒绝跨租户绑定——那是本次要拦的形态，不是要放宽的口径）。
                TenantId = tenantId ?? SqlSugarConst.DefaultTenantId
            };
            await db.Insertable(user).ExecuteCommandAsync();
            return user;
        }

        private static async Task<SysTenant> InsertProbeTenantAsync(DbScope scope, string marker)
        {
            var db = scope.Db;
            var tenant = new SysTenant { Name = marker + "t" };
            await db.Insertable(tenant).ExecuteCommandAsync();
            return tenant;
        }

        private static async Task CleanupAsync(DbScope scope, string marker)
        {
            var db = scope.Db;
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_open_access where \"AccessKey\" like @p", new { p = marker + "%" });
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_user where \"Account\" like @p", new { p = marker + "%" });
            await db.Ado.ExecuteCommandAsync(
                "delete from sys_tenant where \"Name\" like @p", new { p = marker + "%" });
        }

        /// <summary>
        /// DB 作用域:与生产同一条 AddSqlSugar/AddCache 装配(仓储/AOP/软删/缓存全生效);
        /// CodeFirst 扫 Core 程序集(新表 sys_open_access 自动建)。xUnit 同类同线程串行、跨类并行各用 Guid 探针独占。
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
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);                services.AddSignalR(); // 批 F:SysUserService/AuthService → SysOnlineUserService → IHubContext(AddSignalR 注册;无连接时推送 no-op)

                services.AddAutoServices(typeof(SysUser).Assembly);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                Db.HealColumnCase();
                Db.InitTables(typeof(SqlSugarSetup).Assembly);
                PgReachable = true;
            }

            public void Dispose()
            {
                _provider?.Dispose();
                (Db as IDisposable)?.Dispose();
            }

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

            private const string DbHost = "127.0.0.1";
            private const int DbPort = 5432;
            private const string DbConnectionString =
                TestPg.ConnectionString;
        }
    }
}
