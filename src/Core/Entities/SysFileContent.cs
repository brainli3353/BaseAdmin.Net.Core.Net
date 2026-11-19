using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 文件内容（DB 模式，参考手册 §4.5）：二进制内容成行存储，与 sys_file 一对一（SysFileId 指向 sys_file 主键）。
    /// 常规表（无 [LogTable]：内容非日志，随主库路由）；删除走物理删（内容行无审计保留价值）。
    /// 口径同 SysFile：按任务书 task_012 要求继承 EntityBase，[IncreTable] 增量建表。
    /// </summary>
    [SugarTable("sys_file_content", "文件内容")]
    [IncreTable]
    [SysTable]
    public class SysFileContent : EntityBase
    {
        /// <summary>sys_file 主键（本行是哪个文件的内容；与 sys_file 一对一，去重复用文件时内容不重复生成）。</summary>
        [SugarColumn(ColumnDescription = "文件Id")]
        public long SysFileId { get; set; }

        /// <summary>二进制内容（显式 bytea；单文件大小受 Upload:MaxSize 约束，全量内存读写可接受）。</summary>
        [SugarColumn(ColumnDataType = "bytea", ColumnDescription = "文件内容")]
        public byte[]? Content { get; set; }
    }
}
