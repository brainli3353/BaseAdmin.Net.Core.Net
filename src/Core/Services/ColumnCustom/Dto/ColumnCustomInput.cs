namespace AdminCore.Core.Services.ColumnCustom.Dto
{
    /// <summary>
    /// 表格列配置保存入参(批 A 4.12,参照 StoreColumnCustomInput 移植)。
    /// 四类状态数据均为可选字典,前端 vxe-grid 设置态直接透传;null=不设置该项。
    /// </summary>
    public class StoreColumnCustomInput
    {
        /// <summary>表格Id(视图标识,如 sys/user)。</summary>
        public string GridId { get; set; } = string.Empty;

        /// <summary>冻结列状态(列字段 → 固定位置)。</summary>
        public Dictionary<string, string>? FixedData { get; set; }

        /// <summary>列宽状态(列字段 → 像素宽)。</summary>
        public Dictionary<string, int>? ResizableData { get; set; }

        /// <summary>列顺序(列字段 → 序号)。</summary>
        public Dictionary<string, int>? SortData { get; set; }

        /// <summary>显示/隐藏(列字段 → 可见性)。</summary>
        public Dictionary<string, bool>? VisibleData { get; set; }
    }

    /// <summary>
    /// 表格列配置查询/重置入参(批 A 4.12):仅表格Id。
    /// </summary>
    public class GetColumnCustomInput
    {
        /// <summary>表格Id(视图标识)。</summary>
        public string GridId { get; set; } = string.Empty;
    }
}
