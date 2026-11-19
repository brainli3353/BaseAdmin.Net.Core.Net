namespace AdminCore.Core.Services.File.Dto
{
    /// <summary>下载输出（控制器据此构造 FileContentResult）：内容字节 + 响应 Content-Type（按规范化后缀映射）+ 原始文件名（下载命名）。</summary>
    public sealed record DownloadFileOutput(byte[] Content, string ContentType, string? FileName);
}
