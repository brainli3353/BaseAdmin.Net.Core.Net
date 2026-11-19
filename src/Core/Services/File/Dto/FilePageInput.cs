namespace AdminCore.Core.Services.File.Dto
{
    /// <summary>
    /// 文件分页请求 {fileName?, pageIndex, pageSize, startDate?, endDate?}(JSON camelCase)。
    /// 字段名照实体实读:SysFile.FileName 模糊(包含) + SysFile.CreateTime(创建时间)范围过滤,
    /// 时间范围口径与日志域(PageSysLogExInput.StartTime/EndTime)一致。
    /// </summary>
    public sealed record FilePageInput(string? FileName, int PageIndex = 1, int PageSize = 10, DateTime? StartDate = null, DateTime? EndDate = null) : AdminCore.Core.Models.IPageInput
    {
        // 本域无表头动态排序(固定 CreateTime desc,批 UX B1c):显式接口实现返回 null,公开构造/JSON 形态不变(批 S-G B5b)
        string? AdminCore.Core.Models.IPageInput.OrderBy => null;

        bool? AdminCore.Core.Models.IPageInput.Desc => null;
    }
}