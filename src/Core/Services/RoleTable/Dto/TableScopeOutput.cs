namespace AdminCore.Core.Services.RoleTable.Dto
{
    /// <summary>
    /// 表级开关清单行(批 AK/G13):{tableName, tableComment, enabled, grantedRoleCount, columnCount}。
    /// enabled=true ⇒ 该表受管(非超管用户按角色列白名单 fail-closed 裁剪);false ⇒ 零影响。
    /// grantedRoleCount=已配置字段权限的角色数(>0 但 enabled=false 提示"有授权但已停管")。
    /// </summary>
    public sealed record TableScopeOutput(
        string TableName,
        string TableComment,
        bool Enabled,
        int GrantedRoleCount,
        int ColumnCount);
}
