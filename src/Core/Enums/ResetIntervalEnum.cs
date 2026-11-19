using System.ComponentModel;

namespace AdminCore.Core.Enums
{
    /// <summary>
    /// 流水号重置间隔(批 W 4.3):按周期把序列号重置回 Min。
    /// Day = 跨自然日重置(最常用:单号每日从零计);Month = 跨自然月;Year = 跨自然年;Never = 单调递增不重置。
    /// </summary>
    [Description("流水号重置间隔")]
    public enum ResetIntervalEnum
    {
        /// <summary>永不重置(单调递增)。</summary>
        [Description("永不重置")]
        Never = 0,

        /// <summary>按日重置。</summary>
        [Description("按日")]
        Day = 1,

        /// <summary>按月重置。</summary>
        [Description("按月")]
        Month = 2,

        /// <summary>按年重置。</summary>
        [Description("按年")]
        Year = 3
    }
}
