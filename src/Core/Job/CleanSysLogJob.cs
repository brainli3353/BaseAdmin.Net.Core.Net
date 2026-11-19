using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.Job
{
    /// <summary>
    /// 内置定时任务①(框架首个落地任务,task_108):系统日志清理——
    /// 物理删除保留期外的八张日志/审计表数据(本仓现有日志类表全集):
    /// sys_log_op / sys_log_login / sys_log_diff / sys_log_http / sys_log_ex / sys_log_event / sys_job_log / sys_log_msg。
    /// </summary>
    /// <remarks>
    /// 保留天数:配置键 <c>SysJob:LogRetentionDays</c>(缺省 30;非正数回退 30——与 SysLogLoginService.ClearAsync 同口径,
    /// 零配置可用,运维免改码调整)。
    /// 删除方式:物理删除(保留期语义=行消失——同日志服务 ClearAsync 口径;非软删)。
    /// 注册(§99③ 零改 Program.cs):[Service] 显式锁定 ServiceType=具体类——调度器按具体类型解析任务
    /// (同 OpLogSubscriber 口径;省略 ServiceType 会被自动匹配注册成 IJob→具体类,具体类反而解析不到)。
    /// 批 AD 补齐三表:原仅清 op/login/diff——批 I 的 sys_log_ex(异常档)、批 S 的 sys_log_event(事件执行审计)、
    /// 批 AD 的 sys_log_http(出站日志)均**未纳入**,同属"日志表无界增长"缺陷(参照仓 CleanSysLogJob 亦不含 http/ex);
    /// 本批统一收口为六表全集,消除该口径漏洞——属"原判据下判漏"的自纠(判断层缺陷,不开新功能面)。
    /// 2026-09-30:扩第 7 表 sys_job_log(定时任务执行日志,保留期/CreateTime 口径同上)。
    /// 批 BT(2026-10-01):扩第 8 表 sys_log_msg(站内消息日志,批 G 落库面先于批 AD 清理收口存在,批 AD 收口六表时漏纳——
    /// 与 ex/http 同属"日志表无界增长"缺陷族,本批按批 AD 口径补全;保留期/CreateTime 口径同上,[LogTable] 路由同 http/ex)。
    /// </remarks>
    [JobName("clean_sys_log")]
    [Service(ServiceType = typeof(CleanSysLogJob))]
    public sealed class CleanSysLogJob : IJob
    {
        private readonly IServiceProvider _rootProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CleanSysLogJob> _logger;

        public CleanSysLogJob(IServiceProvider rootProvider, IConfiguration configuration, ILogger<CleanSysLogJob> logger)
        {
            _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ExecuteAsync(JobContext context, CancellationToken ct)
        {
            var days = _configuration.GetValue("SysJob:LogRetentionDays", 30);
            if (days <= 0)
            {
                days = 30;
            }

            var cutoff = DateTime.Now.AddDays(-days);

            using var scope = _rootProvider.CreateScope();
            var sp = scope.ServiceProvider;
            var opRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogOp>>();
            var loginRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogLogin>>();
            var diffRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogDiff>>();
            var httpRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogHttp>>();
            var exRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogEx>>();
            var eventRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogEvent>>();
            var jobLogRepo = sp.GetRequiredService<ISqlSugarRepository<SysJobLog>>();
            var msgRepo = sp.GetRequiredService<ISqlSugarRepository<SysLogMsg>>();

            // 物理删除:CreateTime 早于截止时刻的历史日志(保留期语义)
            var opDeleted = await opRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var loginDeleted = await loginRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var diffDeleted = await diffRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var httpDeleted = await httpRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var exDeleted = await exRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var eventDeleted = await eventRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var jobLogDeleted = await jobLogRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);
            var msgDeleted = await msgRepo.DeletePhysicalByAsync(u => u.CreateTime < cutoff);

            _logger.LogInformation(
                "定时任务 clean_sys_log 执行完成:保留 {Days} 天(截止 {Cutoff:yyyy-MM-dd HH:mm:ss}),清理 sys_log_op {OpCount} 行 / sys_log_login {LoginCount} 行 / sys_log_diff {DiffCount} 行 / sys_log_http {HttpCount} 行 / sys_log_ex {ExCount} 行 / sys_log_event {EventCount} 行 / sys_job_log {JobLogCount} 行 / sys_log_msg {MsgCount} 行",
                days, cutoff, opDeleted, loginDeleted, diffDeleted, httpDeleted, exDeleted, eventDeleted, jobLogDeleted, msgDeleted);
        }
    }
}
