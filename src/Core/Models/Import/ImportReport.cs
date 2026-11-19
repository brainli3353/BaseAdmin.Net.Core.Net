using System.Collections.Generic;

namespace AdminCore.Core.Models.Import
{
    /// <summary>
    /// 导入结果报告(批 CP 契约冻结):行级错误经统一响应 data 回传(HTTP 200 + code=0),不是异常。
    /// </summary>
    /// <remarks>
    /// 全回滚口径:只要 <see cref="Errors"/> 非空则 <see cref="Imported"/> 恒为 0(任一行级错误 → 0 行入库);
    /// <see cref="Errors"/> 回传上限 100 条,超出时 <see cref="Truncated"/> 置 true(其余错误已收集但未回传)。
    /// </remarks>
    public sealed class ImportReport
    {
        /// <summary>参与校验的数据行总数(不含表头)。</summary>
        public int Total { get; set; }

        /// <summary>实际入库行数(全回滚口径下:有任一错误则为 0,否则等于 <see cref="Total"/>)。</summary>
        public int Imported { get; set; }

        /// <summary>行级错误明细(已截断至上限 100 条)。</summary>
        public List<ImportRowError> Errors { get; set; } = new List<ImportRowError>();

        /// <summary>错误是否被截断(收集到的错误超过回传上限)。</summary>
        public bool Truncated { get; set; }
    }
}