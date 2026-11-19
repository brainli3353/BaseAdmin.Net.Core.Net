namespace AdminCore.Core.Services.RoleTable.Dto
{
    /// <summary>
    /// 角色字段权限授权请求 {roleId, items}(JSON camelCase)。
    /// items 元素形如 <c>"表名:列名"</c>(如 "sys_user:Account");**全量替换语义**——提交什么就是什么,空数组=清空该角色全部列授权。
    /// </summary>
    public sealed record RoleTableInput(long RoleId, List<string> Items);
}
