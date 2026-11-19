namespace AdminCore.Core.Services.RoleTable.Dto
{
    /// <summary>
    /// 字段权限授权树节点 {tableName, tableComment, columns}(反射实体生成;已排除 [SysTable] 实体与基类审计字段)。
    /// </summary>
    /// <remarks>WHY class 而非 record:与既有 Dto 口径一致(可写属性便于后续扩展),且便于前端直接消费。</remarks>
    public class TableColumnOutput
    {
        /// <summary>表名(库表名)。</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>表注释(实体 [SugarTable] 的描述;无则空串)。</summary>
        public string TableComment { get; set; } = string.Empty;

        /// <summary>列清单。</summary>
        public List<ColumnItemOutput> Columns { get; set; } = new();
    }

    /// <summary>字段权限授权树列节点 {columnName, columnComment}。</summary>
    public class ColumnItemOutput
    {
        /// <summary>列名(库列名)。</summary>
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>列注释。</summary>
        public string ColumnComment { get; set; } = string.Empty;
    }
}
