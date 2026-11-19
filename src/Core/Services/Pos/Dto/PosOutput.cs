namespace AdminCore.Core.Services.Pos.Dto
{
    /// <summary>职位分页行输出 {id, name, code, sort, remark, status}(全字段业务可见,无敏感字段)。</summary>
    public sealed record PosOutput(long Id, string? Name, string? Code, int Sort, string? Remark, int Status);
}
