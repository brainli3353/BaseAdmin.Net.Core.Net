namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 操作日志详情输出(全字段,含 RequestParam 请求参数/ReturnResult 返回结果两大字段;批 AZ 参照口径)。
    /// </summary>
    public sealed record SysLogOpDetailOutput(long Id, string? ControllerName, string? ActionName, string? HttpMethod, string? RemoteIp, int ElapsedMs, string? Account, bool Success, string? RequestParam, string? ReturnResult, DateTime? CreateTime);
}
