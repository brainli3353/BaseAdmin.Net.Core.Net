namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 通知公告发布请求 {id, userIds?}:userIds 空 = 通知所有租户用户(参照口径);
    /// 重发幂等(先物理删旧接收行再按接收人集合重建),发布后广播在线用户。
    /// </summary>
    public sealed record PublicNoticeInput(long Id, List<long>? UserIds);
}
