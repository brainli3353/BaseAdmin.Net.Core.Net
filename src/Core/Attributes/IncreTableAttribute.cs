namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 标记实体参与 CodeFirst 增量建表(对齐 baseadmin 惯例:[IncreTable] 增量建表、[IncreSeed] 增量种子)。
    /// </summary>
    /// <remarks>
    /// WHY 单独立标记属性而非只认 [SugarTable]:建表扫描口径与"业务实体声明"解耦——
    /// 后续可只对 [IncreTable] 实体做增量 DDL,其余 [SugarTable] 实体(如只读映射外部表)不受影响。
    /// task_008 起集中落点 Core/Attributes/(原 Core/Entities/,语义不变)。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public class IncreTableAttribute : Attribute
    {
    }
}
