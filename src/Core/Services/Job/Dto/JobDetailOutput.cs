namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务输出视图 {id, jobName, interval, enabled, lastRunTime, remark, createTime}(契约字段全集)。
    /// </summary>
    public sealed record JobDetailOutput(
        long Id,
        string? JobName,
        int Interval,
        bool Enabled,
        DateTime? LastRunTime,
        string? Remark,
        DateTime? CreateTime);
}
