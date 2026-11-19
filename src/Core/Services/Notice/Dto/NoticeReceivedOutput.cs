namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 接收的通知公告分页行输出 {id(通知公告 Id), title, type, content, status, readStatus, readTime}:
    /// 接收记录(SysNoticeUser)与通知(SysNotice)按 NoticeId 关联的展开行(LeftJoin 手动映射,不用导航属性)。
    /// </summary>
    public sealed record NoticeReceivedOutput(long Id, string Title, int Type, string Content, int Status,
        int ReadStatus, DateTime? ReadTime);
}
