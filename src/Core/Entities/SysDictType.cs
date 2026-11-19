using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 数据字典类型(task_011 契约 §1:两级模型的上层,Name/Code/IsEnum/Remark)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// Code 唯一性由服务层校验(建库不配唯一约束,与项目实体口径一致);
    /// IsEnable 为缓存读取「双 Enable」过滤的一半(值级另一半见 SysDictData.IsEnable)——
    /// 契约字段表未列,但服务契约(缓存读只取双 Enable)必需,对齐 Admin.NET 口径补齐。
    /// </remarks>
    [SugarTable("sys_dict_type", "字典类型")]
    [IncreTable]
    [SysTable]
    public class SysDictType : EntityTenant
    {
        /// <summary>字典类型名称(展示名;ORM 读回无空值保证,按项目实体惯例用可空引用类型)。</summary>
        [SugarColumn(ColumnDescription = "字典类型名称", Length = 64)]
        public string? Name { get; set; }

        /// <summary>字典类型编码(唯一,业务标识;服务层校验唯一性)。缓存键 sys_dict:{Code} 即取自本字段。</summary>
        [SugarColumn(ColumnDescription = "字典类型编码", Length = 64)]
        public string? Code { get; set; }

        /// <summary>是否枚举转换字典:true=本字典服务于业务枚举(如 intel 账号状态机),前端/业务按枚举消费。</summary>
        [SugarColumn(ColumnDescription = "是否枚举转换字典")]
        public bool IsEnum { get; set; } = false;

        /// <summary>启用标记:类型停用后其全部字典值对缓存读取不可见(「双 Enable」的类型侧)。</summary>
        [SugarColumn(ColumnDescription = "启用标记")]
        public bool IsEnable { get; set; } = true;

        /// <summary>备注(用途说明;可选,可空——与 SysOrg/SysPos/SysTenant 同口径)。</summary>
        /// <remarks>WHY IsNullable=true:SqlSugar 对 string 引用类型默认 NOT NULL,漏标则 CodeFirst 建表/同步漂移为
        /// NOT NULL,而契约 remark 可选(null 合法)→ insert null 23502(批 V 部署全量验证抓出)。</remarks>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>
        /// 字典值集合(导航属性,契约 §1「类型 LEFT JOIN 值」)。
        /// WHY IsIgnore:本属性仅作结构声明/文档语义,不参与 CodeFirst 建列;
        /// 缓存读查询在 SysDictDataService 内显式 LeftJoin(单条 SQL 一次查出),不依赖 Includes 导航加载。
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public List<SysDictData>? DataList { get; set; }
    }
}
