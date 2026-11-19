using AdminCore.Core.Attributes;
using SqlSugar;

namespace AdminCore.Core.Entities
{
    /// <summary>
    /// 定时任务定义(task_108):任务名(唯一,对应代码 [JobName])/执行间隔(秒)/启用/最后执行时刻/备注。
    /// 继承 EntityTenant(雪花主键/审计/软删/租户——同 SysOrg/SysUser 口径)。
    /// </summary>
    /// <remarks>
    /// 持久化语义(重启恢复):调度器启动预载本表 → 数据库新增任务重启后生效(动态注册延后迭代——任务书契约);
    /// 管理侧变更(启用/禁用/间隔)由调度器周期快照刷新 1 分钟内热生效,免重启。
    /// JobName 唯一性由服务层校验(不建表设唯一约束——同 SysOrg.Code 口径);
    /// LastRunTime 由调度器触发点(开始时刻)更新,是下次到点判断基准;
    /// Interval 非正数 = 非法配置,调度器跳过该行(记告警)。
    /// </remarks>
    [SugarTable("sys_job_detail", "定时任务")]
    [IncreTable]
    [SysTable]
    public class SysJobDetail : EntityTenant
    {
        /// <summary>任务名(唯一,与代码 [JobName] 特性值匹配;调度键与防重入锁键)。
        /// ORM 回读无 null 保证——项目实体口径可空引用类型(同 SysOrg.Name)。</summary>
        [SugarColumn(ColumnDescription = "任务名", Length = 64)]
        public string? JobName { get; set; }

        /// <summary>执行间隔(秒);服务层校验 &gt; 0(非正 = 非法配置,调度器跳过)。</summary>
        [SugarColumn(ColumnDescription = "执行间隔(秒)")]
        public int Interval { get; set; } = 3600;

        /// <summary>是否启用(false 不执行;调度器周期刷新 1 分钟内热生效)。</summary>
        [SugarColumn(ColumnDescription = "是否启用")]
        public bool Enabled { get; set; } = true;

        /// <summary>最后执行开始时刻(调度器触发点更新;空 = 从未执行 → 首次检查即到点)。</summary>
        [SugarColumn(ColumnDescription = "最后执行开始时刻", IsNullable = true)]
        public DateTime? LastRunTime { get; set; }

        /// <summary>备注(用途说明)。IsNullable=true:无备注不落空串(同 SysOrg.Remark 口径)。</summary>
        [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
        public string? Remark { get; set; }

        /// <summary>
        /// 归属实例标签(批 AJ 5.16 集群):空=任意实例可执行;非空=**仅 NodeTag 匹配的实例**执行(其余实例跳过且不加锁)。
        /// 用途:仅本机资源类任务(本机日志/文件清理、本机指标采集)必须固定在指定实例跑。
        /// 口径:与配置 <c>JobCluster:NodeTag</c> 逐字比对(不区分大小写);生产多实例部署时给各实例配不同 NodeTag。
        /// </summary>
        [SugarColumn(ColumnDescription = "归属实例标签(空=任意)", Length = 128, IsNullable = true)]
        public string? TargetNode { get; set; }
    }
}
