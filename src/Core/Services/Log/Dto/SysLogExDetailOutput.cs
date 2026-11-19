namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 异常日志详情输出(全字段,含 Exception 异常全文/RequestParam/ReturnResult 三大字段)。
    /// </summary>
    public sealed record SysLogExDetailOutput(long Id, string? ControllerName, string? ActionName, string? HttpMethod, string? RequestUrl, string? RemoteIp, string? RequestParam, string? ReturnResult, string? TraceId, int? ThreadId, string? Account, string? RealName, string? Exception, string? LogLevel, DateTime? CreateTime);
}
