using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 通知公告用户阅读状态(批 G 4.7;参照仓值域):0 未读 / 1 已读。
    /// </summary>
    [Description("通知公告阅读状态")]
    public enum NoticeUserStatusEnum
    {
        /// <summary>未读(初始态)。</summary>
        [Description("未读")]
        Unread = 0,

        /// <summary>已读。</summary>
        [Description("已读")]
        Read = 1
    }
}
