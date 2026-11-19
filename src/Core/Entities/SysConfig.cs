using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统配置(批 D 4.1 正式件):业务配置键值对,Code 为唯一配置标识(点分层级,如 sys.user.resetPwd.default),
    /// Value 为配置值字符串,GroupCode 分组。SysFlag=true 为系统内置项(禁删)。
    /// 继承 EntityBase 获得主键/审计/软删字段。读路径带缓存(CacheConst.KeyConfig,变更即失效)。
    /// </summary>
    /// <remarks>
    /// 批 D 由测试桩(Name 单列)升级为正式实体:存量表增量补列须 [SugarColumn(DefaultValue=...)]
    /// (CodeFirst 补列约定,存量行取默认值不报错);Name 为存量列保留(兼容)。
    /// </remarks>
    [SugarTable("sys_config", "系统配置")]
    [IncreTable]
    public class SysConfig : EntityBase
    {
        /// <summary>配置名称(展示用;存量列;显式 IsNullable——SqlSugar 参照类型 CodeFirst 默认 NOT NULL,C# 可空标注不生效)。</summary>
        [SugarColumn(Length = 255, IsNullable = true)]
        public string? Name { get; set; }

        /// <summary>配置代码(唯一标识,点分层级,如 sys.user.resetPwd.default)。</summary>
        [SugarColumn(Length = 64, DefaultValue = "")]
        public string Code { get; set; } = string.Empty;

        /// <summary>配置值(字符串;清空值 = 空串,合法操作)。</summary>
        [SugarColumn(Length = 1024, DefaultValue = "")]
        public string Value { get; set; } = string.Empty;

        /// <summary>分组代码(如 sys.user / sys.security;空串 = 未分组)。</summary>
        [SugarColumn(Length = 64, DefaultValue = "")]
        public string GroupCode { get; set; } = string.Empty;

        /// <summary>系统内置标志(true = 内置项,禁删;业务新增项为 false)。DefaultValue="false" 兜底存量行(PG 布尔字面量)。</summary>
        [SugarColumn(DefaultValue = "false")]
        public bool SysFlag { get; set; }

        /// <summary>备注(列长 255 对齐存量 Name/审计字段口径)。</summary>
        [SugarColumn(Length = 255, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>
        /// 枚举选项(仅枚举型配置使用;每行一项 `值|显示名`,如 `0|禁用`;空串 = 非枚举)。
        /// 管理端据此把值列渲染为快捷下拉选择:非空优先于 bool 推断(值 True/False 也可作枚举)。
        /// </summary>
        [SugarColumn(Length = 1024, IsNullable = true)]
        public string? EnumOptions { get; set; }
    }
}
