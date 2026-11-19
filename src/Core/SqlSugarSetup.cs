using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions; // .NET 10:TryAdd* 扩展方法迁入 Extensions 子命名空间
using Microsoft.Extensions.Logging;
using SqlSugar;
using AdminCore.Core.SqlSugar;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Repository;
using AdminCore.Core.SeedData;
using AdminCore.Core.Security;

namespace AdminCore.Core
{
    /// <summary>
    /// SqlSugar 初始化(零 Furion 原生版):雪花主键 + 多库连接(ConnectionConfigs) + AOP(日志/审计/租户自动填充)
    /// + CodeFirst 增量建表(按库路由,EnableIncreTable 分流) + 增量种子(数据种子缺行插入、已有跳过 + 存量命令式种子) + 软删全局过滤器。
    /// </summary>
    public static class SqlSugarSetup
    {
        // 当前用户 Claim 名约定(task_005 定稿):与 JWT 签发共用 JwtClaimNames 单点定义——
        // 审计用户 Id 读 UserId,审计用户名字段读 Account(账号唯一不可变,作审计身份;姓名可重复/可变更不作身份)

        // 差异日志 JSON 序列化选项(批 X 5.4):camelCase+宽松转义(同 EventExecutor 口径);
        // JsonSerializerOptions 线程安全可共享。WHY 类级而非局部函数内 static:C# 局部函数不允许 static 局部变量。
        private static readonly JsonSerializerOptions DiffLogJsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 建表模式开关(appsettings DbSettings:EnableIncreTable,缺省 true=增量):
        /// true 只处理 [SugarTable]+[IncreTable] 实体;false 全量处理所有 [SugarTable] 实体(首次部署口径)。
        /// AddSqlSugar 时从配置读取、InitTables 时消费——配置是开关唯一入口,调用方不感知。
        /// </summary>
        public static bool EnableIncreTable { get; private set; } = true;

        /// <summary>
        /// 流水号填充服务解析失败是否已告警(批 AE):该解析在**每次插入**的 AOP 分支调用,
        /// 逐次告警会形成日志风暴 ⇒ 进程内只记一次(首次失败记 Warning,其后静默降级)。
        /// </summary>
        private static volatile bool SerialResolveWarned;

