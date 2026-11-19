namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>更新字典类型请求 {id, name, isEnum?, remark?}(Code 不可变更:缓存键与业务引用锚定 Code,变更走新建)。</summary>
    public sealed record UpdateDictTypeInput(long Id, string? Name, bool IsEnum = false, string? Remark = null);
}
