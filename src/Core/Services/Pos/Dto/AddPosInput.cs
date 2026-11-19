namespace AdminCore.Core.Services.Pos.Dto
{
    /// <summary>新建职位请求 {name, code, sort?, remark?, status?}(Code 服务层校验唯一,重复 → 业务码 400)。</summary>
    public sealed record AddPosInput(string? Name, string? Code, int Sort = 0, string? Remark = null, int Status = 0);
}
