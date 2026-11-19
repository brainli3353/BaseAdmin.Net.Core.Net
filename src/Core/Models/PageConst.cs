namespace AdminCore.Core.Models
{
    /// <summary>
    /// 分页边界常量(批 S-G 纵深期·分页边界止血,单点收口全仓 20 处 <c>Math.Max(1, input.PageSize)</c> 调用点)。
    /// </summary>
    /// <remarks>
    /// WHY:历史写法只防"小"(Math.Max 下限)不防"大"——无上限时 <c>?pageSize=100000</c> 可一次拖走全表
    /// (拖库面 + 超大响应内存/带宽放大)。统一收口为 <c>Math.Clamp(input.PageSize, 1, MaxPageSize)</c>:
    /// 下限 1 防除零/负数分页,上限 200 防拖库与超大响应。
    /// 注意与导出上限区分:导出(如 SysLogExport 等批处理上限)是另一条链路,另行常量,勿复用本件充数。
    /// </remarks>
    public static class PageConst
    {
        /// <summary>分页缺省每页行数(与既有 DTO 缺省值一致,如 PageJobLogInput.PageSize=10)。</summary>
        public const int DefaultPageSize = 10;

        /// <summary>分页每页行数硬上限(防拖库/防超大响应;超过一律钳到 200)。</summary>
        public const int MaxPageSize = 200;
    }
}
