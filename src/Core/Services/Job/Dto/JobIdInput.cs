namespace AdminCore.Core.Services.Job.Dto
{
    /// <summary>
    /// 单任务操作入参 {id}(启用/禁用/立即执行共用);控制器与 SysOrg 删除同口径双源取 id([FromQuery] 优先,body 兜底)。
    /// </summary>
    public sealed record JobIdInput(long Id);
}
