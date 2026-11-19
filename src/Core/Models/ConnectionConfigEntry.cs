namespace AdminCore.Core.Models
{
    /// <summary>
    /// appsettings ConnectionConfigs 数组元素模型(多库连接配置,task_008):ConfigId + ConnectionString。
    /// </summary>
    /// <remarks>
    /// 元素缺 ConfigId 或 ConnectionString 为空 → 视为未配置(跳过,不注册该 ConfigId);
    /// 典型用法:注释/删除 log 元素即"日志库未配置 → 回落主库"。
    /// </remarks>
    public sealed class ConnectionConfigEntry
    {
        /// <summary>连接标识(如 main/log;约定 ConfigId 即租户 Id 的常量见 SqlSugarConst)。</summary>
        public string? ConfigId { get; set; }

        /// <summary>连接串(Npgsql 口径)。</summary>
        public string? ConnectionString { get; set; }
    }
}
