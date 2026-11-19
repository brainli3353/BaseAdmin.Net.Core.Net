namespace AdminCore.Core.Services.Print.Dto
{
    /// <summary>打印模板详情输出(全字段:含 Template 全文)。</summary>
    public sealed record SysPrintDetailOutput(long Id, string Name, string Template, int PrintType, int Status, int OrderNo, string? Remark, DateTime? CreateTime);
}
