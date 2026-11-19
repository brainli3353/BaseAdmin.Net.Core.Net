namespace AdminCore.Core.Option;

/// <summary>
/// JWT 配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Jwt</c>。
/// 消费方经 <c>IOptions&lt;JwtOptions&gt;</c>(AddProjectOptions 集中绑定);缺省回落见 JwtOptionsPostConfigure。
/// </summary>
public sealed class JwtOptions
{
    /// <summary>HS256 对称密钥(须 ≥32 字节;生产环境变量注入)。</summary>
    public string? Secret { get; set; }

    /// <summary>签发者(校验参数 ValidIssuer)。</summary>
    public string? Issuer { get; set; }

    /// <summary>受众(校验参数 ValidAudience)。</summary>
    public string? Audience { get; set; }

    /// <summary>access token 时效(小时);&lt;=0 回落 8(缺省口径)。</summary>
    public int ExpireHours { get; set; }

    /// <summary>refresh token 时效(小时,批 N 6.4);&lt;=0 回落 168(7 天,不关闭)。</summary>
    public int RefreshTokenExpireHours { get; set; }
}

