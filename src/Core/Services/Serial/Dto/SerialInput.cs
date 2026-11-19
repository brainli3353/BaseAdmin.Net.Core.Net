using AdminCore.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace AdminCore.Core.Services.Serial.Dto
{
    /// <summary>流水号分页请求 {pageIndex, pageSize, type?, status?, keyword?, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), startDate?, endDate?}(Type 精确/Status 精确/Keyword 模糊 Type+Formater)。
    /// 批 UX B1d 时间范围:按 CreateTime 过滤(配置行创建时刻)。</summary>
    public sealed record PageSerialInput(int PageIndex = 1, int PageSize = 10, string? Type = null, int? Status = null, string? Keyword = null, string? OrderBy = null, bool? Desc = null, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput;

    /// <summary>
    /// 流水号新增请求 {type, resetInterval, formater, min, max, orderNo?, status?, remark?}。
    /// Formater 须含 {SEQ} 插槽(渲染核心);Min/Max 为序列上下限(重置落点/生成上限)。
    /// 校验特性直接挂构造参数(.NET 10 record 口径:挂属性会被忽略并抛异常,见 DemoValidateInput 实证注释)。
    /// </summary>
    public sealed record AddSerialInput(
        [Required] string Type,
        ResetIntervalEnum ResetInterval,
        [Required, RegularExpression(@".*?\{SEQ\}.*?", ErrorMessage = "表达式必须包含插槽 {SEQ}")] string Formater,
        long Min = 1,
        long Max = 1_000_000_000,
        int OrderNo = 100,
        int Status = 0,
        string? Remark = null);

    /// <summary>
    /// 流水号更新请求(只改配置列;Seq/Expy 运行时状态不接受修改——只经取号推进)。
    /// </summary>
    public sealed record UpdateSerialInput(
        [Required] long Id,
        [Required] string Type,
        ResetIntervalEnum ResetInterval,
        [Required, RegularExpression(@".*?\{SEQ\}.*?", ErrorMessage = "表达式必须包含插槽 {SEQ}")] string Formater,
        long Min = 1,
        long Max = 1_000_000_000,
        int OrderNo = 100,
        int Status = 0,
        string? Remark = null);

    /// <summary>取号请求 {type}(nextSeqNo=本租户/globalNextSeqNo=全局)。</summary>
    public sealed record GetNextSeqInput([Required] string Type);

    /// <summary>预览请求 {formater, seq, max}(不消耗序列,渲染即时看效果)。</summary>
    public sealed record PreviewSerialInput(
        [Required] string Formater,
        long Seq = 1,
        long Max = 1_000_000_000);
}
