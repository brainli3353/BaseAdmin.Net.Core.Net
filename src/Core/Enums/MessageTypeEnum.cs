using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 站内消息类型(批 G 4.8;参照仓值域):0 消息 / 1 成功 / 2 警告 / 3 错误。
    /// </summary>
    [Description("站内消息类型")]
    public enum MessageTypeEnum
    {
        /// <summary>普通消息。</summary>
        [Description("普通消息")]
        Info = 0,

        /// <summary>成功提示。</summary>
        [Description("成功")]
        Success = 1,

        /// <summary>警告提示。</summary>
        [Description("警告")]
        Warning = 2,

        /// <summary>错误提示。</summary>
        [Description("错误")]
        Error = 3
    }
}
