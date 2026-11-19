namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务新增 {jobName, interval, remark?}:jobName 须为代码已注册任务([JobName] 实现类),
    /// interval 为正整数(秒);新建任务默认启用。
    /// </summary>
    public sealed record AddJobInput(string? JobName, int Interval = 3600, string? Remark = null);
}
