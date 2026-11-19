namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>
    /// 新建字典值请求 {dictTypeId, code, value, label, tagType?, styleSetting?, classSetting?, orderNo?, isEnum?, extData?, remark?}。
    /// Code 服务层校验同类型下唯一,重复 → 业务码 400。
    /// </summary>
    public sealed record AddDictDataInput(
        long DictTypeId,
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
