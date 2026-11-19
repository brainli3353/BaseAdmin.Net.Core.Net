using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Job;
using AdminCore.Core.Services.Job.Dto;
using AdminCore.Core.Cache;
using AdminCore.Core.Job;
using AdminCore.Core.Localization;
using Microsoft.AspNetCore.Mvc;

namespace AdminCore.Web.Entry.Controllers
{
    /// <summary>
    /// 定时任务管理 API(task_108):分页/新增/更新/启用/禁用/立即执行(契约六端点;无删除——禁用即停用)
    /// ＋执行日志分页 logPage(2026-09-30,sys_job_log)。
    /// 全部端点标注 [MenuPerm] 受校验(批 A 2.10 覆盖:标注端点校验、未标注默认放行,超管穿透)。
    /// 路由风格与 SysOrgController 同口径:api/sysJob + 动词段;异常统一 BizError 转 HTTP 400 + ApiResult 出口。
    /// </summary>
    [ApiController]
    [Route("api/sysJob")]
    public class SysJobController : ControllerBase
    {
        private readonly SysJobService _jobService;
        private readonly IJobScheduler _jobScheduler;
        private readonly ICacheService _cache;

        public SysJobController(SysJobService jobService, IJobScheduler jobScheduler, ICacheService cache)
        {
            _jobService = jobService;
            _jobScheduler = jobScheduler;
            _cache = cache;
        }

        /// <summary>分页查询(契约 Page)。</summary>
        [HttpGet("page")]
        [MenuPerm("sysJob/page")]
        public async Task<IActionResult> Page([FromQuery] PageSysJobInput input)
        {
            try
            {
                return Ok(ApiResult<PageResult<JobDetailOutput>>.Success(await _jobService.PageAsync(input)));
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>
        /// 执行日志分页(2026-09-30):每轮任务执行完成(成功/异常)1 行——
        /// 任务页「查看记录」弹窗入口;权限键 sysJob/logPage(菜单种子 Type=3 按钮)。
        /// </summary>
        [HttpGet("logPage")]
        [MenuPerm("sysJob/logPage")]
        public async Task<IActionResult> LogPage([FromQuery] PageJobLogInput input)
        {
            try
            {
                return Ok(ApiResult<PageResult<JobLogOutput>>.Success(await _jobService.PageJobLogAsync(input)));
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>新增(契约 Add;默认启用)。</summary>
        [HttpPost("add")]
        [MenuPerm("sysJob/add")]
        public async Task<IActionResult> Add([FromBody] AddJobInput input)
        {
            try
            {
                return Ok(ApiResult<long>.Success(await _jobService.AddAsync(input)));
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>更新(契约 Update:增量显式字段;jobName 不可变更)。</summary>
        [HttpPost("update")]
        [HttpPut("update")]
        [MenuPerm("sysJob/update")]
        public async Task<IActionResult> Update([FromBody] UpdateJobInput input)
        {
            try
            {
                await _jobService.UpdateAsync(input);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>启用(契约 Enable;调度器 1 分钟内热生效)。</summary>
        [HttpPost("enable")]
        [MenuPerm("sysJob/enable")]
        public async Task<IActionResult> Enable([FromQuery] long? id, [FromBody] JobIdInput? input)
        {
            var targetId = id ?? input?.Id ?? 0;
            try
            {
                await _jobService.EnableAsync(targetId);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>禁用(契约 Disable;停用不删除)。</summary>
        [HttpPost("disable")]
        [MenuPerm("sysJob/disable")]
        public async Task<IActionResult> Disable([FromQuery] long? id, [FromBody] JobIdInput? input)
        {
            var targetId = id ?? input?.Id ?? 0;
            try
            {
                await _jobService.DisableAsync(targetId);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        /// <summary>立即执行(契约 RunNow;防重入锁同口径,上一轮未完成 → 400)。</summary>
        [HttpPost("runNow")]
        [MenuPerm("sysJob/runNow")]
        public async Task<IActionResult> RunNow([FromQuery] long? id, [FromBody] JobIdInput? input)
        {
            var targetId = id ?? input?.Id ?? 0;
            try
            {
                await _jobService.RunNowAsync(targetId);
                return Ok(ApiResult.Success());
            }
            catch (AppException ex)
            {
                return BizError(ex);
            }
        }

        // 业务异常统一出口(HTTP 400 + ApiResult.Fail,消息稳定可检索——约定 #10;同 SysOrgController 口径)。
        // 批 BJ(错误码口径修复,同 SysOrgController D3):回带 errCode + msg 经 ILocalizationService 按请求语言渲染
        //(批 AQ 起 AppException.Of 的 Message 是裸错误码键,旧实现回裸键且丢 errCode)。
        private ObjectResult BizError(AppException ex)
        {
            var errCode = ex.ErrorCode;
            var message = string.IsNullOrEmpty(errCode)
                ? ex.Message
                : (HttpContext.RequestServices?.GetService<ILocalizationService>()?.Get(errCode, ex.Args) is { } text
                    && !string.IsNullOrEmpty(text)
                    && !string.Equals(text, errCode, StringComparison.Ordinal)
                    ? text
                    : ex.Message);
            return new ObjectResult(ApiResult.Fail(message, ex.Code, errCode)) { StatusCode = StatusCodes.Status400BadRequest };
        }
        /// <summary>
        /// GET /api/sysJob/nodes → 集群视图(批 AJ 5.16):实例节点清单(含本实例标注、僵尸节点已过滤)
        /// + 运行中任务清单(哪个实例在跑哪个任务)。数据源=缓存 Hash(KeyJobNode/KeyJobRunning),只读。
        /// </summary>
        [MenuPerm("sysJob/nodes")]
        [HttpGet("nodes")]
        public async Task<IActionResult> Nodes()
        {
            var timeout = DateTime.Now.AddSeconds(-90);
            var nodes = new List<JobNodeOutput>();

            var raw = await _cache.HashGetAllAsync<JobNodePayload>(CacheConst.KeyJobNode);
            foreach (var node in raw)
            {
                // 僵尸节点由读侧按心跳超期过滤(死节点不靠 TTL 摘除)
                var alive = node.LastBeat.HasValue && node.LastBeat.Value >= timeout;
                if (!alive)
                {
                    continue;
                }

                nodes.Add(new JobNodeOutput(node.InstanceId, node.NodeTag, node.StartedAt, node.LastBeat, true,
                    string.Equals(node.InstanceId, _jobScheduler.InstanceId, StringComparison.Ordinal)));
            }

            var runningRaw = await _cache.HashGetAllAsync<JobRunningPayload>(CacheConst.KeyJobRunning);
            var running = runningRaw
                .Select(r => new JobRunningOutput(r.JobName, r.InstanceId, r.NodeTag, r.StartedAt, r.Trigger))
                .ToList();

            return Ok(ApiResult<JobClusterOutput>.Success(new JobClusterOutput(nodes, running)));
        }
    }}