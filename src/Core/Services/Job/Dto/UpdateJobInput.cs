namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务更新 {id, interval?, enabled?, remark?}:只更新显式传入的字段(null = 不更新——增量口径,约定 #9);
    /// jobName 为任务身份,不可变更(换任务 = 新增 + 禁用旧任务)。
    /// </summary>
    public sealed record UpdateJobInput(long Id, int? Interval = null, bool? Enabled = null, string? Remark = null);
}
