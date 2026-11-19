using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 数据字典值(task_011 契约 §1:两级模型的下层,隶属于 SysDictType)。
    /// 继承 EntityTenant 获得雪花主键/审计/软删/租户字段。
    /// </summary>
    /// <remarks>
    /// Code 在「同一 DictTypeId 下」唯一(跨类型可重名,服务层校验);
    /// IsEnable 为缓存读取「双 Enable」过滤的值级一半;
    /// TagType/StyleSetting/ClassSetting 供前端渲染(标签类型/样式/附加 class),框架不解释其语义。
    /// </remarks>
    [SugarTable("sys_dict_data", "字典值")]
    [IncreTable]
    [SysTable]
    public class SysDictData : EntityTenant
    {
        /// <summary>字典类型 Id(sys_dict_type 主键,外键;服务层校验存在性,不建库级外键——与项目口径一致)。</summary>
        [SugarColumn(ColumnDescription = "字典类型Id")]
        public long DictTypeId { get; set; }

        /// <summary>字典值编码(同类型下唯一,业务标识;服务层校验)。</summary>
        [SugarColumn(ColumnDescription = "字典值编码", Length = 64)]
        public string? Code { get; set; }

        /// <summary>字典值(业务实际存储/消费的值,如 intel 账号状态机的 int 值 "0"-"4")。</summary>
        [SugarColumn(ColumnDescription = "字典值", Length = 128)]
        public string? Value { get; set; }

        /// <summary>标签(前端展示名,如「养号中」)。</summary>
        [SugarColumn(ColumnDescription = "标签", Length = 64)]
        public string? Label { get; set; }

        /// <summary>标签类型(前端渲染:1 主/2 成功/3 警告/4 危险/5 信息,框架不解释语义)。</summary>
        [SugarColumn(ColumnDescription = "标签类型")]
        public int TagType { get; set; } = 1;

        /// <summary>
        /// 样式设置(前端渲染,框架不解释语义)。
        /// IsNullable=true(task_019 实测修复,启动阻塞):种子行不落本列(null),
        /// 缺省时 CodeFirst 建/改列为 NOT NULL → 种子写 23502;且 InitTables 每启动按实体回写,
        /// 库侧手工 DROP NOT NULL 会被回滚,必须实体侧修(同列四处同口径,见类头 remarks 披露)。
        /// </summary>
        [SugarColumn(ColumnDescription = "样式设置", Length = 128, IsNullable = true)]
        public string? StyleSetting { get; set; }

        /// <summary>附加 class(前端渲染,框架不解释语义)。IsNullable=true:同 StyleSetting(task_019 启动阻塞修复)。</summary>
        [SugarColumn(ColumnDescription = "附加class", Length = 128, IsNullable = true)]
        public string? ClassSetting { get; set; }

        /// <summary>显示顺序(缓存读取/分页按本字段升序,同序按 Id 兜底)。</summary>
        [SugarColumn(ColumnDescription = "显示顺序")]
        public int OrderNo { get; set; }

        /// <summary>是否枚举转换字典(随所属类型口径,供值级消费方直接判断)。</summary>
        [SugarColumn(ColumnDescription = "是否枚举转换字典")]
        public bool IsEnum { get; set; } = false;

        /// <summary>启用标记:值停用后对缓存读取不可见(「双 Enable」的值侧;UpdateStatus 端点操作本字段)。</summary>
        [SugarColumn(ColumnDescription = "启用标记")]
        public bool IsEnable { get; set; } = true;

        /// <summary>扩展数据(JSON 字符串,业务自定义扩展,框架不解释语义)。IsNullable=true:同 StyleSetting(task_019 启动阻塞修复)。</summary>
        [SugarColumn(ColumnDescription = "扩展数据", Length = 512, IsNullable = true)]
        public string? ExtData { get; set; }

        /// <summary>备注(用途说明)。IsNullable=true:同 StyleSetting(task_019 启动阻塞修复)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }
    }
}
