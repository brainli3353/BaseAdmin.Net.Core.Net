using AdminCore.Core.Attributes;
using AdminCore.Core.Enums;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统通知公告用户接收记录(批 G 4.7;参照仓 SysNoticeUser 移植):
    /// 发布时按接收人集合生成(一人一行,先物理删旧行再批量插——重发幂等);阅读态(ReadStatus/ReadTime)独立维护。
    /// 中间表语义:继承 EntityBase(主键/审计/软删,对齐本仓 SysUserRole 口径);
    /// 级联清理走物理删(中间行无审计价值,软删残留会让重发留下不可见脏行——仓储 DeletePhysicalByAsync 口径)。
    /// </summary>
    [SugarTable("sys_notice_user", "系统通知公告用户表")]
    // WHY 索引:高频查询列——SysNoticeService 接收人视角按 UserId 查未读/阅读记录(u.UserId==userId)
    [SugarIndex("i_sys_notice_user_userid", nameof(UserId), OrderByType.Asc)]
    [IncreTable]
    public class SysNoticeUser : EntityTenant
    {
        /// <summary>通知公告 Id。</summary>
        [SugarColumn(ColumnDescription = "通知公告Id")]
        public long NoticeId { get; set; }

        /// <summary>接收用户 Id。</summary>
        [SugarColumn(ColumnDescription = "用户Id")]
        public long UserId { get; set; }

        /// <summary>阅读时间(SetRead 置值)。</summary>
        [SugarColumn(ColumnDescription = "阅读时间", IsNullable = true)]
        public DateTime? ReadTime { get; set; }

        /// <summary>阅读状态(0 未读 1 已读,默认未读)。</summary>
        [SugarColumn(ColumnDescription = "状态（0未读 1已读）", DefaultValue = "0")]
        public NoticeUserStatusEnum ReadStatus { get; set; } = NoticeUserStatusEnum.Unread;
    }
}
