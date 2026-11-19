namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记实体落租户库(空标记):仓储/建表按本特性把该实体路由到 ConfigId=租户 Id 的连接。
    /// </summary>
    /// <remarks>
    /// 租户基座(task_008,基座先行/业务后补):本任务只定义特性与选库机制,
    /// 租户库懒加载(按租户初始化独立库)随多租户业务模块后补——后补时改仓储选库单点
    /// (ConfigId=当前租户 Id,常量约定见 SqlSugarConst),本特性零改动。
    /// 基座期贴本特性的实体一律落主库。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class TenantAttribute : Attribute
    {
    }
}
