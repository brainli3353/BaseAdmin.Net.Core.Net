using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 通知公告状态(批 G 4.7;参照仓值域):0 草稿 / 1 发布 / 2 撤回 / 3 删除。
    /// </summary>
    [Description("通知公告状态")]
    public enum NoticeStatusEnum
    {
        /// <summary>草稿(初始态)。</summary>
        [Description("草稿")]
        Draft = 0,

        /// <summary>发布(已推送接收人,禁删)。</summary>
        [Description("已发布")]
        Public = 1,

        /// <summary>撤回。</summary>
        [Description("已撤回")]
        Cancel = 2,

        /// <summary>删除(逻辑终态)。</summary>
        [Description("已删除")]
        Deleted = 3
    }
}
