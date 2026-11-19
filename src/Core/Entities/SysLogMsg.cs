using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统消息日志(批 G 4.8;参照仓 SysLogMsg 移植,去 Device 字段):
    /// 站内消息发送即定格——接收方/发送方各 4 快照字段(自在线表快照),经事件总线异步落日志库。
    /// 继承 EntityTenant(参照 EntityTenantId 同语义:消息属租户内通信,租户隔离——对齐本仓 SysLogLogin 口径);
    /// [LogTable] 多库通用选库(日志库连接配置时落日志库,未配置回落主库);[IncreTable] 增量建表。
    /// 纯推送模型(裁决①):本表即消息唯一持久面,无独立消息实体,离线不补发。
    /// </summary>
    [SugarTable("sys_log_msg", "系统消息日志表")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期按 CreateTime 物理清理＋消息记录分页时间过滤排序
    [SugarIndex("i_sys_log_msg_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    [LogTable]
    public class SysLogMsg : EntityTenant
    {
        /// <summary>消息类型(MessageTypeEnum 名,如 Info/Success)。</summary>
        [SugarColumn(ColumnDescription = "消息类型", Length = 64)]
        public string MessageType { get; set; } = string.Empty;

        /// <summary>消息标题。</summary>
        [SugarColumn(ColumnDescription = "消息标题", Length = 64)]
        public string Title { get; set; } = string.Empty;

        /// <summary>消息内容(大文本 text)。</summary>
        [SugarColumn(ColumnDescription = "消息内容", ColumnDataType = "text")]
        public string Message { get; set; } = string.Empty;

        /// <summary>接收者 Id(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "接收者Id")]
        public long ReceiveUserId { get; set; }

        /// <summary>接收者名称(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "接收者名称", Length = 32, IsNullable = true)]
        public string? ReceiveUserName { get; set; }

        /// <summary>接收者 IP(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "接收者IP", Length = 256, IsNullable = true)]
        public string? ReceiveIp { get; set; }

        /// <summary>接收者浏览器(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "接收者浏览器", Length = 128, IsNullable = true)]
        public string? ReceiveBrowser { get; set; }

        /// <summary>接收者操作系统(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "接收者操作系统", Length = 128, IsNullable = true)]
        public string? ReceiveOs { get; set; }

        /// <summary>发送者 Id(在线表快照;发送者离线时 0)。</summary>
        [SugarColumn(ColumnDescription = "发送者Id")]
        public long SendUserId { get; set; }

        /// <summary>发送者名称(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "发送者名称", Length = 32, IsNullable = true)]
        public string? SendUserName { get; set; }

        /// <summary>发送者 IP(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "发送者IP", Length = 256, IsNullable = true)]
        public string? SendIp { get; set; }

        /// <summary>发送者浏览器(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "发送者浏览器", Length = 128, IsNullable = true)]
        public string? SendBrowser { get; set; }

        /// <summary>发送者操作系统(在线表快照)。</summary>
        [SugarColumn(ColumnDescription = "发送者操作系统", Length = 128, IsNullable = true)]
        public string? SendOs { get; set; }

        /// <summary>发送时间。</summary>
        [SugarColumn(ColumnDescription = "发送时间")]
        public DateTime SendTime { get; set; }
    }
}
