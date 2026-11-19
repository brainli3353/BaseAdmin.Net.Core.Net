using System.Net.Sockets;
using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Role;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Services.RoleTable.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

// SqlSugar 自带同名的 ICacheService,用别名消歧(与 BatchAH 同口径)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BH(role 角色管理复审)修复验证:
    /// D1 角色删除缺 sys_role_table 级联(孤儿行 + 受管声明/缓存最长 7 天漂移)——验 DeleteAsync 级联物理删
    ///    + 该表全库无授权行时受管声明撤销(受管集合不含该表);
    /// D2 角色复制缺第 4 张关联表(sys_role_table)复制(副本静默丢列白名单)——验 CopyRoleAsync 副本
    ///    字段权限集与源全等、源不变;
    /// D3 契约固化:前端新增"数据范围"对话框保存路径依赖的 grantOrg 全量替换/空集清空语义(非自定义
    ///    范围保存 ⇒ 提交空集 ⇒ 清空自定义机构, fail-closed)。
    /// 探针:99_000_000_005_xx 段(批 BH 专用),固定 Id 幂等重建,finally 物理清理;操作者用超管 999
    /// 身份(豁免越权防护,聚焦存储语义)。
    /// </summary>
    public class BatchBHRoleTest
    {
        // 开发库连接串(与 BatchAD/H/AH 同源,dev only;Timeout=10 让不可达场景快速失败而非挂死)
        private const string DbConnectionString =
            TestPg.ConnectionString;

        private const string Downgrade = "降级:PostgreSQL 不可达——本用例未实跑,禁止冒充通过";

        // 探针固定 Id(99_000_000_005_xx 段 = 批 BH;幂等重建 + finally 物理清理)
        private const long ProbeRole = 99_000_000_005_01;
        private const long ProbeOperator = 99_000_000_005_11; // 超管(999)操作者身份:豁免越权防护
        private const long OrgA = 99_000_000_005_21;
        private const long OrgB = 99_000_000_005_22;

        // 字段权限探针表(与 BatchAH 同表;授权行状态净零,清理见 CleanupAsync)
        private const string TableUser = "sys_user";

        /// <summary>受管表集合缓存键(与 SysRoleTableService.GetManagedTablesAsync 内部拼接一致)。</summary>
        private static readonly string ManagedTablesKey = CacheConst.KeyRoleTable + "__managed";

        /// <summary>D2:角色复制第 4 张关联表(sys_role_table)全量复制——副本字段权限集与源全等,源不变。</summary>
        [Fact]
        public async Task 复制角色_第四张表SysRoleTable全量复制()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeRoleAsync(scope);
            scope.FakeAccessor.HttpContext = BuildUserContext(ProbeOperator, 999);

            long copyId = 0;
            try
            {
                using var unit = NewUnit(scope);
                var rtSvc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();
                var roleSvc = unit.ServiceProvider.GetRequiredService<SysRoleService>();

                // 源角色授字段权限(全量替换;提交序故意乱序,顺带固化回读稳定序)
                await rtSvc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:RealName", "sys_user:Account"]));
                Assert.Equal(new List<string> { "sys_user:Account", "sys_user:RealName" },
                             (await rtSvc.GetRoleTableAsync(ProbeRole)).Items);

                copyId = await roleSvc.CopyRoleAsync(ProbeRole);
                Assert.NotEqual(0, copyId);
                Assert.NotEqual(ProbeRole, copyId);

                // D2:副本字段权限集与源全等(修复前恰缺这张表——副本静默丢列白名单,受管表全裁空)
                var copyRead = await rtSvc.GetRoleTableAsync(copyId);
                Assert.Equal(new List<string> { "sys_user:Account", "sys_user:RealName" }, copyRead.Items);

                // 源不变(复制是快照读 + 新 Id 插入,不动源行)
                Assert.Equal(new List<string> { "sys_user:Account", "sys_user:RealName" },
                             (await rtSvc.GetRoleTableAsync(ProbeRole)).Items);
            }
            finally
            {
                await CleanupAsync(scope, copyId);
            }
        }

        /// <summary>D1:角色删除 sys_role_table 级联——授权行物理删(不留孤儿)+ 受管声明重算(该表无授权行 ⇒ 撤销)。</summary>
        [Fact]
        public async Task 删除角色_SysRoleTable级联且受管声明重算()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeRoleAsync(scope);
            scope.FakeAccessor.HttpContext = BuildUserContext(ProbeOperator, 999);

            using var unit = NewUnit(scope);
            var rtSvc = unit.ServiceProvider.GetRequiredService<SysRoleTableService>();
            var roleSvc = unit.ServiceProvider.GetRequiredService<SysRoleService>();

            // 前置:sys_user 不受管(全库无该表授权行)——否则后置"声明撤销"断言归因失效
            Assert.DoesNotContain(TableUser, await rtSvc.GetManagedTablesAsync());

            await rtSvc.GrantAsync(new RoleTableInput(ProbeRole, ["sys_user:Account"]));
            // 授权即入册受管声明(批 AH 口径):受管集合含 sys_user
            Assert.Contains(TableUser, await rtSvc.GetManagedTablesAsync());

            try
            {
                await roleSvc.DeleteAsync(ProbeRole);

                // 角色软删(物理行保留、IsDelete 标记;与 DeleteAsync 契约一致)
                var isDeleted = await scope.Db.Ado.SqlQuerySingleAsync<bool>(
                    "SELECT \"IsDelete\" FROM sys_role WHERE id = @id", new SugarParameter("@id", ProbeRole));
                Assert.True(isDeleted, "软删行的 IsDelete 应为 true");

                // D1:授权行物理删(不留孤儿行;修复前此步缺失,最长 7 天漂移)
                Assert.Equal(0, await CountRoleTableAsync(scope, ProbeRole));

                // 受管重算:该角色是 sys_user 全库唯一授权角色 ⇒ 声明撤销,受管集合不再含该表
                Assert.DoesNotContain(TableUser, await rtSvc.GetManagedTablesAsync());
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        /// <summary>D3 契约:grantOrg 全量替换 + 空集清空——前端"数据范围"对话框保存路径依赖的存储契约。</summary>
        [Fact]
        public async Task 数据范围机构_全量替换与空集清空()
        {
            await using var scope = await DbScope.CreateAsync();
            if (!scope.PgReachable)
            {
                Assert.Fail(Downgrade);
            }

            await EnsureProbeRoleAsync(scope);
            await EnsureProbeOrgsAsync(scope);
            scope.FakeAccessor.HttpContext = BuildUserContext(ProbeOperator, 999);

            using var unit = NewUnit(scope);
            var roleSvc = unit.ServiceProvider.GetRequiredService<SysRoleService>();

            try
            {
                // 授 2 机构 → 回读 2
                await roleSvc.GrantOrgAsync(new GrantOrgInput(ProbeRole, [OrgA, OrgB]));
                Assert.Equal(new List<long> { OrgA, OrgB }, (await roleSvc.GetOrgListAsync(ProbeRole)).OrderBy(x => x).ToList());

                // 全量替换:缩为 1 → 回读即新集(旧集不残留)
                await roleSvc.GrantOrgAsync(new GrantOrgInput(ProbeRole, [OrgA]));
                Assert.Equal(new List<long> { OrgA }, (await roleSvc.GetOrgListAsync(ProbeRole)).OrderBy(x => x).ToList());

                // 空集 = 清空(前端非自定义范围保存提交空集的 fail-closed 口径)
                await roleSvc.GrantOrgAsync(new GrantOrgInput(ProbeRole, []));
                Assert.Empty(await roleSvc.GetOrgListAsync(ProbeRole));
            }
            finally
            {
                await CleanupAsync(scope);
            }
        }

        // ── 辅助 ────────────────────────────────────────────────────────────

        private static IServiceScope NewUnit(DbScope scope) => scope.Provider.CreateScope();

        private static async Task<int> CountRoleTableAsync(DbScope scope, long roleId) =>
            await scope.Db.Queryable<SysRoleTable>().Where(r => r.RoleId == roleId).CountAsync();

        private static async Task EnsureProbeRoleAsync(DbScope scope)
        {
            if (await scope.Db.Queryable<SysRole>().AnyAsync(r => r.Id == ProbeRole))
            {
                return;
            }

            // Remark/Code 显式给值:SqlSugar 对 string 引用类型 CodeFirst 默认 NOT NULL(批 V 部署验证抓出的坑)
            await scope.Db.Insertable(new SysRole
            {
                Id = ProbeRole,
                Name = "dsh_bh_探针角色",
                Code = "dsh_bh_probe_role",
                Remark = "批 BH 探针",
                DataScope = 4,
                SysFlag = false,
                TenantId = SqlSugarConst.DefaultTenantId
            }).ExecuteCommandAsync();
        }

        private static async Task EnsureProbeOrgsAsync(DbScope scope)
        {
            var db = scope.Db;
            foreach (var (id, code, name) in new[]
                     {
                         (OrgA, "dsh_bh_org_a", "dsh_bh_探针机构A"),
                         (OrgB, "dsh_bh_org_b", "dsh_bh_探针机构B")
                     })
            {
                if (await db.Queryable<SysOrg>().AnyAsync(u => u.Id == id))
                {
                    continue;
                }

                await db.Insertable(new SysOrg
                {
                    Id = id,
                    Pid = 0,
                    Name = name,
                    Code = code,
                    Sort = 0,
                    Status = 0,
                    TenantId = SqlSugarConst.DefaultTenantId
                }).ExecuteCommandAsync();
            }
        }

        /// <summary>
        /// 零污染清理(物理删,幂等):授权行 → 机构授权 → 机构 → 角色 → 受管开关行,再清缓存键。
        /// sys_role 走裸 DELETE:软删后该行 IsDelete=true,全局软删过滤器(查询/更新/删除自动追加
        /// IsDelete=false)会让 Deleteable 看不见它——不清则重跑 Ensure 插入撞主键(实测口径)。
        /// </summary>
        private static async Task CleanupAsync(DbScope scope, long copyId = 0)
        {
            if (!scope.PgReachable)
            {
                return;
            }

            var roleIds = copyId > 0 ? new[] { ProbeRole, copyId } : new[] { ProbeRole };

            // 条件内联常量数组:SqlSugar 解析表达式树时拒读 private 字段(批 AH 实测同口径)
            await scope.Db.Deleteable<SysRoleTable>()
                .Where(r => roleIds.Contains(r.RoleId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysRoleOrg>()
                .Where(r => roleIds.Contains(r.RoleId)).ExecuteCommandAsync();
            await scope.Db.Deleteable<SysOrg>()
                .Where(o => new[] { OrgA, OrgB }.Contains(o.Id)).ExecuteCommandAsync();
            foreach (var roleId in roleIds)
            {
                await scope.Db.Ado.ExecuteCommandAsync(
                    "DELETE FROM sys_role WHERE id = @id", new SugarParameter("@id", roleId));
            }

            // sys_user 受管开关行(批 AK 起授权即入册;不清则残留行把"sys_user 不受管"前置断言打翻)
            await scope.Db.Deleteable<SysTableFieldScope>()
                .Where(s => s.TableName == TableUser).ExecuteCommandAsync();

            var cache = scope.Provider.GetRequiredService<ICacheService>();
            foreach (var roleId in roleIds)
            {
                await cache.RemoveAsync($"{CacheConst.KeyRoleTable}{roleId}");
            }

            await cache.RemoveAsync(ManagedTablesKey);
        }

        /// <summary>模拟登录身份(Claim 键与生产 JwtClaimNames 同源;超管 999 豁免 grantOrg 越权防护)。</summary>
        private static HttpContext BuildUserContext(long userId, int accountType)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(JwtClaimNames.UserId, userId.ToString()),
                new Claim(JwtClaimNames.Account, "dsh_bh_probe_operator"),
                new Claim(JwtClaimNames.RealName, "dsh_bh_探针操作者"),
                new Claim(JwtClaimNames.AccountType, accountType.ToString()),
                new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString())
            }, "test"));
            return context;
        }

        /// <summary>
        /// DB 测试容器:与生产同一条 AddSqlSugar 注册路径 + AddCache + AddProjectOptions + AddAutoServices(Core 程序集,
        /// 解析 [Service] 的 SysRoleService/SysRoleTableService),InitTables 扫 Core 程序集增量建表(幂等)。
        /// WHY 异步工厂而非同步构造:PG 探测是 I/O,同步构造只能 .Wait()/.Result 硬等(项目硬约定禁),
        /// 故探测改 await(TcpClient.ConnectAsync),容器经 CreateAsync 产出(与 BatchAH 同口径)。
        /// </summary>
        private sealed class DbScope : IAsyncDisposable
        {
            public ISqlSugarClient Db { get; private set; } = null!;

            public ServiceProvider Provider { get; private set; } = null!;

            public bool PgReachable { get; private set; }

            public FakeHttpContextAccessor FakeAccessor { get; } = new();

            public static async Task<DbScope> CreateAsync()
            {
                var scope = new DbScope();
                if (!await IsPostgresReachableAsync())
                {
                    // 降级:不建容器(用例侧 Assert.Fail);Provider 保持空壳以免调用方 NRE 掩盖真实原因
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
                services.AddSingleton<IHttpContextAccessor>(scope.FakeAccessor); // 须先于 AddSqlSugar(TryAdd 语义)
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config); // 无 ConnectionStrings:Redis → 内存实现(进程内,测试互不串扰)
                services.AddAutoServices(typeof(SysRoleService).Assembly);

                scope.Provider = services.BuildServiceProvider();
                scope.Db = scope.Provider.GetRequiredService<ISqlSugarClient>();
                TestSchemaBootstrap.Ensure(scope.Db);
                scope.PgReachable = true;
                return scope;
            }

            public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

            /// <summary>短超时 TCP 探测(2 秒,PG 黑洞防挂死;同 BatchAH 口径,不阻塞等待)。</summary>
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

        /// <summary>模拟 IHttpContextAccessor(注册先于 AddSqlSugar 的 TryAdd;AOP 审计/租户填充读它)。</summary>
        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
