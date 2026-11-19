using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 AB 多租户库级隔离探针实体:表 ab_tenant_probe 只在**租户库**出现(主库不应存在该表)——
    /// WHY 用探针而非真实业务实体:库级隔离的判定点是"这条数据物理落在哪个库",
    /// 真实系统实体(sys_user 等)在主库/租户库都存在同名表,断言会被同名行干扰;
    /// 探针表名唯一、仅 [Tenant] 路由,落错库即可被 C2C3 用例的独立取证连接立刻抓到。
    /// 继承 EntityBase 保 AOP(雪花主键/审计填充)与软删语义与真实实体一致。
    /// </summary>
    /// <remarks>
    /// WHY [AdminCore.Core.Attributes.Tenant] 全限定写法:本文件同时 using SqlSugar 与 AdminCore.Core.Attributes,
    /// 而 SqlSugar 亦有同名 TenantAttribute → 短名 [Tenant] 会 CS0104 歧义(同 SqlSugarSetup 的处置口径);
    /// 全限定保证贴的是本仓选库特性(SqlSugarSetup.SelectConfigIdFor / InitTenantTables 按它路由)。
    /// WHY [IncreTable]:与生产实体同口径(增量建表开关 EnableIncreTable=true 时只处理带本特性的实体),
    /// 保证 InitTenantTables 的扫描分流与生产完全一致。
    /// </remarks>
    [SugarTable("ab_tenant_probe")]
    [IncreTable]
    [AdminCore.Core.Attributes.Tenant]
    public class AbTenantProbeEntity : EntityBase
    {
        /// <summary>探针标记(用例唯一前缀,便于定位残留行)。</summary>
        [SugarColumn(ColumnDescription = "名称", Length = 64, IsNullable = true)]
        public string? Name { get; set; }
    }
}