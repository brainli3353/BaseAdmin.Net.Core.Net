namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 更新角色请求 {id, name?, remark?, dataScope?}。口径:仅改展示字段 + 数据范围(task_013);
    /// Code(业务标识)建号后不可变——改 Code 会使其余引用(缓存键无关,但语义标识)漂移,前端不开放;
    /// 内置角色(SysFlag)允许改名称/备注/数据范围、禁止删除(契约 §7/§8)。
    /// WHY DataScope 可空:null = 不改(部分更新语义,对齐 UpdateOrgInput 口径);1–5 白名单在服务校验,
    /// 变更时服务侧失效该角色全部用户的数据权限缓存(缓存键见 CacheConst)。
    /// </summary>
    public sealed record UpdateRoleInput(long Id, string? Name, string? Remark, long? DataScope);
}