        /// <summary>
        /// 逐连接独立单连接 scope(ConfigId → scope)。
        /// WHY 不直接用多连接 scope 的 GetConnection:5.1.4.217 的 GetConnection 按线程/按次返回新
        /// SqlSugarProvider 实例,构造期挂的 AOP 配置不跟随 → 二级库(日志库)插入时审计填充缺失。
        /// 独立单连接 scope 的构造配置作用于自身且线程安全。AddSqlSugar 时填充。
        /// WHY 并发字典(批 AB):租户库连接在运行期按租户懒加载注册/失效(见 TenantDbRegistry),
        /// main/log 为注册期一次性写入、租户库为运行期追加——读写并发安全由并发字典承担。
        /// </summary>
        internal static System.Collections.Concurrent.ConcurrentDictionary<string, ISqlSugarClient> ConnectionClients { get; private set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // 构造期填充用:整体替换注册表实例(AddSqlSugar 单线程调用,无并发读)
        private static void SetConnectionClients(
            System.Collections.Concurrent.ConcurrentDictionary<string, ISqlSugarClient> clients)
            => ConnectionClients = clients;

        /// <summary>
        /// 实体程序集登记簿(批 AB C4):启动期 InitTables/HealColumnCase/InitSeedData 扫过的程序集在此登记,
        /// 租户库建表/种子复用同一批程序集——Core 不能反向引用 Business 程序集(依赖方向单向 Web.Core→Core),
        /// 故"业务程序集"只能由宿主在启动时登记一次(Program.cs 传 Core+Business 两个程序集)。
        /// </summary>
        private static readonly List<Assembly> RegisteredAssemblies = new();

        /// <summary>登记实体程序集(去重,幂等)。</summary>
        private static void RegisterAssemblies(IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
            {
                if (!RegisteredAssemblies.Contains(assembly))
                {
                    RegisteredAssemblies.Add(assembly);
                }
            }
        }

        /// <summary>
        /// 解析扫描程序集:显式传入优先(并登记),缺省回落登记簿,登记簿为空时回落 Core 自身。
        /// 建表/种子/租户库三处同口径(约定 #11 单点)。
        /// </summary>
        private static Assembly[] ResolveAssemblies(Assembly[] assemblies)
        {
            if (assemblies.Length > 0)
            {
                RegisterAssemblies(assemblies);
                return assemblies;
            }

            return RegisteredAssemblies.Count > 0
                ? RegisteredAssemblies.ToArray()
                : new[] { typeof(SqlSugarSetup).Assembly };
        }

        /// <summary>
        /// 注册 SqlSugar 到 DI(单例 ISqlSugarClient),挂雪花主键、AOP 日志/审计与软删全局过滤器。
        /// </summary>
        /// <param name="services">服务集合。</param>
        /// <param name="config">配置根(读 ConnectionConfigs 多库连接 / 存量 ConnectionStrings:Default、Yit:WorkerId、DbSettings:EnableIncreTable、DbSettings:SlowSqlSeconds)。</param>
        public static void AddSqlSugar(this IServiceCollection services, IConfiguration config)
        {
            // ① 雪花:工作节点 Id 从配置读取(默认 1)并初始化生成器;
            //    SqlSugar 全局自定义主键函数 → YitIdHelper,实体 [SugarColumn(IsIdentity=false)] 时生效
            var workerId = 1;
            if (int.TryParse(config["Yit:WorkerId"], out var parsedWorkerId))
            {
                workerId = parsedWorkerId;
            }

            // 批 BC:二级缓存键登记簿——**单例**(适配器与跨实例失效订阅者必须共用同一实例才能清到同一批键;
            // 未配缓存/未接总线的宿主里它空转无害)。适配器侧经工厂闭包 GetService 取同一实例。
            services.TryAddSingleton<SqlSugarCacheKeyRegistry>();

            YitIdHelper.Init(workerId);
            StaticConfig.CustomSnowFlakeFunc = () => YitIdHelper.NextId();

            // 建表模式开关(appsettings DbSettings:EnableIncreTable,缺省 true=增量)
            EnableIncreTable = config.GetValue("DbSettings:EnableIncreTable", true);

            // 慢 SQL 阈值(批 J 5.5;appsettings DbSettings:SlowSqlSeconds,缺省 5 秒,<=0=关闭检测;裁决 g4 §5 ①)
            var slowSqlSeconds = config.GetValue("DbSettings:SlowSqlSeconds", 5.0);

            // 库表差异日志开关(批 X 5.4;appsettings DbSettings:EnableDiffLog,缺省 false=关闭):
            // 开启时每次写操作经 Aop.OnDiffLogEvent 落 sys_log_diff(操作前/后整行快照)。
            // WHY 启动读取而非消费点 IOptions 解析:与 EnableIncreTable/SlowSqlSeconds 散读同口径——
            // 开关是部署级决策(缺省关闭),测试容器经内存配置控制即够;消费点动态解析每次写多一次 DI 查找。
            var enableDiffLog = config.GetValue("DbSettings:EnableDiffLog", false);

            // AOP 审计填充需要当前请求用户:注册 IHttpContextAccessor。
            // WHY 用 TryAdd:Web 宿主已注册(宿主实例由中间件设置 HttpContext),重复注册会用
            // 未设置的空实例遮蔽它导致取不到用户;非 Web 宿主(单测/后台)未注册才由这里补上。
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // ②③ SqlSugarScope 构建(多库) + 单例注册。
            //    多库通用选库(task_008):ConnectionConfigs 数组(main 必备 + log 可选 + 可扩任意 ConfigId);
            //    无 ConnectionConfigs 时兼容存量单库配置 ConnectionStrings:Default(既有测试/部署不受影响)。
            //    WHY 用工厂重载而非直接注册实例:ILogger/IHttpContextAccessor 只能从已构建的容器解析,
            //    AddSqlSugar 阶段容器尚未 Build,工厂在首次解析时取依赖。
            var connectionConfigs = LoadConnectionConfigs(config);
            services.AddSingleton<ISqlSugarClient>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SqlSugar");
                var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();

                // 自定义数据过滤器扩展点(批 P 3.3):DI 解析 IDataExecuting 实现列表([Service] 自动注册),
                // 闭包捕获(每容器独立,零静态态),供 Aop.DataExecuting 钩子在框架内建填充之后按实体类型分发
                var executingItems = sp.GetServices<IDataExecuting>().ToList();

                // 流水号填充(批 W 4.3/3.4):[BindSerial] 标注的字符串属性插入时自动取号填入。
                // WHY 消费点延迟解析 + CreateScope 而非工厂闭包内解析/直接 root 解析(批 W 三重实证):
                // ① 闭包内解析:SysSerialService 经 SqlSugarRepository 传递依赖 ISqlSugarClient(即本工厂正在
                //    构建的单例)→ 循环依赖死锁(最小宿主 GetRequiredService<ISqlSugarClient> 挂起);
                // ② root 直接解析 Scoped 服务 → 宿主启动即抛 InvalidOperationException(审计插入失败隔离);
                // ③ CreateScope:scoped 链在 scope 内完整合法;每次插入建/弃 scope 的开销对写路径可接受
                //    (scope 只持有本服务解析链,无长生命周期资源);
                // GetService 非 GetRequiredService:未注册缓存的容器(既有最小测试宿主)无此服务 → null 即不挂填充
                // (批 R 缓存同口径降级)。
                Func<AdminCore.Core.Services.Serial.SysSerialService?> resolveSerialService = () =>
                {
                    // 批 AE(G12 修复):此前直接 GetService<SysSerialService>() —— 但 MS.DI 对"**类型已注册、其 ctor 依赖解析失败**"
                    // 会**抛 InvalidOperationException** 而非返回 null(实测:容器有 AddAutoServices(注册了 SysSerialService,
                    // 其 ctor 依赖 ICacheService)但**没有** AddCache 时抛异常;而本解析在 Aop.DataExecuting 的 Insert 分支
                    // **每次插入**都调用 ⇒ 该容器口径下任何插入都炸,与注释承诺的"未注册缓存 → 降级不填充"不符)。
                    // 修法两步:①先探测缓存抽象(无缓存 = 无取号载体,直接不填充,零解析开销);
                    // ②再 try/catch 兜底——解析失败记**一次**告警并降级 null(防"每次插入一条"的告警风暴)。
                    // 口径边界(批 AE 测试实测后明确,两种"失败"勿混为一谈):
                    //  · **依赖解析失败**(本方法兜住)→ 降级不填充:流水号机制本身不可用,同无缓存宿主口径;
                    //  · **取号失败**(FillBindSerialProperties 内 NextSeqNoAsync,**不在此兜**)→ fail-loud 冒泡:
                    //    属部署/配置缺陷(类型未配置/已停用/超 Max),静默写空会让业务字段悄悄缺号 ⇒ 宁可拦下插入。
                    //    这是刻意选择,不是遗漏(消费方若要"缺配置也放行",应在业务层显式处理,而非框架静默)。
                    // 语义耦合登记:以"ICacheService 是否存在"作"能否取号"的代理判据;若将来 SysSerialService 去缓存化,
                    // 本探测需同步调整。
                    if (sp.GetService<AdminCore.Core.Cache.ICacheService>() is null)
                    {
                        return null;
                    }

                    try
                    {
                        using var scope = sp.CreateScope();
                        return scope.ServiceProvider.GetService<AdminCore.Core.Services.Serial.SysSerialService>();
                    }
                    catch (Exception ex)
                    {
                        if (!SerialResolveWarned)
                        {
                            SerialResolveWarned = true;
                            logger.LogWarning(ex, "流水号填充服务解析失败,已降级为不填充(插入不受影响;仅记一次,见 G12 修复说明)");
                        }

                        return null;
                    }
                };

                // ⑤ 二级缓存适配器(批 R 3.5,参照 SqlSugarCache 移植):SqlSugar 数据缓存接口委托本仓
                //    ICacheService(缓存抽象,Singleton——单例 scope 持有安全);
                //    查询 opt-in 启用(.WithCache()),框架层零默认启用;增删改自动失效靠 IsAutoRemoveDataCache。
                // WHY GetService 而非 GetRequiredService:二级缓存是可选增强(AddCache 独立扩展)——
                // 未注册缓存的容器(既有测试容器/最小宿主)不挂适配器,行为与批 R 前完全一致
                // (IsAutoRemoveDataCache 对 SqlSugar 内建空缓存无害;业务 WithCache 查询在
                // 无缓存实现的宿主不会启用)
                var cacheService = sp.GetService<AdminCore.Core.Cache.ICacheService>();
                if (cacheService is not null)
                {
                    var dataCacheService = new SqlSugarDataCacheService(
                        cacheService,
                        sp.GetService<SqlSugarCacheKeyRegistry>(),
                        sp.GetService<AdminCore.Core.EventBus.IEventPublisher>(),
                        sp.GetService<ILogger<SqlSugarDataCacheService>>());
                    foreach (var cc in connectionConfigs)
                    {
                        cc.ConfigureExternalServices = new ConfigureExternalServices
                        {
                            DataInfoCacheService = dataCacheService
                        };
                    }
                }

                // 逐连接配置:直接配置底层 SqlSugarProvider(AOP 日志/审计自动填充/软删过滤各库一致);
                // 连接级行为差异(如按库日志级别)后续按需在此扩展
                void ConfigureProvider(SqlSugarProvider db)
                {
                    // ④ AOP 日志:错误记错误 SQL(5.1.4.217 的 OnError 为单参 Action<SqlSugarException>,
                    //    出错 SQL 挂在异常对象的 Sql 属性上);执行 SQL 打日志(开发期排查用,生产可调日志级别关闭)
                    db.Aop.OnError = ex =>
                    {
                        logger.LogError(ex, "SQL 执行出错: {Sql}", ex.Sql);
                    };

                    db.Aop.OnLogExecuting = (sql, pars) =>
                    {
                        logger.LogDebug("执行 SQL: {Sql}", sql);
                    };

                    // ④-b 慢 SQL 检测(批 J 5.5,参照 SetDbAop OnLogExecuted 移植):
                    //    执行时间超过阈值 → Warning 记原生 SQL(参数内联)＋堆栈首帧(文件名/行号/方法名)定位业务调用点;
                    //    不落独立表(诊断信号非审计件,裁决 g4 §5 ④)。主/日志库逐连接同口径。
                    db.Ado.CommandTimeOut = 30;
                    db.Aop.OnLogExecuted = (sql, pars) =>
                    {
                        if (slowSqlSeconds <= 0) return;
                        if (db.Ado.SqlExecutionTime.TotalSeconds <= slowSqlSeconds) return;

                        // SqlSugar 堆栈构造时已过滤框架自身帧,首帧即业务调用点(裁决 g4 §5 ⑤)
                        var file = db.Ado.SqlStackTrace?.FirstFileName ?? "?";
                        var line = db.Ado.SqlStackTrace?.FirstLine;
                        var method = db.Ado.SqlStackTrace?.FirstMethodName ?? "?";
                        logger.LogWarning(
                            "慢 SQL（{Elapsed:F2} 秒 > 阈值 {Threshold:F1} 秒）| 调用点 {File}:{Line} {Method} | 库 {ConfigId} | SQL: {Sql}",
                            db.Ado.SqlExecutionTime.TotalSeconds, slowSqlSeconds, file, line, method,
                            db.CurrentConnectionConfig?.ConfigId,
                            global::SqlSugar.UtilMethods.GetNativeSql(sql, pars));
                    };

                    // ⑤ AOP 审计自动填充(DataExecuting):业务代码只管业务字段,
                    //    主键/创建/更新审计字段/租户字段在 SQL 生成前由框架统一填(参考文档 2.1「AOP 三段」之数据执行段)。
                    //    约定:只处理单对象操作(Insertable(obj)/Updateable(obj));批量 List 操作 M1 不在范围
                    //    (契约口径),如业务需要再在此补列表遍历。
                    db.Aop.DataExecuting = (value, data) =>
                    {
                        switch (data.OperationType)
                        {
                            case DataFilterType.InsertByObject:
                                // 主键为空 → 雪花(非空保留:支持业务自定义主键)
                                if (data.EntityValue is EntityBaseId insertEntityId && insertEntityId.Id == 0)
                                {
                                    insertEntityId.Id = YitIdHelper.NextId();
                                }

                                if (data.EntityValue is EntityBase insertEntity)
                                {
                                    if (insertEntity.CreateTime == null)
                                    {
                                        insertEntity.CreateTime = DateTime.Now;
                                    }

                                    // 无当前用户(后台任务/单测/登录未接)则跳过用户字段填充
                                    var createUserId = CurrentUserId(httpContextAccessor);
                                    if (createUserId.HasValue)
                                    {
                                        insertEntity.CreateUserId = createUserId;
                                        insertEntity.CreateUserName = CurrentUserName(httpContextAccessor);
                                    }
                                }

                                // 租户写入自动填(租户基座):插入时 TenantId 为空 → 当前请求租户 Claim 值,
                                // 缺省 DefaultTenantId(对齐 EntityTenant 三层机制之写入层)
                                if (data.EntityValue is ITenantIdFilter tenantEntity && tenantEntity.TenantId is null)
                                {
                                    var tenantClaim = CurrentTenantId(httpContextAccessor);
                                    tenantEntity.TenantId = tenantClaim ?? SqlSugarConst.DefaultTenantId;
                                }

                                // 机构数据权限写入自动填(task_013):插入时 CreateOrgId 为空 → 当前用户所属机构。
                                // WHY 查库而非 Claim:JWT 不含 OrgId(不新增 Claim,避免令牌结构变更);
                                // 无用户上下文(后台/测试) → 列留空 = 存量数据语义(机构范围 fail-closed 不可见)
                                if (data.EntityValue is IOrgIdFilter orgEntity && orgEntity.CreateOrgId is null)
                                {
                                    orgEntity.CreateOrgId = CurrentUserOrgId(httpContextAccessor);
                                }

                                // 流水号自动填充(批 W 4.3/3.4,参照 AdminNetPro SetSerialProperty 移植):
                                // [BindSerial] 标注的字符串属性为 null → 自动取号填入(已有值不覆盖,业务显式赋值优先);
                                // 无填充服务(容器未注册缓存)或属性集为空 → 零开销直通。
                                var serial = resolveSerialService();
                                if (serial is not null)
                                {
                                    FillBindSerialProperties(serial, data);
                                }

                                break;

                            case DataFilterType.UpdateByObject:
                                if (data.EntityValue is EntityBase updateEntity)
                                {
                                    updateEntity.UpdateTime = DateTime.Now;

                                    // 软删语义:软删走 UpdateByObject 路径(IsDelete 更新为 true),
                                    // 此处同步填删除时间,保证审计三元组(人/时间/标记)完整
                                    if (updateEntity.IsDelete && updateEntity.DeleteTime == null)
                                    {
                                        updateEntity.DeleteTime = DateTime.Now;
                                    }

                                    var updateUserId = CurrentUserId(httpContextAccessor);
                                    if (updateUserId.HasValue)
                                    {
                                        updateEntity.UpdateUserId = updateUserId;
                                        updateEntity.UpdateUserName = CurrentUserName(httpContextAccessor);
                                    }
                                }

                                // 更新机构写入自动填(task_013):更新时 UpdateOrgId 为空 → 当前用户所属机构
                                // (仅 EntityBaseUpdateData 有本字段,口径同创建侧)
                                if (data.EntityValue is EntityBaseUpdateData updateOrgEntity && updateOrgEntity.UpdateOrgId is null)
                                {
                                    updateOrgEntity.UpdateOrgId = CurrentUserOrgId(httpContextAccessor);
                                }

                                break;
                        }

                        // 自定义数据过滤器分发(批 P 3.3,参照 SqlSugarDataExecuting.Execute 接通——
                        // 参照挂钩行为注释死代码,本仓接通闭环,裁决 g10 §5 ①):
                        // 所有操作类型均分发,自定义实现自行按 OperationType 分支;无实现时零开销
                        SqlSugarDataExecuting.Execute(executingItems, value, data);
                    };

                    // ⑥ 全局软删过滤器:实现 IDeletedFilter 的实体,查询/更新/删除自动追加 IsDelete=false。
                    //    WHY 只在此注册软删过滤器而租户过滤器不在此注册:软删是全库全请求恒定语义,
                    //    租户过滤是按请求变化(Claim/超管豁免/无 Claim 跳过)——若在此用静态闭包注册,
                    //    单例 Scope 的过滤状态无法随请求切换;租户过滤改由 PermissionMiddleware 按请求装配/清除。
                    db.QueryFilter.AddTableFilter<IDeletedFilter>(u => u.IsDelete == false);

                    // ⑦ 库表差异日志(批 X 5.4,参照 Admin.NET SetDbDiffLog 移植):开启时每次写操作
                    //    (增/删/改)捕获操作前/后整行快照＋SQL/参数/耗时落 sys_log_diff([LogTable] 路由日志库)。
                    //    口径定案(参照差异 4 点,WHY 留痕):
                    //    a) SqlSugar 5.1.4.x 的 OnDiffLogEvent = Action<DiffLogModel>(同步回调,214/217/219 同构)——
                    //       参照 async lambda 实为 void fire-and-forget;本仓同 fire-and-forget 但补 catch+LogError
                    //       (参照 unobserved 静默吞;审计扩展件失败不得影响主写,同批 S「审计写失败只告警不阻断」口径)。
                    //    b) 自回环防护:目标表为 sys_log_diff 自身则跳过(参照原语义;本仓软删口径删除=UPDATE,
                    //       AfterData 恒有值,判 AfterData 即够)。
                    //    c) 写入选库经 SelectConfigIdFor/GetConnectionById(仓储同单点,#11):
                    //       不用 ITenant.GetConnectionScope+CopyNew(参照口径)——本仓逐连接独立 scope 下
                    //       ConnectionClients 的 client 才带 AOP,差异日志行审计字段(CreateUserId/TenantId/CreateTime)
                    //       自动填充依赖之(参照 CopyNew 无 AOP,审计字段缺失,本仓优于参照)。
                    //    d) 事务语义:业务写日志库=差异日志同连接同事务(回滚则不留差异日志,只记已提交写);
                    //       业务写主库=差异日志独立连接独立提交(与参照同)。
                    if (enableDiffLog)
                    {
                        db.Aop.OnDiffLogEvent = diff =>
                        {
                            _ = WriteDiffLogAsync(db, logger, diff);
                        };
                    }
                }

                // 差异日志写入(批 X 5.4):fire-and-forget 的落库主体(见上方 ⑦ 口径定案)。
                // 217 版 DiffLogTableInfo 为 TableName/TableDescription/Columns 直属性(参照 214 同口径)。
                static async Task WriteDiffLogAsync(SqlSugarProvider db, ILogger logger, DiffLogModel diff)
                {
                    try
                    {
                        // 自回环防护(口径 b)。WHY 字面量 "sys_log_diff" 而非 nameof(SysLogDiff):
                        // 本仓表名显式小写([SugarTable("sys_log_diff")]),nameof 得 PascalCase 类名会失配;
                        // 参照仓表名=类名故其 nameof 写法正确,直接照抄即坑。
                        // SqlSugar 217 DELETE 档 AfterData 恒 null(行已删无操作后快照)且 Before/After
                        // 任一为 null 时 FirstOrDefault 会抛 ArgumentNullException——两侧都判
                        // (DELETE 档靠 BeforeData 命中;优于参照仅判 AfterData 的缺口)。
                        var afterTable = diff.AfterData?.FirstOrDefault()?.TableName;
                        var beforeTable = diff.BeforeData?.FirstOrDefault()?.TableName;
                        if (afterTable == "sys_log_diff" || beforeTable == "sys_log_diff")
                        {
                            return;
                        }

                        var logDiff = new SysLogDiff
                        {
                            AfterData = JsonSerializer.Serialize(diff.AfterData, DiffLogJsonOpts),
                            BeforeData = JsonSerializer.Serialize(diff.BeforeData, DiffLogJsonOpts),
                            BusinessData = diff.BusinessData == null
                                ? (afterTable ?? beforeTable)
                                : JsonSerializer.Serialize(diff.BusinessData, DiffLogJsonOpts),
                            DiffType = diff.DiffType.ToString().ToUpper(),
                            Sql = diff.Sql,
                            Parameters = JsonSerializer.Serialize(
                                diff.Parameters.Select(e => new { e.ParameterName, e.Value, TypeName = e.DbType.ToString() }),
                                DiffLogJsonOpts),
                            Elapsed = diff.Time == null ? 0 : (long)diff.Time.Value.TotalMilliseconds
                        };

                        // 选库(口径 c):[LogTable] → log(未配置回落 main)。
                        // WHY 不直接调 SelectConfigIdFor:AOP 回调闭包的 db 是逐连接 SqlSugarProvider
                        // (ConfigureProvider 入参),SelectConfigIdFor 内 (SqlSugarScope)db 转型会抛
                        // InvalidCastException。ConnectionClients 与 SelectConfigIdFor 同源(注册表即已配置
                        // 连接),直接查注册表判定与选库单点语义一致。
                        var configId = ConnectionClients.ContainsKey(SqlSugarConst.LogConfigId)
                            ? SqlSugarConst.LogConfigId
                            : SqlSugarConst.MainConfigId;
                        var logDb = GetConnectionById(db, configId);
                        await logDb.Insertable(logDiff).ExecuteCommandAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "差异日志写入失败(主写不受影响)");
                    }
                }

                // 构造函数 action 的参数是包装类 SqlSugarClient(5.1.4.217 类型体系:
                // SqlSugarClient.Context 为底层 SqlSugarProvider,Provider 只实现 ISqlSugarClient 接口)
                var sqlSugar = new SqlSugarScope(connectionConfigs, wrapper => ConfigureProvider(wrapper.Context));

                // 独立单连接 scope 工厂(批 AB 提取单点):main/log(注册期)与租户库(运行期懒加载)同口径——
                // 都经本方法构造,保证 AOP/软删过滤/审计填充/二级缓存逐连接一致(见 ConnectionClients WHY)
                ISqlSugarClient BuildClient(ConnectionConfig cfg)
                    => new SqlSugarScope(cfg, wrapper => ConfigureProvider(wrapper.Context));

                // WHY 每连接另建独立单连接 scope(而非用多连接 scope 的 GetConnection):
                // 5.1.4.217 多连接 scope 的 GetConnection 按线程/按次返回新 SqlSugarProvider 实例
                // (实机诊断:启动期配置实例 hash ≠ 请求期实例 hash,构造配置不跟随 → SysLogOp 落日志库时
                // 雪花主键/CreateTime 未填充,NOT NULL 违约致操作日志丢失)。单连接 scope 的构造配置
                // 作用于自身且线程安全(主连接 Phase G 全链路已验证),GetConnectionById 统一供给。
                var clients = new System.Collections.Concurrent.ConcurrentDictionary<string, ISqlSugarClient>(StringComparer.OrdinalIgnoreCase);
                foreach (var cfg in connectionConfigs)
                {
                    // ConfigId 声明为 object(兼容长整型库标识),本项目约定恒为 string;
                    // 恒非空——LoadConnectionConfigs 已以 IsNullOrWhiteSpace 过滤空 ConfigId,故用 null-forgiving
                    clients[(cfg.ConfigId as string)!] = BuildClient(cfg);
                }
                SetConnectionClients(clients);

                // ⑧ 租户库注册表装配(批 AB C2/C3 多租户库级隔离):容器侧依赖(解密器/日志/配置开关)
                //    与注册委托一次性注入——委托新建独立单连接 scope 并登记进注册表(与 main/log 同口径;
                //    二级缓存适配器亦挂上,租户库查询 opt-in .WithCache() 行为一致)。
                //    WHY 委托而非直接暴露 ConnectionClients:租户库连接须"构造即配置 AOP",而 ConfigureProvider
                //    是本工厂闭包(捕获 logger/阈值/执行器列表/缓存),只能在容器解析后成型(同批 P/R 口径)。
                //    WHY 解密器解析失败只告警不冒泡(批 AC 实测修正):CryptogramHelper 构造在缺 Cryptogram:Key 时抛异常
                //    (如直接 dotnet <dll> 启动、ContentRoot 无 appsettings.json 的场景),若在此冒泡会**让整个宿主
                //    SqlSugar 解析失败/启动 abort**——而解密只在 EnableConnEncrypt=true 且真有租户库时才需要。
                //    降级为 null 后,真正需要解密时由 DecryptConnection 明确拒绝(AppException),fail-loud 语义不变。
                AdminCore.Core.Security.CryptogramHelper? cryptogram = null;
                try
                {
                    cryptogram = sp.GetService<AdminCore.Core.Security.CryptogramHelper>();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "解密器(CryptogramHelper)解析失败:租户库连接串解密不可用;" +
                                           "若开启 DbSettings:EnableConnEncrypt,租户库解析将明确报错(不影响主库/日志库)");
                }

