namespace AdminCore.Core.Utils.EnumIntrospection
{
    /// <summary>
    /// 枚举类型输出(批 Z 6.9,参照 Admin.NET EnumTypeOutput 移植):
    /// 类型描述 + 名称 + 值集合。TypeDescribe/TypeRemark 参照同值(本仓无独立备注源,合并为单字段 Describe)。
    /// </summary>
    public sealed class EnumTypeOutput
    {
        /// <summary>枚举类型描述([Description] 特性,无则类型名)。</summary>
        public string TypeDescribe { get; set; } = string.Empty;
        /// <summary>枚举类型名称(如 NoticeStatusEnum)。</summary>
        public string TypeName { get; set; } = string.Empty;
        /// <summary>值集合。</summary>
        public List<EnumEntity> EnumEntities { get; set; } = new();
    }

    /// <summary>
    /// 枚举值条目(参照 EnumEntity 裁剪:无 Theme——本仓无 ThemeAttribute 消费方,#8)。
    /// </summary>
    public sealed class EnumEntity
    {
        /// <summary>成员名(如 Public)。</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>描述([Description] 特性,无则成员名)。</summary>
        public string Describe { get; set; } = string.Empty;
        /// <summary>数值(底层 int)。</summary>
        public int Value { get; set; }
    }
}
