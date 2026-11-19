using System.Text.Json;
using AdminCore.Core.Extensions;
using AdminCore.Core.Models;
using AdminCore.Core.Option;
using AdminCore.Web.Entry.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdminCore.Test;

/// <summary>
/// 批 AY(G24 站点身份配置化)测试:纯 DI,无 DB、无 HTTP。
/// 判据核心——**App:Name 是唯一真源**:它同时决定 ① 前端拿到的 name/fullName ② OTP otpauth issuer,
/// 且显式配置(含 env Otp__Issuer)优先、缺省回落链不产生空值/空 URI。
/// </summary>
public class BatchAYAppInfoTest
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddProjectOptions(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AY1_全名缺省回落应用名_后缀留空交前端兜底()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["App:Name"] = "site-one" });
        var app = provider.GetRequiredService<IOptions<AppOptions>>().Value;

        Assert.Equal("site-one", app.Name);
        // FullName 空 ⇒ 回落 Name(不留半截空全名)
        Assert.Equal("site-one", app.FullName);
        // TitleSuffix 未配置 ⇒ 保持 null,由前端构建期兜底(不硬编码进后端)
        Assert.Null(app.TitleSuffix);
    }

    [Fact]
    public void AY2_显式全名与后缀优先于回落()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["App:Name"] = "site-two",
            ["App:FullName"] = "第二站点全名",
            ["App:TitleSuffix"] = "控制台"
        });
        var app = provider.GetRequiredService<IOptions<AppOptions>>().Value;

        Assert.Equal("site-two", app.Name);
        Assert.Equal("第二站点全名", app.FullName);
        Assert.Equal("控制台", app.TitleSuffix);
    }

    [Fact]
    public void AY3_OTP_issuer回落应用名_显式优先_双空不造空URI()
    {
        // ① 未配 Otp:Issuer ⇒ 回落 App:Name(多平台"分不清哪个 OTP"的收口;改 App__Name 即改 issuer)
        using (var p1 = BuildProvider(new Dictionary<string, string?> { ["App:Name"] = "issuer-from-app" }))
        {
            Assert.Equal("issuer-from-app", p1.GetRequiredService<IOptions<OtpOptions>>().Value.Issuer);
        }

        // ② 显式 Otp:Issuer(含 env Otp__Issuer)优先 ⇒ 向后兼容既有部署
        using (var p2 = BuildProvider(new Dictionary<string, string?>
        {
            ["App:Name"] = "app-name-x",
            ["Otp:Issuer"] = "explicit-issuer"
        }))
        {
            Assert.Equal("explicit-issuer", p2.GetRequiredService<IOptions<OtpOptions>>().Value.Issuer);
        }

        // ③ 两处都空 ⇒ 留 null(AuthService.BuildOtpauthUri 既有兜底文案收尾,绝不产生空 issuer 的断 URI)
        using (var p3 = BuildProvider(new Dictionary<string, string?>()))
        {
            var app = p3.GetRequiredService<IOptions<AppOptions>>().Value;
            Assert.True(string.IsNullOrWhiteSpace(app.Name));
            Assert.True(string.IsNullOrWhiteSpace(p3.GetRequiredService<IOptions<OtpOptions>>().Value.Issuer));
        }
    }

    [Fact]
    public void AY4_app_info端点契约_camelCase三键且值来自配置()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["App:Name"] = "site-api",
            ["App:TitleSuffix"] = "管理台"
        });
        var controller = new AppController(provider.GetRequiredService<IOptions<AppOptions>>());

        var ok = Assert.IsType<OkObjectResult>(controller.Info());
        var payload = JsonSerializer.Serialize(ok.Value, ok.Value!.GetType(), ApiResult.Json);
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        // 与前端 api/app.ts 消费契约逐字对齐(前端按 camelCase 解包)
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        Assert.Equal("site-api", data.GetProperty("name").GetString());
        // FullName 未配 ⇒ 契约里是已回落的 Name(前端无需再兜)
        Assert.Equal("site-api", data.GetProperty("fullName").GetString());
        Assert.Equal("管理台", data.GetProperty("titleSuffix").GetString());
    }

    [Fact]
    public void AY5_未配置TitleSuffix时端点输出null_前端保留兜底()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["App:Name"] = "site-null-suffix" });
        var controller = new AppController(provider.GetRequiredService<IOptions<AppOptions>>());

        var ok = Assert.IsType<OkObjectResult>(controller.Info());
        var payload = JsonSerializer.Serialize(ok.Value, ok.Value!.GetType(), ApiResult.Json);
        using var doc = JsonDocument.Parse(payload);

        var suffix = doc.RootElement.GetProperty("data").GetProperty("titleSuffix");
        Assert.Equal(JsonValueKind.Null, suffix.ValueKind);
        Assert.Equal("site-null-suffix", doc.RootElement.GetProperty("data").GetProperty("fullName").GetString());
    }
}