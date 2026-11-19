using System.ComponentModel;

namespace AdminCore.Core.EventBus
{
    /// <summary>
    /// 事件执行状态(批 S 5.8,参照 Admin.NET EventStatusEnum 同值):SysLogEvent 审计行的状态机。
    /// 语义:Running=开始执行;Success=最终成功;Retry=某次尝试失败仍在重试;Error=重试耗尽最终失败。
    /// </summary>
    [Description("事件执行状态")]
    public enum EventStatusEnum
    {
        /// <summary>执行中</summary>
        [Description("执行中")]
        Running = 1,

        /// <summary>成功</summary>
        [Description("成功")]
        Success = 2,

        /// <summary>重试中(某次尝试失败,未耗尽)</summary>
        [Description("重试中")]
        Retry = 3,

        /// <summary>失败(重试耗尽)</summary>
        [Description("失败")]
        Error = 4
    }
}
