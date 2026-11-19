namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>更新字典值状态请求 {id, isEnable}(停用后该值对缓存读取立即不可见,写路径主动失效缓存)。</summary>
    public sealed record UpdateDictDataStatusInput(long Id, bool IsEnable);
}
