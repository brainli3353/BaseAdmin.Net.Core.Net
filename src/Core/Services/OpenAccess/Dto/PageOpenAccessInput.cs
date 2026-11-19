namespace AdminCore.Core.Services.OpenAccess.Dto
{
    /// <summary>
    /// 开放接口分页请求 {accessKey?, pageIndex, pageSize, startDate?, endDate?}(JSON camelCase)。AccessKey 模糊(包含)过滤。
    /// 批 UX B1d 时间范围:按 CreateTime 过滤(建号时刻,列表已有 CreateTime 输出列)。
    /// </summary>
    public sealed record PageOpenAccessInput(string? AccessKey, int PageIndex = 1, int PageSize = 10, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput
    {
        // 本域无表头动态排序(固定 Id desc 的 join 投影,批 CI 边界):显式接口实现返回 null,公开构造/JSON 形态不变(批 S-G B5b)
        string? AdminCore.Core.Models.IPageInput.OrderBy => null;

        bool? AdminCore.Core.Models.IPageInput.Desc => null;
    }
}
