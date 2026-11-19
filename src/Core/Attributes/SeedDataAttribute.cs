namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 种子执行顺序标记:数据种子类(ISqlSugarEntitySeedData&lt;T&gt;)贴本特性声明执行顺序(升序)。
    /// </summary>
    /// <remarks>
    /// WHY 需要顺序:跨种子引用(如 SysRoleMenuSeedData 引用 SysRoleSeedData().HasData() 取角色 Id)
    /// 要求被引用种子先执行;命令式种子(ISqlSugarSeed)另有类名排序,两者机制独立。
    /// 未贴本特性的数据种子 Order=0(同 Order 按类名排序,保证重启间顺序稳定)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class SeedDataAttribute : Attribute
    {
        /// <summary>执行顺序(升序执行;默认 0)。</summary>
        public int Order { get; }

        public SeedDataAttribute(int order = 0)
        {
            Order = order;
        }
    }
}
