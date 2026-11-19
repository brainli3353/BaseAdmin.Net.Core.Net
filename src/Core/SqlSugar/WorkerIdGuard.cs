using System.ComponentModel;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 雪花 <c>WorkerId</c> 占用登记与冲突检出(批 AS 集群基线②):
    /// 实例启动时把 <see cref="YitIdHelper.WorkerId"/> 登记到共享缓存,同 WorkerId 已被他实例占用 ⇒ 响亮告警。
    /// </summary>
    /// <remarks>
    /// WHY 需要本件:主键由雪花生成(<c>EntityBaseId</c> + AOP 插入,<c>StaticConfig.CustomSnowFlakeFunc</c>),
    /// 位长 6 ⇒ WorkerId 取值 0–63;**两个实例配了同一个 WorkerId 时,同一毫秒可生成同一个 Id**——
    /// 现象不是"启动就炸",而是运行期偶发主键冲突/串号,离事故现场很远、最难归因。
    /// 而 <c>Yit:WorkerId</c> 缺省就是 <c>1</c>(appsettings),**零配置的多实例部署必然全部同值**,
    /// 属"集群第一天就埋雷"的那类,故与启动建表锁同批做基线。
    ///
    /// 判据(F4,逐条可判):
    /// ① 首次占用 ⇒ <see cref="WorkerIdGuardOutcome.Registered"/> + LogInformation(带 WorkerId 与本实例标识);
    /// ② 键已被他实例占用(序号 &gt; 1)⇒ <see cref="WorkerIdGuardOutcome.Conflict"/> + LogWarning
    ///    (文案含"主键碰撞"检索点与"为各实例注入不同 Yit:WorkerId"动作指引);
    /// ③ 缓存不可用 ⇒ <see cref="WorkerIdGuardOutcome.Unavailable"/>,**跳过检测不阻断启动**
    ///    (诊断件不得变成启动硬门,同 task_010 存活/就绪解耦口径);
    /// ④ 只有 Registered 方才续期与清理——冲突方不续租、不删他人键。
    ///
    /// 占用原语取 <c>IncrementAsync</c>(原子自增,首增者 ==1)而非 <c>LockTakeAsync>:
    /// 后者写的 token 在 Redis 档是原始串、Memory 档存进进程内字典,**两档都无法用 GetAsync 读回占用者身份**;
    /// 而本件需要"占到/没占到"这一与档位无关的判据——取 Increment 后:1 = 我首个占用,&gt;1 = 他人在先
    /// (同 JobScheduler 防重入锁先例,约定 #11 复用既有原语,不发明平行实现)。
    ///
    /// 续期与残留:登记键 TTL = <see cref="RegistrationTtl"/>(10 分钟),本件每 <see cref="RenewInterval"/>
    /// (5 分钟)续一次 ⇒ 进程活着登记就一直有效;宿主停止(正常重启/发布)在 <c>finally</c> **主动删键**,
    /// 避免"同实例重启后被自己的残留误判成冲突"。
    /// 只有异常崩溃(来不及删)才残留,残留期内同 WorkerId 新实例会收到一次 Conflict 告警——
    /// 告警文案已明写"或前次异常退出的残留(≤TTL 自愈)",不要求人工处置。
    ///
    /// 检出边界(登记不整改):检测发生在**启动那一刻**。已在跑的实例不会"回头"发现后来者与自己撞号
    /// (双向发现需全集群心跳比对,超出基线范围);现象足以定位:**后启动者必然告警**。
    /// 档位边界:Memory 档键不跨进程 ⇒ 每实例都判 Registered、永不告警(检测无意义,同差距清单 G18)。
    ///
    /// 注册(§99③ 零改 Program.cs):<c>[Service]</c> 显式锁定 ServiceType = 具体类 + Singleton,
    /// AddAutoServices 的 HostedService 分支据此补注册 IHostedService(工厂经主服务类型解析共享同一单例,
    /// 与 JobScheduler/EventBus 同模式)。
    /// </remarks>
    [Service(ServiceType = typeof(WorkerIdGuard), Lifetime = ServiceLifetime.Singleton)]
    public sealed class WorkerIdGuard : BackgroundService
    {
        /// <summary>登记键 TTL 兜底(批 AS)= 10 分钟:须明显大于 <see cref="RenewInterval"/>,防一次续租抖动丢登记。</summary>
        public static readonly TimeSpan RegistrationTtl = TimeSpan.FromMinutes(10);

        /// <summary>续期周期(批 AS)= 5 分钟:远小于 TTL ⇒ 存活期间登记不失效;进程退出后最迟 10 分钟自然消失。</summary>
        public static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(5);

        private readonly ICacheService _cache;
        private readonly ILogger<WorkerIdGuard> _logger;

        // 实例标识:机器名:进程号:GUID——**每次进程启动都不同**(与 IJobScheduler.InstanceId 同构),
        // 只用于日志检索(冲突时告警方与占用方各在自己的启动日志里留了这条),不参与判据计算。
        // 实例标识(批 AT F1):取身份单点 ⇒ 与调度节点表(sys_job_node)/监控快照表(sys_server_metrics)同一 InstanceId,
        // 三张可观测视图可对号；只用于日志检索(冲突时告警方与占用方各在自己的启动日志里留了这条),不参与判据计算。
        // WHY 构造期 Resolve 而非直接读 Current:NodeTag 可能要读 JobCluster:NodeTag 覆盖值,而 hosted service
        //   构造顺序不确定——谁先构造都必须带配置,否则自定义标签会被缺省机器名抢先生效并被缓存。
        private readonly string _instanceId;

        public WorkerIdGuard(IServiceProvider rootProvider, ICacheService cache, ILogger<WorkerIdGuard> logger)
        {
            // IConfiguration 可选解析(最小宿主/单测容器可能未注册 → 按缺省机器名),口径同 JobScheduler
            _instanceId = InstanceIdentity.Resolve(rootProvider.GetService<IConfiguration>()).InstanceId;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 生效 WorkerId 取自 YitIdHelper(AddSqlSugar 期已按配置 Init 过);未 Init 时为缺省 1,同 appsettings 缺省
            var workerId = YitIdHelper.WorkerId;
            var key = CacheConst.KeyWorkerId + workerId;

            var outcome = await TryRegisterAsync(_cache, workerId, _instanceId, _logger);

            // 判据④:非占用方不续租、不清键(动了会伤及他人登记)
            if (outcome != WorkerIdGuardOutcome.Registered)
            {
                return;
            }

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(RenewInterval, stoppingToken);
                    await _cache.SetExpireAsync(key, RegistrationTtl);
                }
            }
            catch (OperationCanceledException)
            {
                // 宿主停止:走 finally 主动删键(正常重启不该留残留)
            }
            catch (Exception ex)
            {
                // 续期失败(缓存抖动):登记最迟 TTL 后失效 ⇒ 其后同 WorkerId 实例启动不再被告警。
                // 不上抛(诊断件失败不拖垮宿主),但日志可检索。
                _logger.LogWarning(ex, "雪花 WorkerId={WorkerId} 占用登记续期失败(登记将在 {Ttl} 后失效,期间同值实例启动不再告警)", workerId, RegistrationTtl);
            }
            finally
            {
                try
                {
                    await _cache.RemoveAsync(key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "雪花 WorkerId={WorkerId} 占用登记清理失败(残留至 {Ttl} 自愈)", workerId, RegistrationTtl);
                }
            }
        }

        /// <summary>
        /// 占用登记一次(判据①②③的可测入口,供后台循环与单测直接调用)。
        /// </summary>
        /// <param name="cache">缓存抽象(登记载体)。</param>
        /// <param name="workerId">本实例生效的雪花 WorkerId。</param>
        /// <param name="instanceId">本实例标识(仅入日志)。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>三态结果;缓存异常归一为 <see cref="WorkerIdGuardOutcome.Unavailable"/>(不外抛)。</returns>
        public static async Task<WorkerIdGuardOutcome> TryRegisterAsync(
            ICacheService cache, int workerId, string instanceId, ILogger logger)
        {
            var key = CacheConst.KeyWorkerId + workerId;

            try
            {
                // 原子自增:键不存在从 0 起,首增者得 1(与其他实例无竞争窗口)
                var seq = await cache.IncrementAsync(key, RegistrationTtl);

                if (seq == 1)
                {
                    logger.LogInformation(
                        "雪花 WorkerId={WorkerId} 占用登记成功(实例 {InstanceId});多实例部署该值必须全局唯一(0–63)",
                        workerId, instanceId);
                    return WorkerIdGuardOutcome.Registered;
                }

                // 占用方身份不在键值里(Increment 只有计数),故指引运维去"另一实例的启动日志"查同 WorkerId 的登记行
                logger.LogWarning(
                    "雪花 WorkerId={WorkerId} 冲突:登记键 {Key} 已被其他实例占用(序号 {Seq})——同 WorkerId 双实例 = 同毫秒可生成同一主键(碰撞/串号,现象在运行期才暴露);请为各实例注入不同 Yit__WorkerId(本实例 {InstanceId})。若为前次异常退出的残留,最迟 {Ttl} 自愈,无需处置",
                    workerId, key, seq, instanceId, RegistrationTtl);
                return WorkerIdGuardOutcome.Conflict;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "雪花 WorkerId={WorkerId} 冲突检测跳过(缓存不可用,启动不阻断;本实例 {InstanceId})", workerId, instanceId);
                return WorkerIdGuardOutcome.Unavailable;
            }
        }
    }

    /// <summary>WorkerId 占用登记结果(批 AS,判据 F4 的可断言形状)。</summary>
    [Description("WorkerId 占用登记结果")]
    public enum WorkerIdGuardOutcome
    {
        /// <summary>① 本实例首个占用该 WorkerId(已登记,后台负责续期与清理)。</summary>
        [Description("已登记")]
        Registered,

        /// <summary>② 同 WorkerId 已被其他实例占用(或崩溃残留)——已响亮告警。</summary>
        [Description("冲突")]
        Conflict,

        /// <summary>③ 缓存不可用 ⇒ 检测跳过(不阻断启动;Memory 档下检测无跨实例意义)。</summary>
        [Description("缓存不可用")]
        Unavailable
    }
}
