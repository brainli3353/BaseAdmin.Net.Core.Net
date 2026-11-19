namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 操作日志行输出 {id, controllerName, actionName, httpMethod, remoteIp, elapsedMs, account, success, createTime}。
    /// </summary>
    public sealed record SysLogOpOutput(long Id, string? ControllerName, string? ActionName, string? HttpMethod, string? RemoteIp, int ElapsedMs, string? Account, bool Success, DateTime? CreateTime);
}
