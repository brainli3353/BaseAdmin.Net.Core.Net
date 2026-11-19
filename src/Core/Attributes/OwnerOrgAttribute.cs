namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 机构归属标记(task_013 数据权限):被标记属性为行级「机构」归属字段,
    /// 机构范围(本部门/本部门及以下)行级过滤时,实体全部 [OwnerOrg] 属性须落在用户可见机构集内(多属性 OR 联结)。
    /// 反射标记属性(非 ModelBinding):唯一消费点 Core/SqlSugar/SqlSugarFilter(按请求装配 QueryFilter)。
    /// Inherited=true:具体实体经 EntityBaseData/EntityTenantBaseData 基类继承本标记,无需重复标注。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class OwnerOrgAttribute : Attribute
    {
    }
}
