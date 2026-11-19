namespace AdminCore.Core.Services.ColumnCustom.Dto
{
    /// <summary>
    /// 表格列配置输出(批 A 4.12,参照 ColumnCustomOutput 移植):
    /// 反序列化后的四类状态字典(null=该用户该表无配置或该项未设置)。
    /// </summary>
    public class ColumnCustomOutput
    {
        /// <summary>用户Id。</summary>
        public long UserId { get; set; }

        /// <summary>表格Id。</summary>
        public string GridId { get; set; } = string.Empty;

        /// <summary>冻结列状态。</summary>
        public Dictionary<string, string>? FixedData { get; set; }

        /// <summary>列宽状态。</summary>
        public Dictionary<string, int>? ResizableData { get; set; }

        /// <summary>列顺序。</summary>
        public Dictionary<string, int>? SortData { get; set; }

        /// <summary>显示/隐藏。</summary>
        public Dictionary<string, bool>? VisibleData { get; set; }
    }
}
