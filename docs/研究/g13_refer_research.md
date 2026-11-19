# g13 参照调研——5.8 事件执行器(SysLogEvent 审计 + 精细重试)(批 S)

## §0 目标

功能清单 5.8(🟡):有总线内置简化重试(≤2/1s/耗尽告警)。缺:独立执行器/SysLogEvent 审计/精细重试。
同批 5.9(🟡)判定见 §6。

## §1 参照仓

- `Admin.NET.Core/EventBus/RetryEventHandlerExecutor.cs`(140 行):`IEventHandlerExecutor` 实现。
  - **SysLogEvent 审计**:执行前 Insert(Running)→ 每次尝试 finally Update(Success/Retry/Error + 耗时 + 重试记录拼接);
    `ShouldLog` = 方法无 `[SuppressMonitor]` 特性(默认全审计,可 opt-out);日志写失败只记错误不阻断。
  - **精细重试**:`Retry.InvokeAsync(handler, numRetries, retryTimeout, exceptionTypes, retryAction)`:
    `NumRetries`(订阅特性,0=不重试)/`RetryTimeout`(ms,缺省 1000)/`ExceptionTypes`(仅列出的异常类型触发重试,空=全重试);
    状态机:失败且次数<上限→Retry(继续),耗尽→Error。
  - 独立库:`SqlSugarSetup.LogDb`(事件日志落日志库,与业务隔离)。
  - **参照缺陷**:执行器持有 `SqlSugarScopeProvider _db`(Furion 全局静态)→ 零 Furion 红线需改 DI;
    `Retry.InvokeAsync` 是 Furion 内置工具(本仓自实现等价循环,不引 Furion)。
- `Admin.NET.Core/Entity/SysLogEvent.cs`(13 字段):RunId/EventId(两列同值=Source.EventId)/Payload(BigString)/
  CreateTimeOfEvent/ExecutingTime/Exception(BigString)/Elapsed(long?)/ExecutedTime/Attribute(512)/
  HandlerMethod(128)/Properties(1024)/ConsumeOnce/RetryRecord(1024)/Status(EventStatusEnum);
  `[SysTable][LogTable]`+双索引(RunId/EventId)。
- `Admin.NET.Core/Enum/EventStatusEnum.cs`:Running=1/Success=2/Retry=3/Error=4。
- 执行器挂接:参照 `EventConsumer` 经 DI 解析 `IEventHandlerExecutor`(默认 RetryEventHandlerExecutor);
  订阅特性扩展字段来自 Furion EventBus 生态(`EventSubscribeAttribute` 非参照仓自建)——**本仓特性自建,扩展字段自主**。

## §2 本仓盘点

- `Core/EventBus/EventBus.cs`(313 行):有界 Channel(1000)+单后台消费者+构造期反射扫描 [EventSubscribe]+
  `InvokeWithRetryAsync` 固定全局重试(≤2/1s/耗尽告警,无审计、无按订阅者定制)。
- 本仓 `EventSubscribeAttribute` 自建(仅 Name 字段)→ 扩展 NumRetries/RetryTimeout/ExceptionTypes 零依赖。
- 订阅者 3 个(OpLog/ExLog/MsgLog)全在 Core/Services/(反射扫描单程序集覆盖)。
- 选库:`SelectConfigIdFor` 已有 `[LogTable]` 分支(未配置回落主库)→ SysLogEvent 贴 `[LogTable]` 零新选库代码。
- 实体基线:EntityBase(id/CreateTime/UpdateTime/审计)+`[IncreTable]` 建表+`[SugarTable]` 显式小写表名(本仓口径,
  非参照的 `SugarTable(null, desc)` 空名推断)。
- 测试容器:EventBusTest 已有(AddAutoServices 覆盖 [Service] 自动注册)→ 执行器自动注册进既有容器。

## §3 设计