                TenantDbRegistry.Init(
                    config.GetValue("DbSettings:EnableConnEncrypt", false),
                    cryptogram,
                    logger,
                    (configId, connectionString) =>
                    {
                        var tenantCfg = BuildPgConnectionConfig(configId, connectionString);
                        if (cacheService is not null)
                        {
                            tenantCfg.ConfigureExternalServices = new ConfigureExternalServices
                            {
                                // 批 BC:与主库同一登记簿单例 + 可选发布器(租户库查询 opt-in 缓存同口径)
                                DataInfoCacheService = new SqlSugarDataCacheService(
                                    cacheService,
                                    sp.GetService<SqlSugarCacheKeyRegistry>(),
                                    sp.GetService<AdminCore.Core.EventBus.IEventPublisher>(),
                                    sp.GetService<ILogger<SqlSugarDataCacheService>>())
                            };
                        }

                        var client = BuildClient(tenantCfg);
                        ConnectionClients[configId] = client; // 并发字典索引器=覆盖(重复注册幂等)
                        return client;
                    });

                return sqlSugar;
            });

            // ⑦ 通用仓储:Scoped(每请求一实例),构造时注入上面的 ISqlSugarClient 单例;
            //    仓储实例无状态(仅持有客户端引用),单例客户端的连接自管,Scoped 不引入额外资源
            //    接口与具体类型都注册:业务服务注入接口(ISqlSugarRepository<>),测试/内部按具体类型取
            services.AddScoped(typeof(ISqlSugarRepository<>), typeof(SqlSugarRepository<>));
            services.AddScoped(typeof(SqlSugarRepository<>));
        }

        /// <summary>
        /// 加载多库连接配置:ConnectionConfigs 数组(ConfigId + ConnectionString,可扩任意数量库);
        /// 无 ConnectionConfigs → 兼容存量单库配置 ConnectionStrings:Default(作为 main 库)。
        /// 数组元素缺 ConfigId 或 ConnectionString 为空 → 跳过(日志库未配置 = 不注册该 ConfigId,选库侧回落主库)。
        /// </summary>
        private static List<ConnectionConfig> LoadConnectionConfigs(IConfiguration config)
        {
            // 手工枚举配置节(不用 Binder 包的 Get<T>():Core 只引用 Abstractions,零新增依赖)
            var entries = new List<ConnectionConfig>();
            foreach (var child in config.GetSection("ConnectionConfigs").GetChildren())
            {
                var configId = child["ConfigId"];
                var connectionString = child["ConnectionString"];
                if (string.IsNullOrWhiteSpace(configId) || string.IsNullOrWhiteSpace(connectionString))
                {
                    continue;
                }

                entries.Add(BuildPgConnectionConfig(configId, connectionString));
            }

            if (entries.Count > 0)
            {
                // main 库必备(缺 main = 配置错误,启动快速失败;静默兜底会让全系统数据落错库且无感知)。
                // WHY as string:SqlSugar 的 ConnectionConfig.ConfigId 声明为 object(兼容长整型库标识),
                // 本项目约定 ConfigId 恒为 string
                if (entries.All(c => !string.Equals(c.ConfigId as string, SqlSugarConst.MainConfigId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("ConnectionConfigs 缺少主库配置(ConfigId=main)");
                }

                return entries;
            }

            // 存量配置兼容(task_003 及更早):ConnectionStrings:Default 作为唯一主库
            var defaultConn = config["ConnectionStrings:Default"];
            if (string.IsNullOrWhiteSpace(defaultConn))
            {
                throw new InvalidOperationException("缺少数据库配置:ConnectionConfigs 与 ConnectionStrings:Default 均未配置");
            }

            return new List<ConnectionConfig>
            {
                BuildPgConnectionConfig(SqlSugarConst.MainConfigId, defaultConn)
            };
        }

        /// <summary>
        /// 构造 PG 连接配置(单点):DbType/自动释放/特性主键/PG 精确大小写口径四处一致——
        /// main/log(配置装载)与租户库(运行期懒加载,批 AB)共用本方法,防两处口径漂移。
        /// </summary>
        internal static ConnectionConfig BuildPgConnectionConfig(string configId, string connectionString)
            => new()
            {
                ConfigId = configId,
                ConnectionString = connectionString,
                DbType = DbType.PostgreSQL,
                IsAutoCloseConnection = true, // 每次操作完自动释放,单例 Scope 下防连接泄漏
                InitKeyType = InitKeyType.Attribute, // 主键/列以 [SugarColumn] 特性为准
                MoreSettings = PgExactCaseSettings() // PG 标识符精确大小写(task_008 fixa),见方法头
            };

        /// <summary>
        /// PG 标识符精确大小写配置(task_008 fixa):关闭 SqlSugar 默认的自动小写
        /// (PgSqlIsAutoToLower 三个开关:DML / CodeFirst DDL / Schema 名)。
        /// WHY:PG 对未加引号的标识符统一折为小写,旧配置下生成的列名全小写(如 createtime),
        /// 而验收 SQL 用带引号的精确大小写列名(select "CreateTime" ...)——引号在 PG 中大小写敏感,
        /// 小写列与 "CreateTime" 不匹配即报 column does not exist。关闭后 SQL 标识符按实体
        /// 声明的精确大小写生成(加引号);存量库的小写列由 HealColumnCase 启动时一次性更名补齐。
        /// 实体约定:表名经 [SugarTable] 显式声明小写、列名默认取属性名(PascalCase),两者均原样引用。
        /// </summary>
        private static ConnMoreSettings PgExactCaseSettings()
            => new()
            {
                PgSqlIsAutoToLower = false,
                PgSqlIsAutoToLowerCodeFirst = false,
                PgSqlIsAutoToLowerSchema = false,
                // 二级缓存自动失效(批 R 3.5):所有增删改自动调 RemoveDataCache 删对应数据缓存键
                IsAutoRemoveDataCache = true
            };

        /// <summary>
        /// 实体 → 目标库 ConfigId(多库通用选库,仓储与建表共用单点):
        /// [Tenant] → 租户库(基座先定机制:租户库懒加载后补,基座期落主库);
        /// [LogTable] → 日志库(未配置时回落主库,对齐 baseadmin LogDb 静态取库);[SysTable]/无特性 → 主库。
        /// </summary>
        internal static string SelectConfigIdFor(ISqlSugarClient db, Type entity)
        {
            // 全限定:AdminCore.Core.Attributes.TenantAttribute 与 SqlSugar.TenantAttribute 同名,
            // 本文件 using 两个命名空间,短名歧义(CS0104)
            if (entity.GetCustomAttribute<AdminCore.Core.Attributes.TenantAttribute>() is not null)
            {
                // 批 AB C1 租户库路由(基座期留口在此闭环):当前执行流租户(TenantScope,HTTP 由
                // PermissionMiddleware 按 Claim 设定 / 后台任务经 Begin 显式切换)非空且非默认租户
                // → ConfigId = 租户 Id(常量约定见 SqlSugarConst);
                // 默认租户与无上下文(启动扫描建表/种子/单测) → 主库 = 既有行为零变化。
                var tenantId = TenantScope.Current;
                if (tenantId is null || tenantId.Value == SqlSugarConst.DefaultTenantId)
                {
                    return SqlSugarConst.MainConfigId;
                }

                // 已判定「不分库」的租户(未配置连接串/租户行缺失,由 EnsureConnectionAsync 在请求期登记)
                // → 主库:本条是"opt-in 未开启"与"未注册即 fail-loud"的分界线(批 AB 修订,
                // 否则存量未分库租户一旦出现 [Tenant] 实体,仓储构造就会抛异常 → 请求 500)
                if (TenantDbRegistry.IsMainDatabaseTenant(tenantId.Value))
                {
                    return SqlSugarConst.MainConfigId;
                }

                var tenantConfigId = TenantDbRegistry.ConfigIdForTenant(tenantId.Value);
                // fail-loud(C5):未注册即装配/配置缺陷(请求链由 PermissionMiddleware 先行 EnsureConnectionAsync,
                // 未注册=租户未配连接串或连接已失效);静默回落主库=租户数据落错库且无感知,绝不允许
                return ConnectionClients.ContainsKey(tenantConfigId)
                    ? tenantConfigId
                    : throw new InvalidOperationException(
                        $"租户库连接未注册(租户={tenantId.Value}, 库标识={tenantConfigId}):请先在租户登记中配置连接串并初始化租户库");
            }

            var configId = entity.GetCustomAttribute<LogTableAttribute>() is not null
                ? SqlSugarConst.LogConfigId
                : SqlSugarConst.MainConfigId;

            // 日志库连接未配置 → 回落主库(对齐 baseadmin LogDb 静态取库):
            // GetConnection 对未注册 ConfigId 直接抛异常,故先查已注册 ConfigId 列表再路由
            // (GetCurrentConfigIds 为 SqlSugarScope 具体类方法,接口无此成员)
            var registeredIds = ((SqlSugarScope)db).GetCurrentConfigIds();
            return registeredIds.Any(id => string.Equals(id, configId, StringComparison.OrdinalIgnoreCase))
                ? configId
                : SqlSugarConst.MainConfigId;
        }

        /// <summary>
        /// 取目标库连接(多库通用单点):返回 AddSqlSugar 时创建的逐连接独立单连接 scope
        /// (线程安全、AOP/软删过滤均已配置,见 ConnectionClients WHY)。
        /// 未注册 ConfigId 属配置错误——SelectConfigIdFor 已先行校验,此处兜底回落主库并记日志。
        /// </summary>
        internal static ISqlSugarClient GetConnectionById(ISqlSugarClient db, string configId)
        {
            return ConnectionClients.TryGetValue(configId, out var client)
                ? client
                : db;
        }

        /// <summary>
        /// 当前登录用户 Id(IHttpContextAccessor → User Claim UserId;无用户/无 Claim 返回 null)。
        /// </summary>
        private static long? CurrentUserId(IHttpContextAccessor accessor)
        {
            var claim = accessor.HttpContext?.User?.FindFirst(JwtClaimNames.UserId);
            return claim is not null && long.TryParse(claim.Value, out var userId) ? userId : null;
        }

        /// <summary>
        /// 当前登录用户账号(User Claim Account;无用户/无 Claim 返回 null)。
        /// 口径:审计用户名字段存账号而非姓名(账号唯一不可变,姓名可重复/可变更,见 JwtClaimNames.Account)。
        /// </summary>
        private static string? CurrentUserName(IHttpContextAccessor accessor)
        {
            return accessor.HttpContext?.User?.FindFirst(JwtClaimNames.Account)?.Value;
        }

        /// <summary>
        /// 当前请求租户 Id(User Claim TenantId;无用户/无 Claim 返回 null)。
        /// </summary>
        private static long? CurrentTenantId(IHttpContextAccessor accessor)
        {
            var claim = accessor.HttpContext?.User?.FindFirst(JwtClaimNames.TenantId);
            return claim is not null && long.TryParse(claim.Value, out var tenantId) ? tenantId : null;
        }

        /// <summary>
        /// 当前登录用户所属机构 Id(task_013 数据权限写入侧):查 sys_user.OrgId(JWT 不含 OrgId——
        /// 不新增 Claim,避免令牌结构变更);每请求 memo(HttpContext.Items)——用户机构在单请求内不变,
        /// 防逐实体填充反复查库;无用户上下文(后台/测试) → null(列留空 = 存量数据语义)。
        /// </summary>
        private static long? CurrentUserOrgId(IHttpContextAccessor accessor)
        {
            var userId = CurrentUserId(accessor);
            var httpContext = accessor.HttpContext;
            if (userId is null || httpContext is null)
            {
                return null;
            }

            const string MemoKey = "AdminCore.OrgIdMemo";
            if (httpContext.Items.TryGetValue(MemoKey, out var memo))
            {
                return (long?)memo;
            }

            long? orgId = null;
            if (ConnectionClients.TryGetValue(SqlSugarConst.MainConfigId, out var mainClient))
            {
                // 主库独立单连接 scope(线程安全,见 ConnectionClients WHY);查询自动带软删过滤(软删用户机构不参与填充)
                orgId = mainClient.Queryable<SysUser>()
                    .Where(u => u.Id == userId.Value)
                    .Select(u => u.OrgId)
                    .First();
            }

            httpContext.Items[MemoKey] = orgId;
            return orgId;
        }

        /// <summary>
        /// [BindSerial] 属性元数据缓存(批 W 4.3):实体类型 → 带 [BindSerial] 的 string 属性集(空 = 无绑定)。
        /// 首取反射,之后零反射(参照 SetSerialProperty _serialPropCache 同构)。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,
            System.Collections.Generic.Dictionary<string, (PropertyInfo Prop, BindSerialAttribute Attr)>?> SerialPropCache = new();

        /// <summary>
        /// 流水号自动填充(批 W 4.3/3.4,参照 AdminNetPro SetSerialProperty 移植):
        /// 插入实体的 [BindSerial] string 属性为 null → NextSeqNoAsync 取号填入;已有值不覆盖。
        /// WHY 同步 AOP 里 GetResult 阻塞:SqlSugar DataExecuting 钩子为同步签名(SQL 生成前),
        /// 取号 = 缓存锁 + 单语句 UPDATE(同库 RTT 毫秒级),参照实现同模式;后台任务/测试无 HttpContext
        /// 时取号仍可用(UserManager 各属性缺省语义)。
        /// </summary>
        private static void FillBindSerialProperties(
            AdminCore.Core.Services.Serial.SysSerialService serialService,
            DataFilterModel data)
        {
            var entityValue = data.EntityValue;
            var entityType = entityValue.GetType();
            if (data.OperationType is not DataFilterType.InsertByObject)
            {
                return;
            }

            var propDict = SerialPropCache.GetOrAdd(entityType, type =>
            {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
                    .Where(p => p.GetCustomAttributes(typeof(BindSerialAttribute), true).Length > 0)
                    .Select(p => new { Name = p.Name, Prop = p, Attr = (BindSerialAttribute)p.GetCustomAttributes(typeof(BindSerialAttribute), true)[0] })
                    .ToDictionary(t => t.Name, t => (t.Prop, t.Attr));
                return props.Count > 0 ? props : null;
            });

            if (propDict is null || !propDict.TryGetValue(data.PropertyName, out var propData))
            {
                return;
            }

            if (propData.Prop.GetValue(entityValue) is not null)
            {
                return; // 业务显式赋值优先,不覆盖
            }

            var serial = serialService.NextSeqNoAsync(propData.Attr.Type, propData.Attr.IsGlobal)
                .GetAwaiter().GetResult();
            propData.Prop.SetValue(entityValue, serial);
        }

        /// <summary>
        /// CodeFirst 建表(增量/全量分流 + 按库路由):
        /// 增量模式(EnableIncreTable=true,默认)只处理 [SugarTable]+[IncreTable] 且非 [IgnoreTable] 实体;
        /// 全量模式(=false,首次部署)处理所有 [SugarTable] 实体(同样排除 [IgnoreTable]——
        /// 只读外部表/手工维护表两种模式均禁 DDL)。已有表仅补新增列,不删列,不丢数据。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(多连接 Scope)。</param>
        /// <param name="assemblies">要扫描的程序集;缺省扫 Core 自身(框架实体落点),业务程序集显式传入。</param>
        public static void InitTables(this ISqlSugarClient db, params Assembly[] assemblies)
        {
            // 缺省扫 Core 自身:框架测试实体(SysConfig 等)在 Core,业务实体显式传 Business 程序集;
            // 批 AB:程序集经 ResolveAssemblies 登记,租户库建表/种子复用(见 RegisteredAssemblies)
            var scanAssemblies = ResolveAssemblies(assemblies);

            var entityTypes = scanAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<SugarTable>(inherit: false) != null
                            && t.GetCustomAttribute<IgnoreTableAttribute>() is null)
                .Where(t => EnableIncreTable ? t.GetCustomAttribute<IncreTableAttribute>() is not null : true)
                .ToList();

            if (entityTypes.Count == 0)
            {
                return;
            }

            // 多库:按实体特性把实体路由到各自连接,每库只处理自己的实体(5.1.4.217 的 InitTables 数组重载接收 Type[])
            foreach (var group in entityTypes.GroupBy(t => SelectConfigIdFor(db, t)))
            {
                var conn = GetConnectionById(db, group.Key);
                // 分表实体(批 AC 3.1)与普通实体分开建表:分表实体表名按时间模板生成,
                // 必须走 CodeFirst.SplitTables().InitTables(单类型重载)才能按分表策略建全部子表;
                // 混入普通 InitTables 会按模板名建一张错表(表名含 {year}{month} 字面量)
                var splitTypes = group.Where(IsSplitTableEntity).ToArray();
                var normalTypes = group.Where(t => !IsSplitTableEntity(t)).ToArray();

                if (normalTypes.Length > 0)
                {
                    conn.CodeFirst.InitTables(normalTypes);
                }

                if (splitTypes.Length > 0)
                {
                    conn.CodeFirst.SplitTables().InitTables(splitTypes);
                }
            }
        }

        /// <summary>
        /// 是否分表实体(批 AC 3.1):贴 SqlSugar <c>[SplitTable]</c> 特性(按 Day/Week/Month/Season/Year 分表)。
        /// 建表走 <c>CodeFirst.SplitTables()</c>,查询/写入走仓储的分表入口(见 ISqlSugarRepository 分表方法)。
        /// </summary>
        internal static bool IsSplitTableEntity(Type entityType)
            => entityType.GetCustomAttribute<SplitTableAttribute>() is not null;

        /// <summary>
        /// 租户库建表(批 AB C4 多租户库级隔离):在指定租户的**独立库**上建 [Tenant] 实体表。
        /// 扫描口径与 InitTables 一致的增量/全量分流,但只取贴 [Tenant] 的实体(主库专属实体不落租户库);
        /// 幂等可重跑(CodeFirst 增量:已有表只补新增列,不删列不丢数据)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(多连接 Scope)。</param>
        /// <param name="tenantId">租户 Id(= TenantId Claim 值;默认租户恒主库,不支持)。</param>
        /// <param name="assemblies">要扫描的程序集;缺省用登记簿(启动期 InitTables 登记过的那批)。</param>
        /// <returns>本次处理的实体表数量(0 = 该租户库无 [Tenant] 实体)。</returns>
        public static int InitTenantTables(this ISqlSugarClient db, long tenantId, params Assembly[] assemblies)
        {
            var configId = RequireTenantConfigId(tenantId);
            var entityTypes = ResolveAssemblies(assemblies)
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<SugarTable>(inherit: false) != null
                            && t.GetCustomAttribute<IgnoreTableAttribute>() is null
                            && t.GetCustomAttribute<AdminCore.Core.Attributes.TenantAttribute>() is not null)
                .Where(t => EnableIncreTable ? t.GetCustomAttribute<IncreTableAttribute>() is not null : true)
                .ToArray();

            if (entityTypes.Length == 0)
            {
                return 0;
            }

            var conn = GetConnectionById(db, configId);
            // 分表实体同建表主路径口径(批 AC 3.1):走 CodeFirst.SplitTables() 建全部子表
            var splitTypes = entityTypes.Where(IsSplitTableEntity).ToArray();
            var normalTypes = entityTypes.Where(t => !IsSplitTableEntity(t)).ToArray();

            if (normalTypes.Length > 0)
            {
                conn.CodeFirst.InitTables(normalTypes);
            }

            if (splitTypes.Length > 0)
            {
                conn.CodeFirst.SplitTables().InitTables(splitTypes);
            }

            return entityTypes.Length;
        }

        /// <summary>
        /// 租户库种子(批 AB C4):把 [IncreSeed] 数据种子中**实体带 [Tenant]** 的部分按主键
        /// **缺行插入、已有跳过**落租户库(主库专属种子不落租户库);与主库同一条 StoreSeedData 语义,
        /// 幂等可重跑且不覆写租户运行期修改(2026-10-30 起,同主库口径)。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(多连接 Scope)。</param>
        /// <param name="tenantId">租户 Id(默认租户恒主库,不支持)。</param>
        /// <param name="assemblies">要扫描的程序集;缺省用登记簿。</param>
        /// <returns>本次执行的种子类数量。</returns>
        public static int InitTenantSeedData(this ISqlSugarClient db, long tenantId, params Assembly[] assemblies)
        {
            var configId = RequireTenantConfigId(tenantId);
            var conn = GetConnectionById(db, configId);

            var seedClasses = ResolveAssemblies(assemblies)
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<IncreSeedAttribute>() is not null
                            && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISqlSugarEntitySeedData<>)))
                .Where(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISqlSugarEntitySeedData<>))
                    .Select(i => i.GetGenericArguments()[0])
                    .Any(entityType => entityType.GetCustomAttribute<AdminCore.Core.Attributes.TenantAttribute>() is not null))
                .OrderBy(t => t.GetCustomAttribute<SeedDataAttribute>()?.Order ?? 0)
                .ThenBy(t => t.FullName)
                .ToList();

            foreach (var seedClass in seedClasses)
            {
                var seedInstance = Activator.CreateInstance(seedClass);
                var dataMethod = seedClass.GetMethods()
                    .FirstOrDefault(m => m.Name == "HasData"
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType == typeof(ISqlSugarClient))
                    ?? seedClass.GetMethod("HasData");
                var data = dataMethod?.Invoke(
                    seedInstance,
                    dataMethod is not null && dataMethod.GetParameters().Length == 1 ? new object[] { db } : null)
                    as System.Collections.IEnumerable;
                if (data is null)
                {
                    continue;
                }

                var entityType = seedClass.GetInterfaces()
                    .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISqlSugarEntitySeedData<>))
                    .GetGenericArguments()[0];
                var storeMethod = typeof(SqlSugarSetup)
                    .GetMethod(nameof(StoreSeedData), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(entityType);
                storeMethod.Invoke(null, new object[] { conn, data });
            }

            return seedClasses.Count;
        }

        /// <summary>
        /// 解析并校验租户库 ConfigId(租户建表/种子共用前置):默认租户无独立库;未注册即 fail-loud
        /// (注册由 TenantDbRegistry.EnsureConnectionAsync 完成,见批 AB C3)。
        /// </summary>
        private static string RequireTenantConfigId(long tenantId)
        {
            var configId = TenantDbRegistry.ConfigIdForTenant(tenantId);
            if (configId == SqlSugarConst.MainConfigId)
            {
                throw new InvalidOperationException("默认租户恒用主库,不存在独立租户库(不支持租户库建表/种子)");
            }

            if (!ConnectionClients.ContainsKey(configId))
            {
                throw new InvalidOperationException(
                    $"租户库连接未注册(租户={tenantId}, 库标识={configId}):请先配置租户连接串并调用 EnsureConnectionAsync");
            }

            return configId;
        }

        /// <summary>
        /// 存量列名大小写自愈(task_008 fixa,幂等):开发库在旧配置(自动小写)下建表,列名全为小写(createtime),
        /// 本 fix 关闭自动小写后 SQL 按声明大小写加引号引用,存量小写列须一次性更名为实体声明的 PascalCase,
        /// 否则验收 SQL 的引号列名(select "CreateTime" ...)匹配不到列。
        /// 扫描口径与 InitTables 完全一致([SugarTable] 且非 [IgnoreTable],增量/全量分流同规则),
        /// 按实体特性选库路由(主库/日志库各自自愈)。**必须在 InitTables 之前执行**:
        /// 否则 CodeFirst 见不到引号列名会另建小写同义列,产生双列脏数据。
        /// 规则(防误改):大小写精确匹配 → 跳过;大小写不敏感恰好唯一匹配 → RENAME COLUMN;
        /// 表不存在 → 跳过(CodeFirst 随后按声明大小写建表);不匹配或大小写异名多列(歧义) → 跳过不猜。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(多连接 Scope)。</param>
        /// <param name="assemblies">要扫描的程序集;缺省扫 Core 自身,业务程序集显式传入(与 InitTables 同参)。</param>
        public static void HealColumnCase(this ISqlSugarClient db, params Assembly[] assemblies)
        {
            var scanAssemblies = assemblies.Length > 0
                ? assemblies
                : new[] { typeof(SqlSugarSetup).Assembly };

            var entityTypes = scanAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<SugarTable>(inherit: false) != null
                            && t.GetCustomAttribute<IgnoreTableAttribute>() is null)
                .Where(t => EnableIncreTable ? t.GetCustomAttribute<IncreTableAttribute>() is not null : true)
                .ToList();

            foreach (var group in entityTypes.GroupBy(t => SelectConfigIdFor(db, t)))
            {
                var conn = GetConnectionById(db, group.Key);
                foreach (var entity in group)
                {
                    // 分表实体(批 AC 3.1)跳过自愈:分表表名是含时间模板的通配名(如 {year}{month}),
                    // 按模板名查 information_schema 恒查不到表 → 内部整体跳过,且对模板名拼 SQL 无意义;
                    // 分表子表的列名一致性由 CodeFirst.SplitTables().InitTables 负责(建表即按声明列名)
                    if (IsSplitTableEntity(entity))
                    {
                        continue;
                    }

                    HealEntityColumnCase(conn, entity);
                }
            }
        }

        /// <summary>
        /// 单表列名自愈:实体声明列在库中大小写不精确匹配且恰好唯一匹配 → ALTER TABLE RENAME COLUMN。
        /// 表标识与列标识均加双引号(精确大小写);列名来源为库内 information_schema 实测值与实体声明值,无外部输入。
        /// </summary>
        private static void HealEntityColumnCase(ISqlSugarClient conn, Type entity)
        {
            var info = conn.EntityMaintenance.GetEntityInfo(entity);
            var tableName = info.DbTableName;

            // 库内该表现存列名(表名精确匹配;表不存在 → 空集 → 整体跳过,CodeFirst 随后按声明大小写新建)
            var dbColumns = conn.Ado.SqlQuery<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @t",
                new SugarParameter("@t", tableName));
            if (dbColumns.Count == 0)
            {
                return;
            }

            foreach (var column in info.Columns)
            {
                var declared = column.DbColumnName;
                if (dbColumns.Contains(declared))
                {
                    continue; // 精确大小写一致 → 无需自愈(含首次部署已按声明建列的情形)
                }

                // 大小写不敏感匹配:恰好唯一 → 更名;0 个或多个(大小写异名并存) → 跳过不猜
                var candidates = dbColumns
                    .Where(c => !string.Equals(c, declared, StringComparison.Ordinal))
                    .Where(c => string.Equals(c, declared, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count != 1)
                {
                    continue;
                }

                conn.Ado.ExecuteCommand($"ALTER TABLE \"{tableName}\" RENAME COLUMN \"{candidates[0]}\" TO \"{declared}\"");
            }
        }

        /// <summary>
        /// 增量种子(数据种子机制,task_008):扫描程序集内贴 [IncreSeed] 且实现 ISqlSugarEntitySeedData&lt;T&gt; 的类,
        /// 按 [SeedData(Order)] 升序(同序按类名,跨重启稳定)执行,种子数据按主键**缺行插入、已有跳过**
        /// (2026-10-30 起;种子只做初始引导,不再覆写库内既有行——结构演进改走版本化升级件,见 StoreSeedData)。
        /// 数据源方法:优先取 HasData(ISqlSugarClient) 重载(需引用库内状态的种子,如按账号查存量用户),
        /// 否则取契约无参 HasData();两类种子同机制同顺序。
        /// 命令式种子(ISqlSugarSeed,存量机制)另有 InitSeeds 同批执行,两者互不重叠。
        /// </summary>
        /// <param name="db">SqlSugar 客户端(多连接 Scope)。</param>
        /// <param name="assemblies">要扫描的程序集;缺省扫 Core 自身,业务程序集显式传入。</param>
        public static void InitSeedData(this ISqlSugarClient db, params Assembly[] assemblies)
        {
            var scanAssemblies = ResolveAssemblies(assemblies);

            var seedClasses = scanAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.GetCustomAttribute<IncreSeedAttribute>() is not null
                            && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISqlSugarEntitySeedData<>)))
                .ToList();

            foreach (var seedClass in seedClasses
                         .OrderBy(t => t.GetCustomAttribute<SeedDataAttribute>()?.Order ?? 0)
                         .ThenBy(t => t.FullName))
            {
                // 数据源方法:优先 HasData(ISqlSugarClient) 重载(需库内状态),否则契约无参 HasData();
                // 反射调用不引入 dynamic(项目禁用)
                var seedInstance = Activator.CreateInstance(seedClass);
                var dataMethod = seedClass.GetMethods()
                    .FirstOrDefault(m => m.Name == "HasData"
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType == typeof(ISqlSugarClient))
                    ?? seedClass.GetMethod("HasData");
                // 非泛型 IEnumerable:HasData 返回类型不定(IEnumerable<T> 均实现之),反射结果按基接口收口。
                // WHY 全限定:本文件 using 了 System.Collections.Generic,IEnumerable 会歧义到泛型 IEnumerable<T>
                var data = dataMethod?.Invoke(
                    seedInstance,
                    dataMethod is not null && dataMethod.GetParameters().Length == 1 ? new object[] { db } : null)
                    as System.Collections.IEnumerable;
                if (data is null)
                {
                    continue;
                }

                var entityType = seedClass.GetInterfaces()
                    .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISqlSugarEntitySeedData<>))
                    .GetGenericArguments()[0];

                // 种子实体同样按特性选库(如日志库的种子),与仓储口径一致;
                // 泛型执行入口经一次反射绑定(Why 见 StoreSeedData)
                var storeMethod = typeof(SqlSugarSetup)
                    .GetMethod(nameof(StoreSeedData), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(entityType);
                storeMethod.Invoke(null, new object[]
                {
                    GetConnectionById(db, SelectConfigIdFor(db, entityType)),
                    data
                });
            }
        }

        /// <summary>
        /// 种子数据执行(泛型单点):**按主键"缺行插入、已有跳过"**——种子只做初始引导,永不覆写库内既有行。
        /// </summary>
        /// <remarks>
        /// WHY 不做 upsert(2026-10-30 缺陷上报《种子冲突语义缺陷》,实证见该件 §1):种子行在运行期是
        /// **可管理面编辑的库实体**(sys_config 开关值、菜单/权限骨架都带 UpdateTime),启动时按主键整行
        /// 覆写＝每次部署都执行一次隐式的"代码态强制回滚数据态"。下游实证:sys_otp_enabled 被管理员显式
        /// 改为 False 后,一次常规部署重启即被静默覆回 True,管理面自动化全面失联。
        /// 正确职责切分:种子＝初始引导(缺行插入、已有跳过,永不与运行时修改冲突);结构演进(新增/改默认值)
        /// ＝版本化升级件(第 2 步,计划书 docs/业务/规划/计划_版本化升级件机制_2026-10-30.md)。
        /// WHY 仍走 Storageable:分类口径(主键存在性)与旧 upsert 路径逐字一致,只把 UpdateList/IgnoreList
        /// 丢弃、只落 InsertList——不另造主键查询逻辑,避免"软删行/多主键/实体映射"边界产生行为漂移。
        /// WHY 泛型方法 + 单点反射绑定:SqlSugar 5.1.4.x 的 Storageable&lt;T&gt; 为泛型成员,
        /// 而扫描循环只有运行期实体类型,泛型调用须经 MakeGenericMethod 绑定一次,收敛到本方法,
        /// 扫描循环不出现 dynamic/装箱。
        /// WHY ToStorage + 自行落地而不是 ExecuteCommand:ToStorage() 只按主键把数据分类为插入/更新列表、
        /// 不落库(实机探针验证);旧实现用 ExecuteCommand 一次完成"分类 + 插入 + 更新",现语义要在分类后
        /// **丢弃更新面**,故只落 InsertList(AsInsertable 即 db.Insertable(InsertList)),更新面零执行(不存在"覆写通道")。
        /// </remarks>
        private static void StoreSeedData<TEntity>(ISqlSugarClient conn, System.Collections.IEnumerable data)
            where TEntity : class, new()
        {
            var list = new List<TEntity>();
            foreach (var item in data)
            {
                list.Add((TEntity)item);
            }

            if (list.Count == 0)
            {
                return;
            }

            var storage = conn.Storageable(list).ToStorage();
            if (storage.InsertList.Count == 0)
            {
                // 全部命中既有行(UpdateList/IgnoreList):种子语义=已有跳过,一行都不落库,绝不覆写运行时修改
                return;
            }

            storage.AsInsertable.ExecuteCommand(); // AsInsertable 只落 InsertList(UpdateList/IgnoreList 零执行)
        }
    }
}
