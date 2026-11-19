namespace AdminCore.Core.Services.Database.Dto
{
    /// <summary>
    /// 数据库索引行输出(3.8 只读):索引名/定义/唯一/主键。
    /// </summary>
    /// <remarks>WHY class:SqlSugar 原生 SQL 映射需可写属性(见 DbTableOutput remarks)。</remarks>
    public class DbIndexOutput
    {
        /// <summary>索引名(PG 内唯一)。</summary>
        public string IndexName { get; set; } = string.Empty;

        /// <summary>索引定义(CREATE INDEX 原文,<c>pg_get_indexdef</c> 输出)。</summary>
        public string Definition { get; set; } = string.Empty;

        /// <summary>是否唯一索引。</summary>
        public bool IsUnique { get; set; }

        /// <summary>是否主键索引。</summary>
        public bool IsPrimary { get; set; }
    }
}