1. **`EventStatusEnum`**(`Core/EventBus/EventStatusEnum.cs`):Running=1/Success=2/Retry=3/Error=4(与参照同值)。
2. **`SysLogEvent` 实体**(`Core/Entities/SysLogEvent.cs`):参照 13 字段裁剪至实际消费(#8):
   EventId(64,参照 RunId+EventId 同值两列合一)/Status/Payload(CodeFirst_BigString)/Exception(CodeFirst_BigString)/
   Elapsed(long?)/ExecutingTime/ExecutedTime(DateTime?)/Attribute(512)/HandlerMethod(128)/RetryRecord(1024);
   裁掉:ConsumeOnce(内存版无持久化事件源,无只消费一次语义)/Properties(参照 Channel 共享上下文,本仓单载荷模型无此概念)/
   CreateTimeOfEvent(事件创建时刻无持久化载体——内存事件入队即消费,审计自执行时刻起算);
   `[IncreTable][LogTable]`+EntityBase 继承(审计字段 CreateTime 兼作落库时刻)+索引(EventId)。
3. **`EventSubscribeAttribute` 扩展**:`NumRetries`(int,-1=回落全局 ≤2)/`RetryTimeout`(int ms,-1=回落全局 1000)/
   `ExceptionTypes`(Type[],空=任意异常触发重试;非空=仅列出的类型及其子类触发,其余直接失败不重试)——
   缺省值保证既有 3 订阅者零改动行为不变。
4. **`IEventHandlerExecutor`+`RetryEventHandlerExecutor`**(`Core/EventBus/`):
   - 接口:`Task ExecuteAsync(EventHandlerExecutingContext context, Func<EventHandlerExecutingContext, Task> handler)`;
     context=事件名+载荷+订阅者类型+方法+特性(自定 POCO,非参照的 Furion context)。
   - 实现:审计(Insert Running→每尝试 finally Update)+精细重试循环(自实现,不引 Furion Retry):
     次数/间隔/异常类型白名单按特性,缺省回落全局;审计写失败只记错误不阻断(日志库不可用不伤事件流);
     `[SuppressMonitor]` 特性(方法级,审计 opt-out)。
   - 依赖:ILogger+ISqlSugarClient(单例,[LogTable] 选库)→ `[Service]` Singleton 自动注册。
   - **审计默认开**(参照口径:可追溯性优先;每事件 2 条 SQL 落日志库,压力隔离)。
5. **EventBus 接通**:`InvokeWithRetryAsync` 整体委托执行器(重试语义迁入执行器,总线只负责"分发+作用域+类型校验");
   构造注入 `IEventHandlerExecutor`。
6. **5.9 同批闭合(判定)**:扩展机制完备(标记接口+声明式特性+反射扫描+自动注册)+3 个内建订阅者全有
   (参照 6 订阅者中内建型 HttpLog/ExLog/MsgLog 一一对应)+缺的邮件/企微/短信=外部通道集成(需外部服务凭据+
   业务场景,#8 最小必要+零消费方不建,同 3.4 BindSerial 按需后补口径)→ 5.9 转 ✅(说明更新)。

## §4 验证口径

`BatchSExecutorTest`(真实 PG,探针订阅者 finally 清理):
1. **审计落库**:发布事件→sys_log_event 出现 Success 行(HandlerMethod/Payload/Elapsed>0)。
2. **失败重试+审计**:探针订阅者前 N 次抛异常→重试后成功→审计行 Retry→Success,RetryRecord 含失败记录。
3. **耗尽→Error**:探针订阅者恒抛→审计行 Status=Error,RetryRecord 含全部尝试。
4. **精细重试**:探针订阅者特性 NumRetries=1→仅 1 次重试(对照全局 2 次);ExceptionTypes 白名单不匹配→0 重试直接 Error。
5. **SuppressMonitor**:贴特性→无审计行,订阅照常执行。
6. **审计写失败不阻断**:执行器指向不可写库场景难模拟——改经代码审查+审计 try/catch 结构(§5 声明);
   或探针订阅者正常执行同时断言审计行存在(正例覆盖)。
容器模板:in-memory config + AddSqlSugar/AddCache/AddProjectOptions/AddAutoServices + IsPostgresReachable 降级。

## §5 参照偏差声明

1. **`SqlSugarSetup.LogDb` 静态取库 → DI ISqlSugarClient 单例**([LogTable] 经 SelectConfigIdFor 既有分支选库,零 Furion)。
2. **`Retry.InvokeAsync`(Furion 工具)→ 自实现重试循环**(语义等价:次数/间隔/异常类型白名单;不引 Furion)。
3. **Furion `EventHandlerExecutingContext` → 自定 POCO context**(事件名+载荷+订阅者类型+方法+特性;载荷序列化经 System.Text.Json 同口径)。
4. **RunId+EventId 两列合一**(参照两列同值 Source.EventId,#8 裁剪)。
5. **ConsumeOnce/Properties/CreateTimeOfEvent 三字段裁掉**(内存版无对应语义,§3 ②)。
6. **审计默认开+SuppressMonitor opt-out**(与参照 ShouldLog 同口径)。
7. **重试在单消费者线程串行**(本仓既有口径;参照同——审计不改变此节奏,高失败率占吞吐已在 EventBus 注释声明)。

## §6 5.9 判定(同批)

扩展机制完备(§2)+内建订阅者 3/3+外部通道 3 个不建(#8,零消费方+需外部凭据)→ 5.9 转 ✅(说明更新,无代码)。
后续业务接入邮件/企微/短信=贴 [EventSubscribe] 新订阅类(机制现成,使用指南 §5 已覆盖订阅者范式)。

## §7 范围边界(不做)

- Redis/RabbitMQ 事件源存储(5.6 双通道,悬置口径不变);
- 审计表管理端点(SysLogEvent 查询 API 按需后补,#8);
- 事件持久化/重放(内存版入队即消费,进程重启丢失=既有口径,5.6 后补);
- fallbackPolicy 降级回调(参照亦注释未启用)。
