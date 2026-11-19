using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Security.Claims;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 H(2.2 CopyRole / 2.4 菜单授权回读 / 2.5 机构裁剪 Disabled)测试。
    /// 覆盖:菜单回读与 grantMenu 对偶(全量替换语义)/CopyRole 三表全量复制＋Code 碰撞拒绝＋源不存在拒绝/
    /// Tree 裁剪三视角(超管全 false/非超管范围外 true/匿名无 Claims 全 false)。
    /// 探针:角色/机构/用户均 dsh_h_ 前缀防撞名,固定 Id 幂等重建,finally 清理。
    /// </summary>
    public class BatchHRoleOrgTest
    {
        // 探针实体(固定 Id,幂等重建)
        private const long ProbeRole = 99_000_000_002_01;
        private const long ProbeUser = 99_000_000_002_02;
        private const long OrgA = 99_000_000_002_03;
        private const long OrgB = 99_000_000_002_04;
        private const string RoleCode = "dsh_h_probe_role";
        private const string Account = "dsh_h_role_org";

        // 授权用菜单 Id(取种子固定值:按钮 10020 角色查询 + 菜单 10003 系统管理)
        private static readonly long[] MenuPair = [10003, 10020];

        [Fact]
        public async Task 菜单授权_回读与全量替换对偶()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeRoleAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(ProbeUser, "777");

            var roleId = ProbeRole;
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleService>();

                // 全量授权 2 菜单 → 回读 = 该 2
                await svc.GrantMenuAsync(new GrantMenuInput(roleId, MenuPair.ToList()));
                var read1 = await svc.GetMenuListAsync(roleId);
                Assert.Equal(2, read1.Count);
                Assert.Contains(10003, read1);
                Assert.Contains(10020, read1);

                // 缩减授权 1 菜单 → 回读反映新集(全量替换语义)
                await svc.GrantMenuAsync(new GrantMenuInput(roleId, [10020]));
                var read2 = await svc.GetMenuListAsync(roleId);
                Assert.Equal(new List<long> { 10020 }, read2);
            }
            finally
            {
                await CleanupRoleAsync(scope, roleId);
            }
        }

        [Fact]
        public async Task 复制角色_三表全量复制()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeRoleAsync(scope);
            await EnsureProbeOrgsAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(ProbeUser, "777");

            long srcId = 0, newId = 0;
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleService>();
                srcId = ProbeRole;

                // 源角色先授:2 菜单 + 1 接口 + 1 机构(自定义范围)
                await svc.GrantMenuAsync(new GrantMenuInput(srcId, MenuPair.ToList()));
                await svc.GrantApiAsync(new GrantApiInput(srcId, ["sysRole/page"]));
                await svc.GrantOrgAsync(new GrantOrgInput(srcId, [OrgA]));

                // 复制
                newId = await svc.CopyRoleAsync(srcId);
                Assert.NotEqual(srcId, newId);

                // 新角色存在且 Code/Name 追加 " - copy"(按新 Id 直查)
                var copied = (await scope.Db.Queryable<SysRole>().FirstAsync(u => u.Id == newId))!;
                Assert.NotNull(copied);
                Assert.EndsWith(" - copy", copied.Code);
                Assert.EndsWith(" - copy", copied.Name);

                // 三张关联表集合相等
                Assert.Equal((await svc.GetMenuListAsync(srcId)).OrderBy(x => x).ToList(),
                             (await svc.GetMenuListAsync(newId)).OrderBy(x => x).ToList());
                Assert.Equal(await svc.GetApiListAsync(srcId), await svc.GetApiListAsync(newId));
                Assert.Equal(await svc.GetOrgListAsync(srcId), await svc.GetOrgListAsync(newId));
            }
            finally
            {
                if (newId > 0)
                {
                    await CleanupRoleAsync(scope, newId);
                }
                await CleanupProbeRoleFullAsync(scope);
            }
        }

        [Fact]
        public async Task 复制角色_Code碰撞与源不存在拒绝()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeRoleAsync(scope);
            scope.FakeAccessor.HttpContext = UserContext(ProbeUser, "777");

            long srcId = 0, firstCopy = 0;
            try
            {
                using var unit = NewUnit(scope);
                var svc = unit.ServiceProvider.GetRequiredService<SysRoleService>();
                srcId = ProbeRole;

                // 首次复制成功
                firstCopy = await svc.CopyRoleAsync(srcId);
                Assert.True(firstCopy > 0);

                // 二次复制同一源 → 新 Code(" - copy")已存在 → 拒绝
                await Assert.ThrowsAsync<AppException>(() => svc.CopyRoleAsync(srcId));

                // 源不存在 → 拒绝
                await Assert.ThrowsAsync<AppException>(() => svc.CopyRoleAsync(99_999_999_999_999));
            }
            finally
            {
                if (firstCopy > 0)
                {
                    await CleanupRoleAsync(scope, firstCopy);
                }
                await CleanupProbeRoleFullAsync(scope);
            }
        }

        [Fact]
        public async Task 机构树_超管全量不标记禁用()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeOrgsAsync(scope);
            // 超管(999)
            scope.FakeAccessor.HttpContext = UserContext(ProbeUser, "999");

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();
            var tree = await svc.TreeAsync();

            Assert.True(tree.Count > 0);
            Assert.All(Flat(tree), n => Assert.False(n.Disabled));
        }

        [Fact]
        public async Task 机构树_非超管范围外标记禁用()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeOrgsAsync(scope);
            await EnsureProbeUserAndRoleAsync(scope);

            // 探针用户(777,OrgId=OrgA,角色 DataScope=3 本部门)
            scope.FakeAccessor.HttpContext = UserContext(ProbeUser, "777");

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();
            var tree = Flat(await svc.TreeAsync());

            // 先拿到探针用户数据范围(枢纽口径):Org=3 → 仅本人所属机构 OrgA
            var (maxScope, orgIds) = await svc.GetUserOrgScopeAsync(ProbeUser);
            Assert.Equal(DataScopeEnum.Org, maxScope);
            Assert.Contains(OrgA, orgIds);

            var byId = tree.ToDictionary(n => n.Id);
            Assert.Contains(OrgA, byId.Keys);
            Assert.False(byId[OrgA].Disabled);       // 范围内 → 可选
            Assert.Contains(OrgB, byId.Keys);
            Assert.True(byId[OrgB].Disabled);        // 范围外 → 禁用
        }

        [Fact]
        public async Task 机构树_匿名无Claims不标记禁用()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }

            await EnsureProbeOrgsAsync(scope);
            scope.FakeAccessor.HttpContext = null; // 无 Claims(测试/内部调用)

            using var unit = NewUnit(scope);
            var svc = unit.ServiceProvider.GetRequiredService<SysOrgService>();
            var tree = await svc.TreeAsync();

            Assert.All(Flat(tree), n => Assert.False(n.Disabled));
        }

        // ── 工具 ──────────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        /// <summary>探针机构幂等重建(Pid=0 顶级;dsh_h 前缀防撞名)。</summary>
        private static async Task EnsureProbeOrgsAsync(DbScope scope)
        {
            var db = scope.Db;
            foreach (var (id, code, name) in new[] { (OrgA, "dsh_h_org_a", "探针机构A"), (OrgB, "dsh_h_org_b", "探针机构B") })
            {
                var exists = await db.Queryable<SysOrg>().AnyAsync(u => u.Id == id);
                if (!exists)
                {
                    await db.Insertable(new SysOrg { Id = id, Pid = 0, Name = name, Code = code, Sort = 0, Status = 0 }).ExecuteCommandAsync();
                }
            }
        }

        /// <summary>探针角色幂等重建(DataScope 缺省 4;此处显式 3 本部门)。</summary>
        private static async Task EnsureProbeRoleAsync(DbScope scope)
        {
            var db = scope.Db;
            var exists = await db.Queryable<SysRole>().AnyAsync(u => u.Id == ProbeRole);
            if (!exists)
            {
                // Remark 显式给值(参照类型 CodeFirst 默认 NOT NULL,批 D 坑 12 同族)
                await db.Insertable(new SysRole { Id = ProbeRole, Name = "探针角色H", Code = RoleCode, Remark = "批 H 探针", DataScope = 3, SysFlag = false }).ExecuteCommandAsync();
            }
        }

        /// <summary>探针用户(OrgId=OrgA)＋用户-角色绑定幂等重建。</summary>
        private static async Task EnsureProbeUserAndRoleAsync(DbScope scope)
        {
            await EnsureProbeRoleAsync(scope);
            var db = scope.Db;

            var user = await db.Queryable<SysUser>().FirstAsync(u => u.Id == ProbeUser);
            if (user is null)
            {
                await db.Insertable(new SysUser
                {
                    Id = ProbeUser,
                    Account = Account,
                    RealName = "探针丙",
                    Password = "x",
                    AccountType = 777,
                    OrgId = OrgA,
                    Status = 0,
                    TokenVersion = 1
                }).ExecuteCommandAsync();
            }

            var bound = await db.Queryable<SysUserRole>().AnyAsync(u => u.UserId == ProbeUser && u.RoleId == ProbeRole);
            if (!bound)
            {
                await db.Insertable(new SysUserRole { UserId = ProbeUser, RoleId = ProbeRole }).ExecuteCommandAsync();
            }
        }

        private static List<OrgOutput> Flat(List<OrgOutput> nodes)
        {
            var result = new List<OrgOutput>();
            void Walk(List<OrgOutput> list)
            {
                foreach (var n in list)
                {
                    result.Add(n);
                    Walk(n.Children);
                }
            }
            Walk(nodes);
            return result;
        }

        private static HttpContext UserContext(long userId, string accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, Account),
                new Claim(JwtClaimNames.RealName, "探针丙"),
                new Claim(JwtClaimNames.AccountType, accountType),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
        }

        /// <summary>探针角色清理(关联三表 + 角色行)。</summary>
        private static async Task CleanupRoleAsync(DbScope scope, long roleId)
        {
            var db = scope.Db;
            await db.Deleteable<SysRoleMenu>().Where(u => u.RoleId == roleId).ExecuteCommandAsync();
            await db.Deleteable<SysRoleApi>().Where(u => u.RoleId == roleId).ExecuteCommandAsync();
            await db.Deleteable<SysRoleOrg>().Where(u => u.RoleId == roleId).ExecuteCommandAsync();
            await db.Deleteable<SysRole>().Where(u => u.Id == roleId).ExecuteCommandAsync();
        }

        /// <summary>探针角色(固定 Id)全量清理。</summary>
        private static async Task CleanupProbeRoleFullAsync(DbScope scope)
        {
            await CleanupRoleAsync(scope, ProbeRole);
            await scope.Db.Deleteable<SysUserRole>().Where(u => u.UserId == ProbeUser && u.RoleId == ProbeRole).ExecuteCommandAsync();
        }

        /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd,AOP 审计/租户填充读它)。</summary>
        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;

            public IServiceProvider Provider { get; } = null!;

            public bool PgReachable { get; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

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
                services.AddSingleton<IHttpContextAccessor>(FakeAccessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysRole).Assembly);
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
