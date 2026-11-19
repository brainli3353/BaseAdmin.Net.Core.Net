namespace AdminCore.Core.Services.Dict.Dto
{
    /// <summary>
    /// 字典值输出 {id, dictTypeId, code, value, label, tagType, styleSetting, classSetting, orderNo, isEnum, isEnable, extData, remark}。
    /// 亦为缓存对象(sys_dict:{code} 键存的即 List&lt;DictDataOutput&gt;,DTO 落缓存而非实体——缓存形态与前端契约稳定,不受审计列演进影响)。
    /// </summary>
    public sealed record DictDataOutput(
        long Id,
        long DictTypeId,
        string? Code,
        string? Value,
        string? Label,
        int TagType,
        string? StyleSetting,
        string? ClassSetting,
        int OrderNo,
        bool IsEnum,
        bool IsEnable,
        string? ExtData,
        string? Remark);
}
