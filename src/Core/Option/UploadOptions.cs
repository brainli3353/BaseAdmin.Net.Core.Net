namespace AdminCore.Core.Option;

/// <summary>
/// 上传配置选项(批 Q 6.8,节名=类名约定):配置节 <c>Upload</c>。
/// 消费方经 <c>IOptions&lt;UploadOptions&gt;</c>;既有散读点(SysFileService)保留 IConfiguration 直读(#8)。
/// </summary>
public sealed class UploadOptions
{
    /// <summary>客户端声明 MIME 白名单(忽略大小写精确匹配);缺省空集。</summary>
    public List<string> ContentType { get; set; } = [];

    /// <summary>文件后缀白名单(不含点);缺省空集。</summary>
    public List<string> AllowSuffix { get; set; } = [];
}
