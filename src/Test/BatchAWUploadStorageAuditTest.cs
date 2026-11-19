using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Extensions;
using AdminCore.Core.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminCore.Test;

/// <summary>
/// 批 AW「上传存储集群守卫」验证件:被测主体 =
/// ① <see cref="UploadStorageGuard"/> 判据纯函数与配置键常量;
/// ② <see cref="UploadStorageAudit"/> 的一次性判定入口(可脱离宿主直测);
/// ③ **DI 可发现性**——审计件必须真的被 AddAutoServices 的 HostedService 分支登记,否则等于没挂载(本批最易错的一点)。
/// </summary>
/// <remarks>
/// 零外部依赖(不需要 PG/Redis):判据是纯函数,DI 用例只建最小容器(AddLogging/IConfiguration/AddCache/AddAutoServices)。
/// 真机三态对照(集群声明×档位)见差距清单历史行与 /tmp/aw_e2e.sh。
/// </remarks>
public class BatchAWUploadStorageAuditTest
{
    // ── ① 判据真值表 ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 守卫判据_真值表四组合()
    {
        Assert.True(UploadStorageGuard.NeedsClusterStorageWarning(clusterDeclared: true, saveToDb: false));
        Assert.False(UploadStorageGuard.NeedsClusterStorageWarning(clusterDeclared: true, saveToDb: true));
        Assert.False(UploadStorageGuard.NeedsClusterStorageWarning(clusterDeclared: false, saveToDb: false));
        Assert.False(UploadStorageGuard.NeedsClusterStorageWarning(clusterDeclared: false, saveToDb: true));
    }

    // ── ② 档位描述文案(留痕日志的可读口径) ──────────────────────────────────────────────────
    [Fact]
    public void 档位描述_两档文案()
    {
        Assert.Equal("Local(wwwroot/uploads)", UploadStorageGuard.DescribeEffectiveStorage(saveToDb: false));
        Assert.Equal("Db(sys_file_content)", UploadStorageGuard.DescribeEffectiveStorage(saveToDb: true));
    }

    // ── ③ 审计判定入口(不起宿主):档位 + 是否告警 一并断言 ──────────────────────────────────
    [Fact]
    public void 审计判定_档位与告警()
    {
        Assert.Equal(("Local(wwwroot/uploads)", true), UploadStorageAudit.Evaluate(clusterDeclared: true, saveToDb: false));
        Assert.Equal(("Db(sys_file_content)", false), UploadStorageAudit.Evaluate(clusterDeclared: true, saveToDb: true));
        Assert.Equal(("Local(wwwroot/uploads)", false), UploadStorageAudit.Evaluate(clusterDeclared: false, saveToDb: false));
    }

    // ── ④ 配置键常量逐字(审计件与 SysFileService 必须读同一键,防漂移) ─────────────────────
    [Fact]
    public void 配置键常量_逐字()
    {
        Assert.Equal("Upload:EnableSaveDb", UploadStorageGuard.EnableSaveDbConfigKey);
        Assert.Equal("EventBus:EnableRedis", UploadStorageGuard.ClusterDeclaredConfigKey);
    }

    // ── ⑤ DI 可发现性:HostedService 分支真的登记了它(零改 Program.cs 的注册契约) ──────────
    [Fact]
    public void 注册_被AddAutoServices登记为HostedService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = TestPg.ConnectionStringNoTimeout,
            ["Yit:WorkerId"] = "1"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddCache(config);
        services.AddAutoServices(typeof(SysUser).Assembly);
        using var provider = services.BuildServiceProvider();

        // 主注册在场
        Assert.NotNull(provider.GetService<UploadStorageAudit>());

        // HostedService 分支在场(宿主动员时会 StartAsync ⇒ 审计才真的跑)——这是"零改 Program.cs"契约的判据
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is UploadStorageAudit);
    }

    // ── ⑥ 审计默认档位(未注册 IConfiguration 的最小容器)不抛 ───────────────────────────────
    [Fact]
    public async Task 审计_无配置最小容器_不抛且空转()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        var audit = new UploadStorageAudit(provider, provider.GetRequiredService<ILogger<UploadStorageAudit>>());
        await audit.StartAsync(CancellationToken.None);
        await audit.StopAsync(CancellationToken.None);
    }
}