namespace AdminCore.Core.Attributes
{
    /// <summary>
    /// 导入列标注(批 CP 通用数据导入):标注在导入 DTO 属性上,声明该属性对应源文件表头文案与列序。
    /// </summary>
    /// <remarks>
    /// 语义(与 <see cref="ExportColumnAttribute"/> 对偶):
    /// - 导入服务按 <see cref="Header"/> 名与源文件**表头行**对齐取值(不做列序硬编码,容忍列顺序变化);
    /// - <see cref="Order"/> 仅作列定义排序(升序),便于排错与文档化,不参与表头匹配;
    /// - 仅标注属性参与导入;未标注属性一律忽略(不落值)。
    /// 只读的 <see cref="Header"/> 经构造函数传入(表头文案),可写的 <see cref="Order"/> 经具名参数指定
    /// (如 <c>[ImportColumn("职位编码", Order = 2)]</c>);缺省 0。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ImportColumnAttribute : System.Attribute
    {
        /// <summary>源文件表头文案(与 Excel/CSV 首行单元格文本一致)。</summary>
        public string Header { get; }

        /// <summary>列排序序号(仅用于列定义排序);缺省 0。</summary>
        public int Order { get; set; }

        public ImportColumnAttribute(string header)
        {
            Header = header;
        }
    }
}