namespace AdminCore.Core.Services.Role.Dto
{
    /// <summary>
    /// 复制角色入参(批 H 2.2):源角色 Id。新角色 Code/Name 追加 " - copy",三张关联表全量复制。
    /// </summary>
    public sealed record CopyRoleInput(long Id);
}
