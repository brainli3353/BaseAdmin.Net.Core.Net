namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 用户归属标记(task_013 数据权限):被标记属性为行级「创建用户」归属字段,
    /// 仅本人(4)范围行级过滤时,实体全部 [OwnerUser] 属性须等于当前用户 Id(多属性 OR 联结)。
    /// 反射标记属性(非 ModelBinding):唯一消费点 Core/SqlSugar/SqlSugarFilter(按请求装配 QueryFilter)。
    /// Inherited=true:EntityBase.CreateUserId 统一标注,数据权限实体基类继承即生效,业务零感知。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class OwnerUserAttribute : Attribute
    {
    }
}
