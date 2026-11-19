using System.ComponentModel;
using System.Reflection;
using AdminCore.Core.Cache;
using AdminCore.Core.SeedData;
using Microsoft.Extensions.Logging;
using SqlSugar;
// SqlSugar 自带同名 ICacheService ⇒ 短名歧义 CS0104(本批编译门实抓)：显式别名钉本仓缓存抽象(同 Program.cs 口径)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.SqlSugar
{
    /// <summary>
    /// 启动期 schema 初始化(建表 + 种子)的**集群安全入口**(批 AS):
    /// 多实例同时启动时,CodeFirst DDL(<c>HealColumnCase</c>/<c>InitTables</c>)与增量种子
    /// 由**取到分布式锁的那个实例**执行,其余实例整段跳过——把"每台都跑一遍 DDL"收敛为"每台只跑进程内件"。
    /// </summary>
    /// <remarks>
    /// WHY 需要本件(集群部署必现):改造前 <c>Program.cs</c> 让**每个实例启动都跑**
    /// <c>HealColumnCase → InitTables → InitSeeds → InitSeedData</c>,无跨实例协调。
    /// PostgreSQL 下并发 <c>ALTER TABLE ... ADD COLUMN</c> / <c>CREATE TABLE</c> 会互等锁,
    /// 或直接报"column already exists"(42701)/"relation already exists"(42P07),
    /// 种子缺行插入亦会主键冲突(并发实例各判"缺行"同时插)——现象是"滚动/全量重启时随机某台启动降级告警、schema 半更新"。
    /// 单实例部署下该缺陷不可见(只有一台),属"集群第一天就炸"的那类,故列为集群基线首批。
    ///
    /// 三态判据(本批契约 F2,逐条可判):
    /// ① 取到锁 ⇒ 执行 DDL + 种子,<c>finally</c> 释放;<see cref="StartupSchemaInitOutcome.Executed"/>。
    /// ② 取不到(他实例持锁)⇒ **整段跳过** + LogWarning;
    ///    <see cref="StartupSchemaInitOutcome.SkippedHeldByOther"/>。
    ///    跳过方不碰 DB:他实例正在建同一批表,重复建表只有冲突没有收益。
    /// ③ 缓存本身异常(不可达/超时)⇒ **照常执行** + LogWarning;
    ///    <see cref="StartupSchemaInitOutcome.ExecutedWithoutLock"/>。
    ///    WHY 保守执行而非跳过:单机/Memory 档不得因"锁不可用"而永不建表;
    ///    CodeFirst 增量本身幂等,并发 DDL 的风险小于"新库谁都没建表"的确定性故障。
    ///
    /// 只管 DDL + 种子段:宿主级进程内件(<c>SqlSugarFilter.Init</c>/本地化解析)
    /// **不入锁内**——它们不产生跨实例副作用,各实例都需要自己那一份。
    /// 例外是字段权限升级迁移:它要**表已存在**才有效,故由调用方按本件返回值裁决(见使用指南 §44)。
    ///
    /// 边界(登记不整改):
    /// - 锁只在**缓存档 = Redis** 时跨实例成立;Memory 档键不跨进程 ⇒ 每台照常各自执行(同差距清单 G18 口径)。
    /// - 跳过方与持锁方之间**没有就绪等待**:第二台可能在第一台建表完成前就开始对外服务,
    ///   期间该实例的 DB 请求可能报错——部署口径仍应滚动串行 + 过健康门(见使用指南 §44)。
    /// - 持锁实例若在 DDL 中途崩溃:锁残留至 <see cref="SchemaLockTtl"/> 过期,
    ///   其后任一实例重启即可补跑(本件只在启动时刻执行一次,不自动重试)。
    ///
    /// 异常口径:DDL/种子自身异常**原样上抛**(由 <c>Program.cs</c> 既有 try/catch 收敛为"启动降级"响亮告警,
    /// 存活与就绪解耦的 task_010 定案不变);本件只在"锁面"内部吞异常并降级(判据③)。
    /// </remarks>
    public static class StartupSchemaInitializer
    {
        /// <summary>
        /// 建表/种子锁 TTL 兜底(批 AS)= 10 分钟。取值依据:① 明显长于正常启动建表 + 种子耗时
        /// (实测量级数秒),留足慢库/大表余量;② 又不过长——持锁实例崩溃时,残留期内其他实例都只会跳过,
        /// 10 分钟后新启动的实例即可接管补跑;③ 正常路径 <c>finally</c> 立即释放,TTL 只兜"进程异常"。
        /// </summary>
        public static readonly TimeSpan SchemaLockTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 集群安全的启动 schema 初始化入口(建表 + 种子,锁内一次执行)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(<c>AddSqlSugar</c> 后的共享实例;建表按实体特性多库路由)。</param>
        /// <param name="cache">缓存抽象(锁载体;Redis 档跨实例,Memory 档仅本进程)。</param>
        /// <param name="logger">日志器(跳过/降级两态必须响亮告警,不静默)。</param>
        /// <param name="instanceToken">本实例标识(作锁 token;释放只认同一 token——防误释放他实例锁)。</param>
        /// <param name="entityAssemblies">实体所在程序集全集(漏传一侧即漏建;口径同 Program.cs 原注释)。</param>
        /// <returns>三态结果,供调用方裁决后续依赖"表已存在"的启动件。</returns>
        public static async Task<StartupSchemaInitOutcome> InitAsync(
            ISqlSugarClient db,
            ICacheService cache,
            ILogger logger,
            string instanceToken,
            params Assembly[] entityAssemblies)
        {
            var lockKey = CacheConst.KeyStartupSchema;
            var lockTaken = default(bool?); // null = 锁状态未知(缓存面异常,判据③)

            try
            {
                lockTaken = await cache.LockTakeAsync(lockKey, instanceToken, SchemaLockTtl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "启动建表锁获取失败(缓存不可用)——保守照常执行建表/种子:单机与 Memory 档不得因锁不可用而永不建表(判据③)");
            }

            if (lockTaken == false)
            {
                // 判据②:他实例正在执行同一批 DDL/种子,本实例整段跳过——不碰 DB,避免并发 ALTER 互等与 42701/42P07
                logger.LogWarning(
                    "启动建表/种子跳过:锁 {LockKey} 由其他实例持有(本实例 {InstanceToken})——schema 由持锁实例执行;滚动部署请等其完成后再放流量",
                    lockKey, instanceToken);
                return StartupSchemaInitOutcome.SkippedHeldByOther;
            }

            try
            {
                await RunSchemaAndSeedsAsync(db, logger, entityAssemblies);
                return lockTaken == true
                    ? StartupSchemaInitOutcome.Executed
                    : StartupSchemaInitOutcome.ExecutedWithoutLock;
            }
            finally
            {
                // 只有真取到锁才释放(判据③ 未取到锁,无权也无需释放);释放失败仅告警——残留由 TTL 兜底
                if (lockTaken == true)
                {
                    try
                    {
                        await cache.LockReleaseAsync(lockKey, instanceToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "启动建表锁释放失败(残留至 {Ttl} 兜底,其后启动的实例照常跳过直至过期)", SchemaLockTtl);
                    }
                }
            }
        }

        // 锁内实际执行的 schema 段(口径与既有 Program.cs 逐行等价,只做"收进可测单件"的位置迁移):
        // 列名大小写自愈必须在建表之前(否则 CodeFirst 另建双列)→ 建表 → 命令式种子 → 数据种子
        private static async Task RunSchemaAndSeedsAsync(ISqlSugarClient db, ILogger logger, Assembly[] entityAssemblies)
        {
            db.HealColumnCase(entityAssemblies);
            db.InitTables(entityAssemblies);
            logger.LogInformation("初始化表完成(列名自愈 + 增量/全量分流,按库路由)");

            await db.InitSeeds(entityAssemblies);
            db.InitSeedData(entityAssemblies);
            logger.LogInformation("种子执行完成(命令式 + 数据种子,主键缺行才插、既有行不覆写)");
        }
    }

    /// <summary>
    /// 启动 schema 初始化的三态结果(批 AS,判据 F2 的可断言形状):
    /// 调用方据此裁决"需要表已存在"的后续启动件(如字段权限升级迁移)。
    /// </summary>
    [Description("启动建表+种子结果")]
    public enum StartupSchemaInitOutcome
    {
        /// <summary>① 本实例取到锁并执行了建表 + 种子(锁已释放)。</summary>
        [Description("本实例已执行")]
        Executed,

        /// <summary>② 他实例持锁,本实例整段跳过(未碰 DB)。</summary>
        [Description("他实例持锁跳过")]
        SkippedHeldByOther,

        /// <summary>③ 缓存不可用,锁状态未知 ⇒ 保守照常执行(单机/Memory 档降级面)。</summary>
        [Description("无锁执行")]
        ExecutedWithoutLock
    }
}
