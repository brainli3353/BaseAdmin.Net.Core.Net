using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 定时任务执行日志行(契约 GET api/sysJob/logPage):
    /// {id, jobName, trigger, triggeredAt, durationMs, success, errorMsg, instanceId, nodeTag}。
    /// </summary>
    public sealed record JobLogOutput(
        long Id,
        string? JobName,
        string? Trigger,
        DateTime? TriggeredAt,
        [property: JsonConverter(typeof(LongNumberJsonConverter))] long DurationMs,
        bool Success,
        string? ErrorMsg,
        string? InstanceId,
        string? NodeTag);
}
