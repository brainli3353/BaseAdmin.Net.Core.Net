using System.Security.Claims;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Serial;
using AdminCore.Core.Services.Serial.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 BW sysSerial 复审回归:
    /// D1＝Page keyword 空白判据分裂(`keyword != null` vs 全站 IsNullOrWhiteSpace)——全空白 keyword 视为无过滤;
    /// D2＝Add/Update 判重缺显式租户限定(参照 `u.TenantId == _userManager.TenantId`)——
    ///     超管豁免租户过滤走全表判重时,他租户已有同 Type 误拦本(默认)租户建号(活体实锤 code=400 serial.type_exists)。
    /// 容器口径同 BatchWSerialTest.DbScope(真实 PG);principal=默认租户超管(仓储无请求级租户过滤=超管全表视角,
    /// 正是 D2 误拦场景的忠实复现)。
    /// </summary>
    public class BatchBWSerialTest
    {
        private const long OtherTenantId = 9999999999999L; // 探针用虚构租户(不污染真实租户面)

        [Fact]
        public async Task 分页_全空白keyword视为无过滤_D1()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = scope.GetSerialService();
            var id = await scope.EnsureTestSerialAsync(svc, "{yyyy}{MM}{dd}-{SEQ}", 1, 9999);
            try
            {
                var noFilter = await svc.PageAsync(new PageSerialInput(1, 10, "test", null, null));
                var blank = await svc.PageAsync(new PageSerialInput(1, 10, "test", null, "   "));
                Assert.True(noFilter.Total == blank.Total, $"全空白 keyword 应与无过滤同效(全站 IsNullOrWhiteSpace 口径): noFilter={noFilter.Total} blank={blank.Total}");
                Assert.Contains(blank.List, u => u.Id == id);
            }
            finally
            {
                await scope.CleanupSerialAsync(id);
            }
        }

        [Fact]
        public async Task 新增_他租户已有同Type_默认租户放行_D2()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = scope.GetSerialService();
            var otherId = await scope.InsertOtherTenantRowAsync();
            var myId = 0L;
            try
            {
                // 修复前:全表判重看到他租户行 → 400 serial.type_exists(活体实锤);修复后:限定本租户 → 放行
                myId = await svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Day, "{yyyy}{SEQ}"));
                Assert.True(myId > 0, "他租户已有同 Type 时,本租户建号应放行(参照 u.TenantId == _userManager.TenantId 口径)");
            }
            finally
            {
                if (myId > 0) { await scope.CleanupSerialAsync(myId); }
                await scope.DeleteRowAsync(otherId);
            }
        }

        [Fact]
        public async Task 新增_同租户重复仍拒绝_不变量()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = scope.GetSerialService();
            var id = await scope.EnsureTestSerialAsync(svc, "{SEQ}", 1, 9999);
            try
            {
                await Assert.ThrowsAsync<AppException>(() =>
                    svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Never, "{SEQ}")));
            }
            finally
            {
                await scope.CleanupSerialAsync(id);
            }
        }

        [Fact]
        public async Task 更新_他租户已有同Type_默认租户同值更新不受影响_D2()
        {
            using var scope = new DbScope();
            if (!scope.PgReachable) { Assert.Fail("PG 不可达,跳过"); }
            var svc = scope.GetSerialService();
            var other = await scope.InsertOtherTenantRowAsync();
            var id = 0L;
            try
            {
                // 单类型注册集下(内置 test)Update 判重分支(input.Type != entity.Type)不可达——
                // 断言面=他租户已有 test 时,本租户同值更新不触发 type_exists(修复不改变同值路径)
                id = await svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Day, "{yyyy}{SEQ}", 1, 9999));
                await svc.UpdateAsync(new UpdateSerialInput(id, "test", ResetIntervalEnum.Day, "{yyyy}{SEQ}", 1, 9999));
            }
            finally
            {
                if (id > 0) { await scope.CleanupSerialAsync(id); }
                await scope.DeleteRowAsync(other);
            }
        }

        /// <summary>DbScope:同 BatchW 口径(真实 PG + 全容器)+ 默认租户超管 principal。</summary>
        private sealed class DbScope : IDisposable
        {
            private ServiceProvider? _provider;

            public ISqlSugarClient Db { get; } = null!;
            public IServiceProvider Provider { get; } = null!;
            public bool PgReachable { get; }

            public DbScope()
            {
                if (!IsPostgresReachable()) { return; }

                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = DbConnectionString,
                        ["Yit:WorkerId"] = "1"
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddLogging();
                var accessor = new FakeHttpContextAccessor();
                // 默认租户超管:仓储无请求级租户过滤(无 middleware)= 超管全表视角,忠实复现 D2 误拦场景
                accessor.HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(JwtClaimNames.UserId, "841915844415557"),
                        new Claim(JwtClaimNames.TenantId, SqlSugarConst.DefaultTenantId.ToString()),
                        new Claim(JwtClaimNames.Account, "admin"),
                        new Claim(JwtClaimNames.AccountType, "999"),
                    }, "Test"))
                };
                services.AddSingleton<IHttpContextAccessor>(accessor);
                services.AddSingleton<IConfiguration>(config);
                services.AddSqlSugar(config);
                services.AddProjectOptions(config);
                services.AddCache(config);
                services.AddAutoServices(typeof(SysNotice).Assembly);
                _provider = services.BuildServiceProvider();
                Db = _provider.GetRequiredService<ISqlSugarClient>();
                Provider = _provider;
                TestSchemaBootstrap.Ensure(Db);
                PgReachable = true;
            }

            public SysSerialService GetSerialService() => Provider.GetRequiredService<SysSerialService>();

            /// <summary>幂等建默认租户 test 配置行(先物理清默认租户 test 残留,保证用例间隔离)。</summary>
            public async Task<long> EnsureTestSerialAsync(SysSerialService svc, string formater, long min, long max)
            {
                await Db.Deleteable<SysSerial>().Where(u => u.Type == "test" && u.TenantId == SqlSugarConst.DefaultTenantId).ExecuteCommandAsync();
                return await svc.AddAsync(new AddSerialInput("test", ResetIntervalEnum.Day, formater, min, max));
            }

            /// <summary>直插他租户 test 行(D2 夹具;绕服务层以控制 TenantId)。</summary>
            public async Task<long> InsertOtherTenantRowAsync()
            {
                await Db.Deleteable<SysSerial>().Where(u => u.Type == "test" && u.TenantId == OtherTenantId).ExecuteCommandAsync();
                var id = YitIdHelper.NextId();
                await Db.Insertable(new SysSerial
                {
                    Id = id, Type = "test", ResetInterval = ResetIntervalEnum.Day, Formater = "{SEQ}",
                    Min = 1, Max = 9999, OrderNo = 100, Status = 0, Seq = 1, Expy = DateTime.Now, TenantId = OtherTenantId
                }).ExecuteCommandAsync();
                return id;
            }

            public async Task CleanupSerialAsync(long id)
            {
                await Db.Deleteable<SysSerial>().Where(u => u.Id == id).ExecuteCommandAsync();
            }

            public async Task DeleteRowAsync(long id)
            {
                await Db.Deleteable<SysSerial>().Where(u => u.Id == id).ExecuteCommandAsync();
            }

            public void Dispose() => _provider?.Dispose();

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

        private sealed class FakeHttpContextAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
