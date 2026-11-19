using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 通知公告类型(批 G 4.7;参照仓值域):1 通知 / 2 公告。
    /// </summary>
    [Description("通知公告类型")]
    public enum NoticeTypeEnum
    {
        /// <summary>通知。</summary>
        [Description("通知")]
        Notice = 1,

        /// <summary>公告。</summary>
        [Description("公告")]
        Announcement = 2,

        /// <summary>
        /// 系统升级(批 AF 4.11:升级公告**以本类型承载**,不另建升级日志表——复用决策见框架使用指南 §35)。
        /// 该类型参与"登录后未读提示"受配置开关 <c>sys_upgrade</c> 控制(关=不提示,其余类型照常)。
        /// </summary>
        [Description("系统升级")]
        Upgrade = 3
    }
}
