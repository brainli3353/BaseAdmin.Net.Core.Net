namespace AdminCore.Core.Extensions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AdminCore.Core.Option;

/// <summary>
/// IOptions 强类型配置集中绑定(批 Q 6.8,手册 §6.8 原生版口径):
/// <c>services.Configure&lt;T&gt;(config.GetSection("节名"))</c> × 本仓实际 9 节,节名=类名(去 Options 后缀)约定。
/// 参照 Admin.NET AddConfigurableOptions/Furion 反射自动绑定的原生替代(裁决 g11 §5 ③:规模手写显式,零反射)。
/// </summary>
public static class ProjectOptionsExtensions
{
    /// <summary>集中绑定本仓 9 个配置节 Option(须在消费方解析前调用;PostConfigure 缺省回落随 Configure 自动注册)。</summary>
    public static IServiceCollection AddProjectOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AppOptions>(configuration.GetSection("App"));
        services.PostConfigure<AppOptions>(o =>
        {
            // 全名空 → 回落应用名(批 AY):下游只配 App:Name 也能拿到完整身份,不留半截空全名
            if (string.IsNullOrWhiteSpace(o.FullName)) o.FullName = o.Name;
        });
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.PostConfigure<JwtOptions>(o =>
        {
            // 缺省回落(与 AuthService 消费点既有口径一致——双保险:Option 层落默认值,消费点缺省逻辑保留)
            if (o.ExpireHours <= 0) o.ExpireHours = 8;
            if (o.RefreshTokenExpireHours <= 0) o.RefreshTokenExpireHours = 168;
        });
        services.Configure<DbSettingsOptions>(configuration.GetSection("DbSettings"));
        services.Configure<StartupSeedOptions>(configuration.GetSection("StartupSeed"));
        services.Configure<CacheOptions>(configuration.GetSection("Cache"));
        services.Configure<YitOptions>(configuration.GetSection("Yit"));
        services.Configure<SysPasswordOptions>(configuration.GetSection("SysPassword"));
        services.Configure<OtpOptions>(configuration.GetSection("Otp"));
        services.PostConfigure<OtpOptions>(o =>
        {
            // issuer 空 → 回落 App:Name(批 AY,G24):多套独立服务"分不清哪个平台对应哪个 OTP"的收口。
            // 显式 Otp:Issuer(含 env Otp__Issuer)优先,行为向后兼容;App:Name 也空则留空,
            // 由 AuthService.BuildOtpauthUri 既有兜底文案收尾(双保险,防 URI 断裂)。
            if (string.IsNullOrWhiteSpace(o.Issuer)) o.Issuer = configuration["App:Name"];
        });
        services.Configure<CryptogramOptions>(configuration.GetSection("Cryptogram"));
        services.Configure<UploadOptions>(configuration.GetSection("Upload"));
        return services;
    }
}
