namespace AdminCore.Core.Services.Database.Dto
{
    /// <summary>
    /// 数据库列结构行输出(3.8 只读):列名/数据类型/可空/字符长度/主键/注释/序位。
    /// </summary>
    /// <remarks>WHY class:SqlSugar 原生 SQL 映射需可写属性(见 DbTableOutput remarks)。</remarks>
    public class DbColumnOutput
    {
        /// <summary>列名(本仓约定:主键列小写 id,其余按实体属性 PascalCase)。</summary>
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>数据类型(PG 完整类型名,如 "character varying" / "bigint" / "timestamp without time zone")。</summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>是否可空。</summary>
        public bool IsNullable { get; set; }

        /// <summary>字符型最大长度(varchar/char;其余类型为 null)。</summary>
        public int? MaxLength { get; set; }

        /// <summary>是否主键列(复合主键时多列均为 true)。</summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>列注释(PG COMMENT;无注释为 null)。</summary>
        public string? Comment { get; set; }

        /// <summary>列序位(从 1 开始,与实体声明顺序一致)。</summary>
        public int OrdinalPosition { get; set; }
    }
}