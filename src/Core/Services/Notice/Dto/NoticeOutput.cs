namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 通知公告行输出(分页/详情/未读/最新共用):实体全业务字段投影(枚举按数值序列化,数值域见 NoticeTypeEnum/NoticeStatusEnum)。
    /// </summary>
    public sealed record NoticeOutput(long Id, string Title, string Content, int Type,
        long PublicUserId, string? PublicUserName, long PublicOrgId, string? PublicOrgName,
        DateTime? PublicTime, DateTime? CancelTime, int Status, DateTime? CreateTime);
}
