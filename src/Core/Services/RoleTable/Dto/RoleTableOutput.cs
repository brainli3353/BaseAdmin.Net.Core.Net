namespace AdminCore.Core.Services.RoleTable.Dto
{
    /// <summary>角色字段权限回读输出 {roleId, items}(与 Grant 全量替换对偶;items 元素为 "表名:列名")。</summary>
    public sealed record RoleTableOutput(long RoleId, List<string> Items);
}
