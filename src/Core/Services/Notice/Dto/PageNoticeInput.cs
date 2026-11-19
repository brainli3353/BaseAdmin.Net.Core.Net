namespace AdminCore.Core.Services.Notice.Dto
{
    /// <summary>
    /// 通知公告分页请求 {title?, type, pageIndex, pageSize, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), startDate?, endDate?}(JSON camelCase)。
    /// title 提供时模糊(包含)搜索;type 为 0 不过滤,非 0 时精确过滤(参照口径)。
    /// 批 UX B1d 时间范围:按 CreateTime 过滤(管理列表含草稿,草稿 PublicTime 恒 null——以 PublicTime 作范围会静默漏掉全部草稿;
    /// 列表默认序亦为 CreateTime desc,故取创建时间语义)。
    /// </summary>
    public sealed record PageNoticeInput(string? Title, int Type = 0, int PageIndex = 1, int PageSize = 10, string? OrderBy = null, bool? Desc = null, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput;
}
