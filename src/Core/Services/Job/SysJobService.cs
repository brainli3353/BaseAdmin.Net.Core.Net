using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Job;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Job.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

namespace AdminCore.Core.Services.Job
{
    /// <summary>
    /// 定时任务管理服务(task_108):分页/新增/更新/启用/禁用/立即执行(契约全集;无删除——禁用即停用,行保留执行历史)。
    /// 注册:无接口 → [Service] 自注册具体类(同 SysOrgService 口径),控制器直接注入具体类。
    /// </summary>
    /// <remarks>
    /// 业务规则:
    /// 新增校验——任务名非空 + 代码已注册(调度器注册表) + 库内唯一 + 间隔正数(违反即 AppException,HTTP 400);
    /// 更新增量——只更新显式传入字段(约定 #9),jobName 不可变更;
    /// 立即执行——经 IJobScheduler.RunNowAsync(防重入锁同口径:未注册或上一轮未完成 → AppException);
    /// 启用/禁用——改 Enabled 列,调度器周期快照刷新 1 分钟内热生效(免重启)。
    /// 租户隔离:EntityTenant 查询走租户 QueryFilter(登录用户口径,与 SysOrg 同口径)。
    /// </remarks>
    [Service]
    public class SysJobService
    {
        private readonly ISqlSugarRepository<SysJobDetail> _repo;
        private readonly ISqlSugarRepository<SysJobLog> _logRepo;
        private readonly IJobScheduler _scheduler;

        public SysJobService(ISqlSugarRepository<SysJobDetail> repo, ISqlSugarRepository<SysJobLog> logRepo, IJobScheduler scheduler)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _logRepo = logRepo ?? throw new ArgumentNullException(nameof(logRepo));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>分页查询(契约 Page:Name 模糊;按 Id 倒序=新任务在前)。</summary>
        public async Task<PageResult<JobDetailOutput>> PageAsync(PageSysJobInput input)
        {
            var total = new RefAsync<int>();
            var rows = await _repo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(input.Name), u => u.JobName!.Contains(input.Name!))
                // 批 UX:启用状态精确过滤(bool? Enabled,缺省 null=不过滤)
                .WhereIF(input.Enabled.HasValue, u => u.Enabled == input.Enabled)
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<JobDetailOutput>
            {
                Total = total,
                List = rows.Select(ToOutput).ToList()
            };
        }

        /// <summary>
        /// 新增(契约 Add):任务名须代码已注册([JobName] 实现类)+ 库内唯一 + 间隔正数;
        /// 新建任务默认启用(启用后即被调度器 1 分钟内的快照刷新捕获)。
        /// </summary>
        public async Task<long> AddAsync(AddJobInput input)
        {
            var jobName = input.JobName?.Trim();
            if (string.IsNullOrWhiteSpace(jobName))
            {
                throw AppException.Of(ErrorCodes.JobNameRequired);
            }

            if (input.Interval <= 0)
            {
                throw AppException.Of(ErrorCodes.JobIntervalInvalid);
            }

            if (!_scheduler.GetRegisteredJobNames().Contains(jobName))
            {
                throw AppException.With(ErrorCodes.JobCodeUnregistered, "name", jobName);
            }

            if (await _repo.IsAnyAsync(u => u.JobName == jobName))
            {
                throw AppException.With(ErrorCodes.JobExists, "name", jobName);
            }

            var entity = new SysJobDetail
            {
                JobName = jobName,
                Interval = input.Interval,
                Enabled = true,
                Remark = input.Remark
            };

            await _repo.InsertAsync(entity);
            return entity.Id;
        }

        /// <summary>更新(契约 Update:增量更新显式字段;jobName 不可变更)。</summary>
        public async Task UpdateAsync(UpdateJobInput input)
        {
            var row = await _repo.GetFirstAsync(u => u.Id == input.Id)
                ?? throw AppException.Of(ErrorCodes.JobNotFound);

            if (input.Interval is not null && input.Interval.Value <= 0)
            {
                throw AppException.Of(ErrorCodes.JobIntervalInvalid);
            }

            if (input.Interval is not null)
            {
                row.Interval = input.Interval.Value;
            }

            if (input.Enabled is not null)
            {
                row.Enabled = input.Enabled.Value;
            }

            if (input.Remark is not null)
            {
                row.Remark = input.Remark;
            }

            await _repo.UpdateAsync(row);
        }

        /// <summary>启用(契约 Enable)。</summary>
        public Task EnableAsync(long id) => SetEnabledAsync(id, true);

        /// <summary>禁用(契约 Disable:停用不删除,行保留执行历史)。</summary>
        public Task DisableAsync(long id) => SetEnabledAsync(id, false);

        /// <summary>
        /// 执行日志分页(2026-09-30):jobName 精确(任务行「查看记录」弹窗入口),按触发时刻倒序(最新在前);
        /// 数据源=sys_job_log(调度器每轮执行完成落 1 行),保留期由 clean_sys_log 物理清理。
        /// </summary>
        public async Task<PageResult<JobLogOutput>> PageJobLogAsync(PageJobLogInput input)
        {
            var total = new RefAsync<int>();
            var rows = await _logRepo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(input.JobName), u => u.JobName == input.JobName!)
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.TriggeredAt, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<JobLogOutput>
            {
                Total = total,
                List = rows.Select(ToLogOutput).ToList()
            };
        }

        /// <summary>
        /// 立即执行(契约 RunNow):经调度器 RunNowAsync(与调度执行同一条触发路径,防重入锁同口径);
        /// 未注册代码或上一轮未完成 → AppException(HTTP 400)。
        /// </summary>
        public async Task RunNowAsync(long id)
        {
            var row = await _repo.GetFirstAsync(u => u.Id == id)
                ?? throw AppException.Of(ErrorCodes.JobNotFound);

            var ok = await _scheduler.RunNowAsync(row.JobName!);
            if (!ok)
            {
                throw AppException.Of(ErrorCodes.JobRunRejected);
            }
        }

        private async Task SetEnabledAsync(long id, bool enabled)
        {
            var row = await _repo.GetFirstAsync(u => u.Id == id)
                ?? throw AppException.Of(ErrorCodes.JobNotFound);

            row.Enabled = enabled;
            await _repo.UpdateAsync(row);
        }

        private static JobDetailOutput ToOutput(SysJobDetail row)
            => new(row.Id, row.JobName, row.Interval, row.Enabled, row.LastRunTime, row.Remark, row.CreateTime);

        private static JobLogOutput ToLogOutput(SysJobLog row)
            => new(row.Id, row.JobName, row.Trigger, row.TriggeredAt, row.DurationMs, row.Success, row.ErrorMsg, row.InstanceId, row.NodeTag);
    }
}
