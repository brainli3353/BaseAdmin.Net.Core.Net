namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 通知公告更新请求 {id, title?, content?, type?}(部分更新,约定 #9 增量更新;仅发布人本人可改)。
    /// </summary>
    public sealed record UpdateNoticeInput(long Id, string? Title, string? Content, int? Type);
}
