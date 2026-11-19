namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>字典类型输出 {id, name, code, isEnum, isEnable, remark}。</summary>
    public sealed record DictTypeOutput(long Id, string? Name, string? Code, bool IsEnum, bool IsEnable, string? Remark);
}
