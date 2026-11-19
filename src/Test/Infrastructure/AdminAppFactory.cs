// 批 S-G B4b:入站契约冒烟夹具——进程内完整宿主(WebApplicationFactory + TestServer)。
// 背景:全仓此前 WebApplicationFactory/TestServer 0 命中,鉴权管线/模型绑定/400 双口径只能 /tmp 人肉 e2e;
// 本夹具让 HTTP 入站契约(路由/中间件/过滤器/序列化出口)进入回归。首次启动对 dev 库跑 CodeFirst/种子
// (幂等,dev 库既有惯例;集群锁 StartupSchemaInitializer 防多实例 DDL 并发);5 条冒烟全部匿名,
// 走中间件层判定,不依赖建表/种子就绪,也不依赖真实登录/OTP。
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AdminCore.Test.Infrastructure;

public sealed class AdminAppFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 进程环境变量注入 + 退出恢复。
    /// WHY 不能只靠 ConfigureAppConfiguration:Program 在 `builder.Build()` **之前**就读取
    /// ConnectionConfigs 装配 SqlSugar(见 SqlSugarSetup.LoadConnectionConfigs),而工厂的
    /// ConfigureAppConfiguration 回调要到宿主构建期才并入配置链 ⇒ 时序上晚于 SqlSugar 装配,
    /// 表现为"配置读出来是 127.0.0.1、SqlSugar 却仍连 appsettings 的 postgres"⇒ 启动降级 + /health 503。
    /// 环境变量则被 Program 在 LoadModuleConfiguration 之后**重新加入链尾**(G8 修复),优先级最高且时序早。
    /// </summary>
    private readonly Dictionary<string, string?> _previousEnvironment = new();

    public AdminAppFactory()
    {
        SetEnvironment("ConnectionConfigs__0__ConfigId", "main");
        SetEnvironment("ConnectionConfigs__0__ConnectionString", TestConnectionString);
        SetEnvironment("ConnectionStrings__Default", TestConnectionString);
        SetEnvironment("ConnectionStrings__Redis", string.Empty);
        SetEnvironment("Cache__Type", "Memory");
        SetEnvironment("Jwt__Secret", "huluyeye-test-only-jwt-secret-2026-change-me");
        SetEnvironment("Cryptogram__Key", "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");
        SetEnvironment("Logging__File__Enabled", "false");
    }

    private void SetEnvironment(string key, string value)
    {
        _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var (key, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// dev 主库直连(与既有测试域 BatchAB/BatchAC 等同串;127.0.0.1 实测可达 2026-10-26,
    /// 不走 appsettings 的 postgres 主机名——容器内 hosts 已漂移)。
    /// </summary>
    public const string TestConnectionString =
        TestPg.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production:避开 Development 的 ValidateScopes/ValidateOnBuild 宿主校验差异(与测试版部署环境同口径)。
        // 该环境下 UseExceptionHandler 只对非 /api、非 /hubs 路径挂兜底分支——冒烟用例全在 /api 面,无影响。
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // 工厂追加的配置源在链尾(高于 Program 内部重加的环境变量/命令行):盖住
            // appsettings 的漂移主机名、生产环境占位密钥(Database/JWT.Production.json)与 Redis 依赖。
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 主库双维度覆盖:存量单库回落键 + 多库连接 [0](SqlSugarSetup.LoadConnectionConfigs 优先吃 ConnectionConfigs)
                ["ConnectionStrings:Default"] = TestConnectionString,
                ["ConnectionConfigs:0:ConfigId"] = "main",
                ["ConnectionConfigs:0:ConnectionString"] = TestConnectionString,
                // 内存缓存避开 Redis 依赖;连接串显式置空(CacheSetup 视作缺失,防误连)
                ["Cache:Type"] = "Memory",
                ["ConnectionStrings:Redis"] = "",
                // JWT 测试占位密钥(同型 JWT.Development.json 但测试专属——其注释口径"不得带进测试",故另起)
                ["Jwt:Secret"] = "huluyeye-test-only-jwt-secret-2026-change-me",
                // 32 字节 Base64 占位(加解密服务启动要求;Base64 解码=0123456789abcdef0123456789abcdef)
                ["Cryptogram:Key"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                // 文件日志通道基础件 Enabled=true + 相对 logs——工厂下 ContentRoot=src/Web.Entry,
                // 不关会在源树写日志目录(污染工作区);测试面用控制台日志即可
                ["Logging:File:Enabled"] = "false",
            });
        });
    }
}
