namespace AdminCore.Core.Services.Print.Dto
{
    /// <summary>
    /// 打印模板列表行输出(**不含 Template 大字段**——列表在库层就不选该列,详情端点取全文)。
    /// </summary>
    /// <remarks>
    /// WHY class 而非 positional record:分页投影**下推到 SqlSugar 查询侧**(SELECT 只取这些列),
    /// 而 SqlSugar 不映射 positional record 的构造参数(批 E 实证)——需可写属性。
    /// </remarks>
    public class SysPrintOutput
    {
        /// <summary>主键。</summary>
        public long Id { get; set; }

        /// <summary>模板名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>打印类型(1=浏览器 2=客户端)。</summary>
        public int PrintType { get; set; }

        /// <summary>状态(1=启用 2=停用)。</summary>
        public int Status { get; set; }

        /// <summary>排序。</summary>
        public int OrderNo { get; set; }

        /// <summary>备注。</summary>
        public string? Remark { get; set; }

        /// <summary>创建时间。</summary>
        public DateTime? CreateTime { get; set; }
    }
}
