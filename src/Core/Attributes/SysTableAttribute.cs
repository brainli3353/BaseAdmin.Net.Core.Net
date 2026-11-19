namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记实体落主库(空标记,显式声明):仓储/建表按本特性把该实体路由到 ConfigId=main 的连接。
    /// </summary>
    /// <remarks>
    /// 不贴本特性(或贴本特性)的实体均落主库——特性是显式声明而非路由必要条件,
    /// 与 baseadmin [SysTable] 同口径:系统表显式标注,业务/日志表各归其位,后续多库扩展零歧义。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class SysTableAttribute : Attribute
    {
    }
}
