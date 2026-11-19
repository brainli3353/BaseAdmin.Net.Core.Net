namespace AdminCore.Core.Utils.EnumIntrospection
{
    /// <summary>
    /// 单枚举值集合查询(批 Z 6.9):按枚举类型名。
    /// </summary>
    public sealed class EnumQueryInput
    {
        /// <summary>枚举类型名(如 NoticeStatusEnum)。</summary>
        public string EnumName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 实体字段枚举查询(批 Z 6.9):按实体类型名 + 字段名,字段类型须为枚举。
    /// </summary>
    public sealed class EnumDataByFieldInput
    {
        /// <summary>实体类型名(如 SysNotice)。</summary>
        public string EntityName { get; set; } = string.Empty;
        /// <summary>字段名(PascalCase,如 NoticeStatus)。</summary>
        public string FieldName { get; set; } = string.Empty;
    }
}
