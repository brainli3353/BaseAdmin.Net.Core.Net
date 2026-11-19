namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 登录日志分页请求 {pageIndex, pageSize, account?, startTime?, endTime?, status?, orderBy/desc(表头排序,批 CI,字段名经实体白名单校验), loginType?(批 UX:按实体列 SysLogLogin.LoginType string 精确匹配)}。
    /// 过滤口径(契约 §2):Account 精确匹配、时间范围按创建时间闭区间(日志行只增不改,CreateTime 即登录时刻)、Status 可选三态(不过滤/成功/失败)。
    /// </summary>
    public sealed record PageSysLogLoginInput(
        int PageIndex = 1,
        int PageSize = 10,
        string? Account = null,
        DateTime? StartTime = null,
        DateTime? EndTime = null,
        bool? Status = null,
        string? OrderBy = null,
        bool? Desc = null,
        string? LoginType = null) : AdminCore.Core.Models.IPageInput;
}
