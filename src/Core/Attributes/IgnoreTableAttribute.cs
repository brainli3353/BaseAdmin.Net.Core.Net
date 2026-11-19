namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记实体不参与 CodeFirst 建表(空标记,对齐 baseadmin 惯例)。
    /// </summary>
    /// <remarks>
    /// 贴本特性的实体即使带 [SugarTable] 也不被 InitTables 处理——
    /// 典型场景:只读映射外部表/手工维护表,禁止框架 DDL 触碰(增量/全量两种建表模式均排除)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class IgnoreTableAttribute : Attribute
    {
    }
}
