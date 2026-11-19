using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统文件（元数据 + 位置指针，参考手册 §4.5）：一个文件一行，Provider 字段标明内容实际存放位置
    /// （Local=wwwroot/uploads 物理文件 / Db=sys_file_content 表），Url 为对外访问地址，DataId 为业务关联通用外键。
    /// 口径说明：按任务书 task_012 要求不归属租户（无需租户行级过滤）——未用 008 起"业务实体一律 EntityTenant"
    /// 的项目公约；D1.3 S-G 纵深起继承 EntityBaseOrg（无租户机构数据权限基类）：机构范围行级过滤（CreateOrgId）
    /// 与 AOP 写入自动填生效，租户语义保持不变——任务书明文优先，如需租户隔离由业务层自行控制。
    /// [IncreTable] CodeFirst 增量建表，[SysTable] 标记主库系统表。
    /// </summary>
    [SugarTable("sys_file", "系统文件")]
    [IncreTable]
    [SysTable]
    public class SysFile : EntityBaseOrg
    {
        /// <summary>文件 MD5（小写 32 位 hex；去重键=MD5+大小，两者相同视为同一文件，重传复用不产生新行）。</summary>
        [SugarColumn(ColumnDescription = "文件MD5", Length = 32)]
        public string? FileMd5 { get; set; }

        /// <summary>大小（KB，向上取整，0 字节文件为 0；参与去重判定）。</summary>
        [SugarColumn(ColumnDescription = "大小KB")]
        public long SizeKb { get; set; }

        /// <summary>原始文件名（客户端文件名原样保留，展示/下载命名用）。</summary>
        [SugarColumn(ColumnDescription = "文件名", Length = 255)]
        public string? FileName { get; set; }

        /// <summary>路径段（日期目录，如 2026/8/27；Db 模式无物理路径含义，同样记录便于模式切换后迁移）。</summary>
        [SugarColumn(ColumnDescription = "路径段", Length = 64)]
        public string? FilePath { get; set; }

        /// <summary>规范化后缀（含前导点，如 .jpg；jpeg 等按配置表规范化为 jpg，表外后缀拒绝）。</summary>
        [SugarColumn(ColumnDescription = "后缀", Length = 32)]
        public string? Suffix { get; set; }

        /// <summary>内容存储位置："Local"=本地 wwwroot/uploads，"Db"=sys_file_content 表（仅两档，OSS/SSH 裁剪，后续按需扩展）。</summary>
        [SugarColumn(ColumnDescription = "存储Provider", Length = 16)]
        public string? Provider { get; set; }

        /// <summary>业务关联通用外键（关联业务表主键，任意业务表可用；NULL=未关联，UpdateFileByDataId 差集回填）。</summary>
        [SugarColumn(ColumnDescription = "业务关联DataId", IsNullable = true)]
        public long? DataId { get; set; }

        /// <summary>访问 URL：Local 模式=静态直链 /uploads/{路径段}/{id}{后缀}（wwwroot 托管）；Db 模式=/api/sysFile/download?id=..&md5=..（md5 校验防篡改）。</summary>
        [SugarColumn(ColumnDescription = "访问URL", Length = 512)]
        public string? Url { get; set; }
    }
}
