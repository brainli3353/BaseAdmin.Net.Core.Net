namespace AdminCore.Core.Models.Import
{
    /// <summary>
    /// 导入行级错误(批 CP 契约冻结):一行一条,回传前端定位到具体行与字段。
    /// </summary>
    /// <param name="Row">数据行号(源文件物理行号,表头为第 1 行,首条数据从 2 起)。</param>
    /// <param name="Field">出错字段名(未标注列/整行级错误时为 null)。</param>
    /// <param name="Reason">人读原因(行级错误不是异常,不参与错误码体系)。</param>
    public sealed record ImportRowError(int Row, string? Field, string Reason);
}