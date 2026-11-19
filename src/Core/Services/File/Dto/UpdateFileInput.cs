namespace AdminCore.Core.Services.File.Dto
{
    /// <summary>业务关联差集同步入参：dataId=业务主键，fileIds=业务方持有的该业务权威文件全集（null/空=该业务不持有任何文件）。</summary>
    public sealed record UpdateFileInput(long DataId, List<long>? FileIds);
}
