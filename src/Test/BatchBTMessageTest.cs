using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Job;
using AdminCore.Core.Repository;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;


namespace AdminCore.Test;

/// <summary>
/// 批 BT「sysMessage 站内消息菜单复审」测试:被测主体 = 消息日志表 <c>sys_log_msg</c> 的清理面
/// (CleanSysLogJob 覆盖本仓日志类表全集)。
/// </summary>
/// <remarks>
/// WHY 只测清理面:本批唯一缺陷 D1＝sys_log_msg(批 G 落库面,早于批 AD 清理收口存在)未纳入
/// CleanSysLogJob——与本仓批 AD 已裁决的"日志表无界增长"缺陷族同族(ex/http 已补, msg 漏纳),
/// 属"本仓内部口径分裂"(参照亦无 msg 清理,但本仓批 AD 已否决"参照缺清理=可接受"口径)。
/// 断言口径同 JobSchedulerTest 既有清理用例:保留期 1 天 → CreateTime 早于 2 天前的行物理删除,期内行保留。
/// </remarks>
public class BatchBTMessageTest
{
    // 开发库连接串(与 JobSchedulerTest 同源;Timeout=10 防沙箱 PG 黑洞挂死)。
    private const string DbConnectionString =
        TestPg.ConnectionString;

    /// <summary>PG 可达性探测(TCP 直连,不依赖 ORM;同 JobSchedulerTest 口径)。</summary>
    private static bool IsPostgresReachable()
    {
        try
        {
            var conn = DbConnectionString;
            var parts = conn.Split(';');
            var host = parts[0].Split('=')[1];
            var port = int.Parse(parts[1].Split('=')[1]);
            using var tcp = new System.Net.Sockets.TcpClient();
            var ar = tcp.BeginConnect(host, port, null, null);
            return ar.AsyncWaitHandle.WaitOne(3000) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// D1 防复发:sys_log_msg 纳入清理全集——保留期 1 天 → 2 天前的消息日志行物理删除(行消失,非软删),
    /// 期内行保留(同既有 7 表口径;CreateTime 由 AOP 填充,过期行显式回置)。
    /// </summary>
    [Fact]
    public async Task D1_消息日志表纳入清理_过期行物理删除期内保留()
    {
        if (!IsPostgresReachable())
        {
            Assert.Fail("PostgreSQL 不可达,跳过消息日志清理用例(同 RepositoryTest 口径)");
        }

        var vars = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = DbConnectionString, ["SysJob:LogRetentionDays"] = "1" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config); // 裸容器须显式 <IConfiguration>(IConfigurationRoot 键不匹配)
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSqlSugar(config);
        services.AddCache(config);
        services.AddSignalR();
        services.AddScoped<CleanSysLogJob>();
        using var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<ISqlSugarClient>();
        db.InitTables(typeof(SqlSugarSetup).Assembly); // 扫 Core 程序集(含 sys_log_msg,增量幂等)

        var marker = $"test_bt_msglog_{Guid.NewGuid():N}";
        using (var scope = provider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysLogMsg>>();
            var expired = new SysLogMsg
            {
                MessageType = "Info",
                Title = marker,
                Message = marker + "_expired",
                ReceiveUserId = 1,
                SendUserId = 2,
                SendTime = DateTime.Now.AddDays(-2),
                CreateTime = DateTime.Now.AddDays(-2) // AOP 不覆盖非空 CreateTime
            };
            var recent = new SysLogMsg
            {
                MessageType = "Info",
                Title = marker,
                Message = marker + "_recent",
                ReceiveUserId = 1,
                SendUserId = 2
            };
            await repo.InsertAsync(expired);
            await repo.InsertAsync(recent);

            var job = scope.ServiceProvider.GetRequiredService<CleanSysLogJob>();
            await job.ExecuteAsync(new JobContext("clean_sys_log", "test", DateTime.Now), CancellationToken.None);

            var expiredAfter = await repo.GetFirstAsync(u => u.Message == marker + "_expired");
            var recentAfter = await repo.GetFirstAsync(u => u.Message == marker + "_recent");

            Assert.True(expiredAfter is null, $"D1:过期的 sys_log_msg 行应被物理删除(纳入清理全集): {expiredAfter?.Message}");
            Assert.True(recentAfter is not null, "期内 sys_log_msg 行应保留(保留期语义)");

            // 探针清理(物理删,防跨用例串扰)
            await repo.DeletePhysicalByAsync(u => u.Title == marker);
        }
    }
}
