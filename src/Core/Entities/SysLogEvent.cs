using AdminCore.Core.Attributes;
using AdminCore.Core.EventBus;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 系统事件日志(批 S 5.8,参照 Admin.NET SysLogEvent 裁剪):事件执行审计表——
    /// 每个事件的每次订阅执行落一行(状态机 Running→Success/Retry→Error),
    /// 由 RetryEventHandlerExecutor 写入([LogTable] 选日志库,未配置回落主库)。
    /// 用途:事件消费失败可追溯(哪次失败/重试记录/耗时);参照 13 字段裁剪至实际消费(见 g13 §3 ②)。
    /// </summary>
    [SugarTable("sys_log_event", "系统事件日志")]
    [IncreTable]
    [LogTable]
    [SugarIndex("i_sys_log_event_e", nameof(EventId), OrderByType.Asc)]
    public class SysLogEvent : EntityBase
    {
        /// <summary>事件 ID(发布时生成的唯一标识;参照 RunId+EventId 两列同值,本仓合一)。</summary>
        [SugarColumn(ColumnDescription = "事件ID", Length = 64)]
        public string EventId { get; set; } = string.Empty;

        /// <summary>事件名(与发布时的 eventName 一致,如 op_log/ex_log)。</summary>
        [SugarColumn(ColumnDescription = "事件名", Length = 64)]
        public string EventName { get; set; } = string.Empty;

        /// <summary>事件载荷(JSON 序列化;大字段)。</summary>
        [SugarColumn(ColumnDescription = "事件载荷", ColumnDataType = "text", IsNullable = true)]
        public string? Payload { get; set; }

        /// <summary>异常信息(最终失败的根因异常;大字段)。</summary>
        [SugarColumn(ColumnDescription = "异常信息", ColumnDataType = "text", IsNullable = true)]
        public string? Exception { get; set; }

        /// <summary>耗时(毫秒,自执行开始至终态)。</summary>
        [SugarColumn(ColumnDescription = "耗时毫秒", IsNullable = true)]
        public long? Elapsed { get; set; }

        /// <summary>执行开始时间(本地时间,审计时刻)。</summary>
        [SugarColumn(ColumnDescription = "执行前时间")]
        public DateTime ExecutingTime { get; set; }

        /// <summary>执行终态时间(成功/耗尽时刻)。</summary>
        [SugarColumn(ColumnDescription = "执行后时间", IsNullable = true)]
        public DateTime? ExecutedTime { get; set; }

        /// <summary>订阅特性快照(JSON:重试策略等,执行时定格;变更后可回溯当时的策略)。</summary>
        [SugarColumn(ColumnDescription = "订阅特性", Length = 512, IsNullable = true)]
        public string? Attribute { get; set; }

        /// <summary>触发方法(订阅者类型全名.方法名)。</summary>
        [SugarColumn(ColumnDescription = "触发方法", Length = 128)]
        public string HandlerMethod { get; set; } = string.Empty;

        /// <summary>重试记录(每次失败的"时刻: 消息"按序拼接;无失败为 null)。</summary>
        [SugarColumn(ColumnDescription = "重试记录", Length = 1024, IsNullable = true)]
        public string? RetryRecord { get; set; }

        /// <summary>执行状态(状态机:Running→Success/Retry→Error)。</summary>
        [SugarColumn(ColumnDescription = "事件状态")]
        public EventStatusEnum Status { get; set; }
    }
}
