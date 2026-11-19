using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Test
{
    /// <summary>
    /// 通用仓储测试探针实体(批 D):独立表 test_repo_probe——
    /// WHY 不直接用 SysConfig:批 D 起多个测试类并发写 sys_config(UnitOfWork/Config 等),
    /// 分页 Total=全表行数 断言对任何并发/残留行敏感(flake 实证:全量并行 Total=87)。
    /// 探针表仅本用例独占,幂等前置清理后 Total 断言确定性成立;继承 EntityBase 保 AOP/审计/软删语义同真实实体。
    /// </summary>
    [SugarTable("test_repo_probe")]
    [IncreTable]
    public class TestRepoProbe : EntityBase
    {
        /// <summary>测试标记(用例唯一前缀,幂等清理/防串读)。</summary>
        [SugarColumn(ColumnDescription = "测试标记", Length = 128, IsNullable = true)]
        public string? Name { get; set; }
    }
}
