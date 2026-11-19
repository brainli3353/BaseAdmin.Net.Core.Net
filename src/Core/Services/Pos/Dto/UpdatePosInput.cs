namespace AdminCore.Core.Services.Pos.Dto
{
    /// <summary>更新职位请求 {id, name, code, sort?, remark?, status?}(Code 唯一性校验排除自身,变更 Code 防与他行撞码)。</summary>
    public sealed record UpdatePosInput(long Id, string? Name, string? Code, int Sort = 0, string? Remark = null, int Status = 0);
}
