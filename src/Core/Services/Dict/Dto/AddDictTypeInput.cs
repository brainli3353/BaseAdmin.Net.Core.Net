namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>新建字典类型请求 {name, code, isEnum?, remark?}(Code 服务层校验唯一,重复 → 业务码 400)。</summary>
    public sealed record AddDictTypeInput(string? Name, string? Code, bool IsEnum = false, string? Remark = null);
}
