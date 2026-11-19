namespace AdminCore.Core.Services.RoleTable.Dto
{
    /// <summary>表级开关切换入参(批 AK):{tableName, enabled}。</summary>
    public sealed class TableScopeInput
    {
        public string TableName { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }
}
