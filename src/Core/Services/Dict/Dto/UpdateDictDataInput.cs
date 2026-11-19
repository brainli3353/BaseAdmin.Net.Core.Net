namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>
    /// 更新字典值请求 {id, code, value, label, tagType?, styleSetting?, classSetting?, orderNo?, isEnum?, extData?, remark?}。
    /// DictTypeId 不可变更(值跨类型迁移走删+建);Code 变更仍须满足同类型唯一。
    /// </summary>
    public sealed record UpdateDictDataInput(
        long Id,
        string? Code,
        string? Value,
        string? Label,
        int TagType = 1,
        string? StyleSetting = null,
        string? ClassSetting = null,
        int OrderNo = 0,
        bool IsEnum = false,
        string? ExtData = null,
        string? Remark = null);
}
