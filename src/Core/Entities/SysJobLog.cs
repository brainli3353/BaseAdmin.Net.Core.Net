using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 定时任务执行日志(task_108 增强):调度器每轮任务执行完成(成功/异常)落 1 行——
    /// 执行痕迹的历史持久档(sys_job_detail.LastRunTime 只记最近一轮开始时刻,会被覆盖)。
    /// </summary>
    /// <remarks>
    /// 写入路径:JobScheduler.ExecuteTriggeredAsync 在 _dbSerial 串行锁内独立 scope 落行
    /// (G7「每操作独立 scope」安全形态);宿主停止取消(执行被中断)不落行——中断不算完成轮;
    /// 落行失败仅告警,绝不阻断执行/锁释放(可观测性永不拖累业务)。
    /// 保留期:CleanSysLogJob 与六张日志表同批按 SysJob:LogRetentionDays(缺省 30 天)物理清理(CreateTime 口径)。
    /// 租户隔离:EntityTenant(与 SysJobDetail 同口径,框架自有表)。
    /// </remarks>
    [SugarTable("sys_job_log", "定时任务执行日志")]
    // WHY 索引:高频查询列——CleanSysLogJob 保留期清理＋近期执行查询(EXPLAIN 实测 ORDER BY "CreateTime" DESC LIMIT 20 为 Seq Scan,本批动机)
    [SugarIndex("i_sys_job_log_createtime", nameof(CreateTime), OrderByType.Desc)]
    [IncreTable]
    [SysTable]
    public class SysJobLog : EntityTenant
    {
        /// <summary>任务名(与 sys_job_detail.JobName 一致,无外键,一致性由调度器保证)。</summary>
        [SugarColumn(ColumnDescription = "任务名", Length = 64)]
        public string JobName { get; set; } = string.Empty;

        /// <summary>触发方式:scheduled=定时到点 / manual=RunNow 手动(与 JobContext.Trigger 同口径)。</summary>
        [SugarColumn(ColumnDescription = "触发方式", Length = 16)]
        public string Trigger { get; set; } = string.Empty;

        /// <summary>执行开始时刻(调度器触发时刻,与 sys_job_detail.LastRunTime 同口径)。</summary>
        [SugarColumn(ColumnDescription = "执行开始时刻")]
        public DateTime TriggeredAt { get; set; }

        /// <summary>执行耗时(毫秒,触发至执行体完成/抛异常)。</summary>
        [SugarColumn(ColumnDescription = "耗时(毫秒)")]
        public long DurationMs { get; set; }

        /// <summary>执行体是否正常完成(false=抛异常,错误信息见 ErrorMsg)。</summary>
        [SugarColumn(ColumnDescription = "是否成功")]
        public bool Success { get; set; }

        /// <summary>错误信息(Exception.Message,截断 512 防大字段膨胀;仅失败时写入)。</summary>
        [SugarColumn(ColumnDescription = "错误信息", Length = 512, IsNullable = true)]
        public string? ErrorMsg { get; set; }

        /// <summary>执行实例 Id(多实例集群对照,JobScheduler.InstanceId)。</summary>
        [SugarColumn(ColumnDescription = "执行实例Id", Length = 64)]
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>实例标签(部署时指定,便于区分机器;集群未启用时为空)。</summary>
        [SugarColumn(ColumnDescription = "实例标签", Length = 128, IsNullable = true)]
        public string? NodeTag { get; set; }
    }
}
