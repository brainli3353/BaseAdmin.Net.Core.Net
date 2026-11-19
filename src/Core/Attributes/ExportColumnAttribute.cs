namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 导出列标注(批 CO 通用列表导出):标注在输出 DTO 属性上,声明该属性为导出列并指定列标题与排序。
    /// </summary>
    /// <remarks>
    /// 语义:
    /// - 一个类型上**任一**属性标注了本特性 → 仅导出被标注属性,列序按 <see cref="Order"/> 升序;
    /// - 一个类型上**无任何**属性标注 → 回退导出全部 public 实例属性(属性名为列头),保证未标注类型也可导出;
    /// - 本特性亦可标注在**类**上(AttributeTargets.Class),供导出服务识别"是否导出型 DTO";
    ///   列定义仍以属性级标注为准。
    /// 只读的 <see cref="DisplayName"/> 经构造函数传入(列标题),可写的 <see cref="Order"/> 经具名参数指定
    /// (如 <c>[ExportColumn("账号", Order = 2)]</c>);缺省 0。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ExportColumnAttribute : Attribute
    {
        /// <summary>列标题(导出文件表头文案)。</summary>
        public string DisplayName { get; }

        /// <summary>列排序序号(升序);缺省 0。</summary>
        public int Order { get; set; }

        public ExportColumnAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }
}