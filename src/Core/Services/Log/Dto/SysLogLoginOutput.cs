namespace AdminCore.Core.Services.Log.Dto
{
    /// <summary>
    /// 登录日志输出 {id, account, remoteAddr, browser, os, status, message, loginType, createTime}。
    /// 只读展示对象:审计列(CreateTime)显式带出供前端展示登录时刻;DTO 承载对外契约,实体演进不影响前端。
    /// </summary>
    public sealed record SysLogLoginOutput(
        long Id,
        string? Account,
        string? RemoteAddr,
        string? Browser,
        string? Os,
        bool Status,
        string? Message,
        string? LoginType,
        DateTime? CreateTime);
}
