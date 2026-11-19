using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Test
{
    /// <summary>
    /// 机构数据权限测试实体(task_013):继承 EntityBaseData → 自动获得 CreateOrgId [OwnerOrg]
    /// + 继承的 CreateUserId [OwnerUser](仅本人范围标记),测试启动时 CodeFirst 增量建表(InitTables 扫测试程序集);
    /// 仅测试使用,不污染框架实体集(表名前缀 test_,用毕按 Name 前缀物理清理)。
    /// </summary>
    [SugarTable("test_data_scope_probe")]
    [IncreTable]
    public class TestDataScopeProbe : EntityBaseData
    {
        /// <summary>测试标记(用例唯一前缀,幂等清理/防串读)。</summary>
        [SugarColumn(ColumnDescription = "测试标记", Length = 128)]
        public string? Name { get; set; }
    }

    /// <summary>
    /// 机构数据权限测试实体 + 更新机构(task_013):继承 EntityBaseUpdateData → 另获得 UpdateOrgId [OwnerOrg]
    /// (AOP 更新路径自动填),验证 UpdateOrgId 填充与 [OwnerOrg] 多属性 OR 联结。
    /// </summary>
    [SugarTable("test_data_scope_update_probe")]
    [IncreTable]
    public class TestDataScopeUpdateProbe : EntityBaseUpdateData
    {
        /// <summary>测试标记(用例唯一前缀,幂等清理/防串读)。</summary>
        [SugarColumn(ColumnDescription = "测试标记", Length = 128)]
        public string? Name { get; set; }
    }
}
