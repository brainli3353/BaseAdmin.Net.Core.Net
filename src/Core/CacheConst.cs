namespace AdminCore.Core
{
    /// <summary>
    /// 缓存键常量(task_011 数据字典起立):业务缓存键统一经本常量拼接,
    /// 防裸字符串散落——键格式变更(如加租户段)时单点修改即可全量生效。
    /// </summary>
    public static class CacheConst
    {
        /// <summary>
        /// 数据字典缓存键前缀(完整键 = 前缀 + 字典类型 Code,如 sys_dict:account_status)。
        /// WHY 键只含 Code 不含租户:字典是全局参考数据(种子落默认租户,跨租户共享同一份),
        /// 读路径 ClearFilter 跨租户,缓存维度必须与查询维度一致,否则键空间分裂、失效遗漏。
        /// </summary>
        public const string KeyDict = "sys_dict:";

        /// <summary>
        /// 用户按钮权限集缓存键前缀(task_022;完整键 = 前缀 + 用户 Id,如 KeyUserApiPerm:123)。
        /// WHY 键不含租户段:用户隶属单一租户,与 008 旧键 sys_user_api: 同口径——键维度与查询维度(按用户)一致,
        /// 加租户段只会分裂键空间、遗漏失效。读路径(MenuPermService.GetPermissionsAsync)与
        /// 失效路径(角色/菜单变更)共用本常量,防字面量漂移。
        /// </summary>
        public const string KeyUserApiPerm = "KeyUserApiPerm:";

        /// <summary>
        /// 用户机构数据范围缓存键前缀(task_013 数据权限;完整键 = 前缀 + 用户 Id,如 KeyUserOrg:123)。
        /// 值 = 用户可见机构集(List&lt;long&gt;,全部/仅本人范围为空集负缓存)。
        /// 读路径(SysOrgService.GetUserOrgScopeAsync)与失效路径(角色/用户/机构变更 → SqlSugarFilter.DeleteUserOrgCacheAsync)
        /// 共用本常量,防字面量漂移;TTL 7 天(变更即失效为主机制,TTL 仅作泄漏兜底,同 KeyUserApiPerm 口径)。
        /// </summary>
        public const string KeyUserOrg = "KeyUserOrg:";

        /// <summary>
        /// 用户角色最大数据范围缓存键前缀(task_013;完整键 = 前缀 + 用户 Id,如 KeyRoleMaxDataScope:123)。
        /// 值 = 多角色收敛后的最大范围(DataScopeEnum 数值;枚举值小 = 范围大)。
        /// 与 KeyUserOrg 同读/同失效(两键必须同批失效,否则范围与机构集错配)。
        /// </summary>
        public const string KeyRoleMaxDataScope = "KeyRoleMaxDataScope:";

        /// <summary>
        /// 定时任务防重入锁键前缀(task_108;完整键 = 前缀 + 任务名,如 sys_job:clean_sys_log)。
        /// 获取 = IncrementAsync 原子自增(首个自增者持锁,后到者见值 &gt;1 跳过「上一轮未完成」);
        /// 释放 = RemoveAsync(执行完 finally 统一释放);TTL 30 分钟仅防进程异常后的锁残留泄漏,不是执行超时控制。
        /// </summary>
        public const string KeyJobLock = "sys_job:";

        /// <summary>
        /// 用户 Token 版本键前缀(task_007 强制失效;完整键 = 前缀 + 用户 Id,如 sys_user_token:123)。
        /// 值 = 当前 TokenVersion(int;起始 1 只增)。登录签发写本键(JwtAuthHandler 快路径);
        /// 改密/重置/停用经 SysUserService.OfflineAndExpireToken 删本键(批 A 1.11 三动作闭环②),
        /// 删后 JwtAuthHandler 快路径缺失 → DB 回查读新版本 → 旧 Token 即被拒。
        /// WHY 本常量:此前键散落于 JwtAuthHandler/AuthService 字面量,批 A 起改密侧需同键读写,统一单点防漂移(约定 #11)。
        /// </summary>
        public const string KeyUserToken = "sys_user_token:";

        /// <summary>
        /// 账号级密码错误计数键前缀(task_007;完整键 = 前缀 + 账号,如 sys_password_error_times:admin)。
        /// 值 = 窗口内连续失败次数(30min TTL 滑窗);达 5 次锁定登录。解锁(UnlockLogin)/重置密码清空本键。
        /// WHY 本常量:此前键仅 AuthService.ErrorKey 私有用,批 A 解锁/重置需跨服务清键,统一单点防漂移(约定 #11)。
        /// </summary>
        public const string KeyPasswordError = "sys_password_error_times:";

        /// <summary>
        /// 账号级 OTP 失败计数键前缀(task_007;完整键 = 前缀 + 账号,如 sys_otp_error_times:admin)。
        /// 值 = 窗口内 OTP 连续失败次数(30min TTL);达 10 次锁定两阶段登录。解锁(UnlockLogin)清空本键。
        /// </summary>
        public const string KeyOtpError = "sys_otp_error_times:";

        /// <summary>
        /// 系统配置缓存键前缀(批 D 4.1;完整键 = 前缀 + 配置 Code,如 sys_config:sys.user.resetPwd.default)。
        /// 值 = 配置项 Value 字符串;读路径(SysConfigService.GetConfigValueByCodeAsync)未命中回源回填,
        /// 写路径(Update/Delete)变更即失效(删键),TTL 7 天仅防泄漏兜底(同 KeyUserApiPerm 口径)。
        /// </summary>
        public const string KeyConfig = "sys_config:";

        /// <summary>
        /// 幂等/防重复提交键前缀(批 D 1.13;完整键 = 前缀 + 身份:控制器:动作,如 sys_idempotent:123:SysConfig:Add)。
        /// 值 = 窗口内提交计数(long;IncrementAsync 原子自增,首个 1 持锁、>1 拒绝 429);
        /// TTL = [Idempotent] 特性 Seconds(窗口自首次提交起固定,不随重复续期)。
        /// </summary>
        public const string KeyIdempotent = "sys_idempotent:";

        /// <summary>
        /// 开放接口身份读缓存键前缀(批 E 1.6/1.7;完整键 = 前缀 + AccessKey,如 sys_open_access:open.intelcore)。
        /// 值 = 轻量身份 DTO(密钥/绑定用户/绑定租户快照,含绑定用户 Account/TokenVersion 等签名与 Claims 所需字段);
        /// 读路径(SysOpenAccessService.GetIdentityAsync)未命中回源回填,TTL 7 天兜底(同 KeyConfig 口径),
        /// 写路径(Update/Delete/ResetSecret)变更即失效(删键)——绑定用户停用/改密对开放接口即时生效靠写路径失效 +
        /// Handler 验签时不额外查用户行(信任快照)的口径见 SignatureAuthenticationHandler remarks。
        /// </summary>
        public const string KeyOpenAccess = "sys_open_access:";

        /// <summary>
        /// 开放接口签名防重放键前缀(批 E 1.6;完整键 = 前缀 + accessKey|timestamp|nonce)。
        /// 值 = null(存在性标记);TTL = 2 × 允许偏差窗口(默认 10min,略大于 5min 窗口保证窗口内重放必被拦,
        /// 参照 Handler 口径)。写入侧 SignatureAuthenticationHandler 验签通过后 SetAsync。
        /// </summary>
        public const string KeyOpenAccessNonce = "sys_open_access_nonce:";

        /// <summary>
        /// 在线用户 Hash 键(批 F 1.8/5.13):全局单键,field = ConnectionId,
        /// 值 = OnlineUserInfo(JSON)。TTL 24h 滑动续期(每次注册/注销刷新),防崩溃断连残留僵尸行。
        /// 参照 AdminNetPro KeyUserOnline 无冒号口径(Hash 单键结构,非前缀＋Id)。
        /// </summary>
        public const string KeyUserOnline = "sys_user_online";

        /// <summary>
        /// 用户表格列配置键前缀(批 A 4.12):完整键 = 前缀 + userId + ":" + gridId,
        /// 值 = ColumnCustomOutput(JSON)。TTL 7 天(对齐权限缓存口径),存/重置精准失效。
        /// 值参照 AdminNetPro KeyColumnCustom 原值 snake_case 口径。
        /// </summary>
        public const string KeyColumnCustom = "sys_column_custom:";

        /// <summary>在线用户租户分组前缀(批 F;SignalR 分组名 = 前缀＋TenantId,组内广播在线列表变动)。</summary>
        public const string GroupOnline = "GROUP_ONLINE_";

        /// <summary>在线用户表 TTL(批 F):24h 滑动续期(注册/注销/强踢处刷新)——崩溃断连僵尸行的自动清理兜底,单点常量防漂移。</summary>
        public static readonly TimeSpan OnlineUserTtl = TimeSpan.FromHours(24);

        /// <summary>
        /// 流水号取号锁键前缀(批 W 4.3;完整键 = 前缀 + 租户Id:类型,如 sys_serial_lock:1300000000001:order)。
        /// 值 = 取号临界区持锁计数(long;IncrementAsync 原子自增,首个 1 持锁、&gt;1 重试);
        /// 释放 = 临界区结束 finally RemoveAsync;TTL 5s 仅防进程异常后的锁残留泄漏(同 KeyJobLock 口径)。
        /// </summary>
        public const string KeySerialLock = "sys_serial_lock:";

        /// <summary>
        /// 队列/锁键前缀约定(批 AF 5.10):队列原语(<see cref="Cache.ICacheService.ListPushAsync"/> 等)与分布式锁
        /// (<see cref="Cache.ICacheService.LockTakeAsync"/>)的键建议以此前缀拼接(如 <c>queue:order:dispatch</c>),
        /// 与事件总线通道/业务 KV 键在**同一 Redis 实例**上隔离(测试版 Redis 6380 与 eventbus 频道共用,故前缀必须显式)。
        /// WHY 只给约定不给强制:原语接口收完整键(调用方可能要与既有键体系对齐),框架不擅自改写业务键。
        /// </summary>
        public const string KeyQueuePrefix = "queue:";

        /// <summary>
        /// 角色字段权限缓存键前缀(批 AH 2.9;完整键 = 前缀 + 角色 Id,另有 <c>前缀__managed</c> 存"受管表集合")。
        /// 值 = 该角色允许的"表:列"集合;TTL 7 天,授权变更即失效(与 KeyUserApiPerm 同口径)。
        /// 消费方:响应出口强制点 ColumnScopeFilter(Web.Core)。
        /// </summary>
        public const string KeyRoleTable = "sys_role_table:";

        /// <summary>
        /// 定时任务集群节点表键(批 AJ 5.16;Hash:field=实例Id → JSON{nodeTag,workerId,startedAt,lastBeat})。
        /// 调度器每轮心跳写入;读侧**过滤 lastBeat 超期(>90s)的僵尸节点**(进程崩溃后节点自动不可见,无需清理作业)。
        /// 另有超长 TTL(30 天)兜底防键泄漏。
        /// </summary>
        public const string KeyJobNode = "sys_job_node";

        /// <summary>
        /// 运行中任务表键(批 AJ 5.16;Hash:field=任务名 → JSON{instanceId,nodeTag,startedAt,trigger})。
        /// 任务开始写入、finally 删除 ⇒ 集群视图可看到"哪个实例正在跑哪个任务"。
        /// </summary>
        public const string KeyJobRunning = "sys_job_running";

        /// <summary>
        /// 定时任务「上次触发时刻」共享基准键前缀(批 AK;完整键 = 前缀 + 任务名,如 sys_job_last:clean_sys_log)。
        /// 值 = 最近一次触发时刻(DateTime,JSON 口径);写 = 触发裁决通过时 <c>SetAsync</c>(带 TTL,见
        /// <see cref="Job.JobScheduler"/> 的 LastTtlFor),**执行 finally 绝不删除本键**(删 = 基准丢失 = 重复触发回归)。
        /// WHY 本键:防重入锁(KeyJobLock)只保证「不同时跑」,不保证「同一轮不重复跑」——内存快照的 LastRunTime
        /// 每 60s(12 tick)才整表重载,任务执行完、锁释放后,在快照刷新前的每个 tick 都会被反复判到期并重复执行
        /// (批 AK 实测:5.1s 窗口内执行 11 次,期望 1)。本键是**跨实例统一的权威判据**,调度触发在防重入锁内
        /// 以它复检(消费点 <see cref="Job.JobScheduler"/>.ConfirmDueBySharedLastAsync);内存快照降级为快速筛选。
        /// 档位边界:Redis 档跨实例成立;Memory 档只在单进程内成立(同 KeyJobLock 口径,多实例必须 Redis 档)。
        /// </summary>
        public const string KeyJobLast = "sys_job_last:";

        /// <summary>
        /// 启动建表/种子集群锁键(批 AS 集群基线①;**单键无冒号**,同 KeyUserOnline/KeyJobNode 的"整槽一把"口径)。
        /// 取 = <c>ICacheService.LockTakeAsync(key, instanceToken, TTL 10min)</c>(原子 SET NX PX),
        /// 释放 = 执行完 <c>LockReleaseAsync</c>(仅同一 token 可解,防误释放他人锁);
        /// 取不到锁的实例**整段跳过** HealColumnCase/InitTables/InitSeeds/InitSeedData
        /// (消费点 <see cref="SqlSugar.StartupSchemaInitializer"/>)。
        /// WHY 需要:改造前每实例启动都跑一遍 CodeFirst DDL + 种子,多实例同刻并发 ALTER/CREATE/UPSERT
        /// 在 PG 上互等或直接 42701/42P07——现象是"随机某台启动降级、schema 半更新"。
        /// 档位边界:Redis 档跨实例成立;Memory 档键不跨进程 ⇒ 每台各自执行(同 <see cref="KeyJobLock"/> 口径,G18)。
        /// </summary>
        public const string KeyStartupSchema = "sys_startup_schema";

        /// <summary>
        /// 雪花 WorkerId 占用登记键前缀(批 AS 集群基线②;完整键 = 前缀 + WorkerId,如 <c>sys_worker_id:1</c>)。
        /// 值 = 占用序号(long;<c>IncrementAsync</c> 原子自增,首增者得 1 = 本实例首个占用,
        /// &gt;1 = 同 WorkerId 已被他实例占用 ⇒ LogWarning);TTL 10 分钟由登记方周期续期(缺省 5 分钟),
        /// 宿主正常停止主动删键,异常崩溃残留最迟 TTL 自愈(消费点 <see cref="SqlSugar.WorkerIdGuard"/>)。
        /// WHY 判据取 Increment 而非 LockTake:后者写的 token 两档都读不回身份,而本件只需"占到/没占到"这一档位无关判据。
        /// WHY 需要:主键由雪花生成,两个实例同 WorkerId ⇒ 同毫秒可生成同一 Id(运行期偶发主键冲突/串号,最难归因);
        /// 而 <c>Yit:WorkerId</c> 缺省恒为 1 ⇒ 零配置多实例必然全撞。
        /// 档位边界:Memory 档键不跨进程 ⇒ 恒判 Registered、永不告警(检测无意义,同 <see cref="KeyJobLock"/>,G18)。
        /// </summary>
        public const string KeyWorkerId = "sys_worker_id:";

        /// <summary>
        /// 服务器监控自报快照表(批 AT F3;**Hash 单键无冒号**,同 <see cref="KeyJobNode"/> 的"整表一把"口径):
        /// field = 实例标识(<c>{NodeTag}:{Pid}:{GUID}</c>,与 <c>KeyJobNode</c> 同源可对号),
        /// 值 = <c>ServerMetricsSnapshot</c> JSON(运行时/硬件/程序集三块采集原文 + reportAt)。
        /// 写 = 每实例每 30s 上报一次并刷新键 TTL 90s(消费点 <c>ServerMetricsReporter</c>);
        /// 读 = <c>SysServerNodeService.GetNodesAsync</c>,**死节点由读侧按 reportAt 超 90s 隐藏**(TTL 只防键泄漏);
        /// 宿主正常停止时本实例主动 <c>HashDeleteAsync</c> 摘自己那行。
        /// 档位边界:Memory 档键不跨进程 ⇒ 集群视图只看得到应答的那个实例(同 <see cref="KeyJobNode"/>,G18)。
        /// </summary>
        public const string KeyServerMetrics = "sys_server_metrics";
    }
}
