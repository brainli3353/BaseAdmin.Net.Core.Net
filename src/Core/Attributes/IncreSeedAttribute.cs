namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记类参与增量种子(对齐 baseadmin 惯例:[IncreTable] 增量建表、[IncreSeed] 增量种子)。
    /// 被标记类须实现 <c>ISqlSugarSeed</c>(命令式种子)或 <c>ISqlSugarEntitySeedData&lt;T&gt;</c>(数据种子);
    /// 幂等性由种子自身保证(每次启动都会重跑,二次执行必须不重复)。
    /// </summary>
    /// <remarks>
    /// task_008 起集中落点 Core/Attributes/(原 Core/SeedData/,语义不变)。
    /// 与 [IncreTable] 成对使用是硬性约定:实体默认 [IncreTable]、种子默认 [IncreSeed]。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class IncreSeedAttribute : Attribute
    {
    }
}
