using AdminCore.Core.Attributes;

namespace AdminCore.Core.Services.Pos.Dto
{
    /// <summary>
    /// 职位导入行 DTO(批 CP):四列对齐 SysPos 实体——Name≤64 必填、Code≤64 必填且唯一、Sort 默认 0、Remark≤256 可空。
    /// </summary>
    /// <remarks>
    /// WHY Sort 用 string 而非 int:导入的"排序非数字"必须是**行级错误**(报告第 N 行第几列),不能是抛异常;
    /// 若此处为 int,MiniExcel/转换层会把非法值吞成 0,校验层再也看不到原文。
    /// 保留原文由 <c>ImportService.Validate</c> 判定,入库前再 Int 化(Sort 空 = 默认 0)。
    /// </remarks>
    public sealed class ImportPosInput
    {
        /// <summary>职位名称(必填,≤64)。</summary>
        [ImportColumn("职位名称", Order = 1)]
        public string? Name { get; set; }

        /// <summary>职位编码(必填,≤64,全表唯一 + 文件内唯一)。</summary>
        [ImportColumn("职位编码", Order = 2)]
        public string? Code { get; set; }

        /// <summary>排序号(可空,空即默认 0;非整数为行级错误)。</summary>
        [ImportColumn("排序", Order = 3)]
        public string? Sort { get; set; }

        /// <summary>备注(可空,≤256)。</summary>
        [ImportColumn("备注", Order = 4)]
        public string? Remark { get; set; }
    }
}