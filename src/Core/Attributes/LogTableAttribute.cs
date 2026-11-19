namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记实体落日志库(空标记):仓储/建表按本特性把该实体路由到 ConfigId=log 的连接。
    /// </summary>
    /// <remarks>
    /// 多库通用选库(task_008,对齐 baseadmin 仓储按特性选库):贴本特性的实体(如操作日志 sys_log_op)
    /// 读写日志库;日志库连接未配置时自动回落主库(对齐 baseadmin LogDb 静态取库)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class LogTableAttribute : Attribute
    {
    }
}
