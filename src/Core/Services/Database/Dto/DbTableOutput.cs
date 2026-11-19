using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Database.Dto
{
    /// <summary>
    /// 数据库表清单行输出(3.8 只读):表名/表注释/行数估算/占用大小/索引数。
    /// </summary>
    /// <remarks>
    /// WHY class 而非 positional record:本 DTO 由 SqlSugar 原生 SQL(<c>Ado.SqlQueryAsync&lt;T&gt;</c>)直接映射,
    /// 而 SqlSugar 5.1.4.217 不映射 positional record 的构造参数(批 E 实证)——需可写属性。
    /// 行数估算口径:取 <c>pg_stat_user_tables.n_live_tup</c>(统计信息估算,非精确 count——
    /// 全表 count 在大表上代价过高;本页定位=运维概览)。
    /// </remarks>
    public class DbTableOutput
    {
        /// <summary>表名(当前 schema 内)。</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>表注释(PG COMMENT;无注释为 null)。</summary>
        public string? Comment { get; set; }

        /// <summary>行数估算(统计信息;新表/未 analyze 可能为 0,不代表空表)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long RowEstimate { get; set; }

        /// <summary>占用大小(人类可读,含索引与 TOAST;<c>pg_size_pretty</c> 输出,如 "1616 kB")。</summary>
        public string TotalSize { get; set; } = string.Empty;

        /// <summary>索引数量。</summary>
        public int IndexCount { get; set; }
    }
}