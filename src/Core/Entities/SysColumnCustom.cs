using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统用户表格列配置(批 A 4.12,参照 Admin.NET SysColumnCustom 移植):
    /// 用户 × 表格(GridId=视图标识,如 "sys/user")× 四类列状态(冻结/列宽/顺序/显隐)。
    /// 状态数据 JSON 序列化落 text 大字段;业务键 (UserId, GridId) 唯一
    /// (参照无唯一索引——本仓加,防并发重复行,裁决留痕);无种子行。
    /// </summary>
    [SugarTable("sys_column_custom", "系统用户表格列配置表")]
    [IncreTable]
    [SugarIndex("i_{table}_ug", nameof(UserId), OrderByType.Asc, nameof(GridId), OrderByType.Asc, IsUnique = true)]
    public class SysColumnCustom : EntityTenant
    {
        /// <summary>用户Id。</summary>
        [SugarColumn(ColumnDescription = "用户Id")]
        public long UserId { get; set; }

        /// <summary>表格Id(视图标识,如 sys/user)。</summary>
        [SugarColumn(ColumnDescription = "表格Id", Length = 128)]
        public string GridId { get; set; } = string.Empty;

        /// <summary>冻结列状态数据(JSON: 列字段 → 固定位置)。</summary>
        [SugarColumn(ColumnDescription = "冻结列状态数据", ColumnDataType = "text", IsNullable = true)]
        public string? FixedData { get; set; }

        /// <summary>列宽状态数据(JSON: 列字段 → 像素宽)。</summary>
        [SugarColumn(ColumnDescription = "列宽状态数据", ColumnDataType = "text", IsNullable = true)]
        public string? ResizableData { get; set; }

        /// <summary>列顺序数据(JSON: 列字段 → 序号)。</summary>
        [SugarColumn(ColumnDescription = "列顺序数据", ColumnDataType = "text", IsNullable = true)]
        public string? SortData { get; set; }

        /// <summary>显示/隐藏列状态数据(JSON: 列字段 → 可见性)。</summary>
        [SugarColumn(ColumnDescription = "显示/隐藏列状态数据", ColumnDataType = "text", IsNullable = true)]
        public string? VisibleData { get; set; }
    }
}
