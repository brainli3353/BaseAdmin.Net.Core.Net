namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 异常日志列表行输出(不含大字段——参照 IgnoreColumns 口径:Exception/RequestParam/ReturnResult 三 text 大字段
    /// 列表不载防慢查询,经 detail 端点取全字段)。
    /// </summary>
    public sealed record SysLogExOutput(long Id, string? ControllerName, string? ActionName, string? HttpMethod, string? RequestUrl, string? RemoteIp, string? TraceId, int? ThreadId, string? Account, string? RealName, string? LogLevel, DateTime? CreateTime);
}
