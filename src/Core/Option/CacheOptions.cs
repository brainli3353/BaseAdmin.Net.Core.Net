namespace AdminCore.Core.Option;

/// <summary>
/// 缓存配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Cache</c>。
/// 消费方经 <c>IOptions&lt;CacheOptions&gt;</c>;既有散读点(AddCache)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class CacheOptions
{
    /// <summary>缓存实现:Redis(默认) / Memory;显式配置决定实现,不做运行期切换。</summary>
    public string? Type { get; set; }
}
