# g12 参照调研——3.5 SqlSugar 二级缓存(批 R)

## §0 目标

功能清单 3.5(🟡):有 `ICacheService`/Redis/Memory 双实现+`AddCache`。缺:SqlSugar 二级缓存适配(SqlSugarCache/DataInfoCacheService 零命中)。

## §1 参照仓

- `Admin.NET.Core/SqlSugar/SqlSugarCache.cs`(60 行):实现 `SqlSugar.ICacheService`,委托 `SysCacheService`(参照仓自有缓存服务),键前缀 `CacheConst.SqlSugar = "sys_sqlSugar:"`。
  - 方法集(7):`Add<V>(key, value)` / `Add<V>(key, value, cacheDurationInSeconds)` / `ContainsKey<V>(key)` / `Get<V>(key)` / `GetAllKey<V>()` / `GetOrCreate<V>(key, create, cacheDurationInSeconds = int.MaxValue)` / `Remove<V>(key)`。
  - **参照缺陷(与批 P SqlSugarDataExecuting 同型)**:全仓 `WithCache()` 零命中——`DataInfoCacheService` 挂了但**查询侧从不显式启用缓存**,纯死挂钩;`GetAllKey` 委托 `GetKeysByPrefixKey`(参照仓有键枚举能力)。
  - 静态引用 `App.GetRequiredService<SysCacheService>()`(Furion 静态容器)——零 Furion 红线→DI 闭包捕获(批 P 同裁决)。
- `Admin.NET.Core/SqlSugar/SqlSugarSetup.cs` L126/L135:`ConfigureExternalServices.DataInfoCacheService = new SqlSugarCache()` + `ConnMoreSettings.IsAutoRemoveDataCache = true`(所有增删改自动调 `.RemoveDataCache()`)。

## §2 本仓盘点

- 本仓 `SqlSugarSetup` 无 `ConfigureExternalServices`(零命中);`ConnMoreSettings` 仅 PG 精确大小写三开关(`PgExactCaseSettings`)。
- 本仓 `Core/Cache/ICacheService`(task_007 契约):**全异步**(`SetAsync/GetAsync<T>/RemoveAsync/ExistKeyAsync`...),值 JSON 序列化;Redis/Memory 双实现;无键枚举方法。
- 本仓查询侧零 `.WithCache()`(与参照同——启用缓存是查询 opt-in,默认关闭)。
- 同名消歧:`SqlSugar.ICacheService`(二级缓存接口)与 `AdminCore.Core.Cache.ICacheService`(缓存抽象)同名——本仓既有别名先例(`using ICacheService = AdminCore.Core.Cache.ICacheService;`)。

## §3 设计

1. **`SqlSugarDataCacheService`**(`Core/SqlSugar/SqlSugarDataCacheService.cs`,新):实现 `SqlSugar.ICacheService` 7 方法,委托本仓 `Core.Cache.ICacheService`(构造注入,非静态),键前缀 `sys_sqlSugar:`(与参照 `CacheConst.SqlSugar` 同值)。
   - **同步→异步桥**:`GetAwaiter().GetResult()`(SqlSugar 数据缓存接口天生同步;参照仓 SysCacheService 同模式——同步方法内部包 Redis 异步;生产路径与 xUnit1031 测试口径同源,SqlSugar 查询线程上阻塞仅一次缓存 IO)。
   - `Add(key, value, seconds)`:seconds=int.MaxValue 视为无过期(本仓 `SetAsync(null)`=永不过期)。
   - `GetOrCreate(key, create, seconds)`:先 `ExistKeyAsync` 判存 → 命中 `GetAsync`/未命中 `create()`+`SetAsync`(#11 复用既有 KV 原语;参照仓 GetOrAdd 原子语义在 Redis 侧,本仓 Memory 实现无原子 GetOrAdd——缓存穿透场景(并发同 key 首查)双读 DB 后同值覆盖写,幂等无害,裁决 §5 ③)。
   - **`GetAllKey` 空实现**:本仓缓存抽象无键枚举(不开放前缀枚举——键空间安全/DoS 面);SqlSugar 精确失效走 `Remove`(自动删缓存路径),全量清空(`.ClearCache()`)本仓业务零调用——空枚举安全(裁决 §5 ②)。
2. **`SqlSugarSetup` 接通**(`DataInfoCacheService` 挂点):`AddSqlSugar` 扩展的 `AddSingleton<ISqlSugarClient>` 闭包内(sp 可用)解析 `Core.Cache.ICacheService` → `connectionConfigs` 每项追加 `ConfigureExternalServices = new ConfigureExternalServices { DataInfoCacheService = new SqlSugarDataCacheService(cache) }`;`PgExactCaseSettings` +`IsAutoRemoveDataCache = true`(增删改自动 `RemoveDataCache`)。
   - 挂接时机:scope 构造前(连接配置装配段),单例 scope 生命周期内 cache 实例恒定(DI Singleton/Scoped?——**Core ICacheService 生命周期须 Singleton 语义**(单例 scope 持有):查注册处确认,Scoped 则闭包内建 scope 解析 Singleton 根容器实例)。
3. **消费口径(与批 P 同型"接通闭环")**:框架层**不改任何既有查询**(零行为变化——二级缓存是查询 opt-in);测试证闭环:探针实体 `.WithCache()` 查询(首查 DB/二查缓存命中/写操作自动失效后见新值)。

## §4 验证口径

`BatchRDataCacheTest`(真实 PG,探针表或既有表探针行 finally 清理):
1. **缓存命中**:探针实体两次 `.WithCache()` 查询 → 第二次 SQL 不执行(Aop.OnLogExecuted 计数/或 cache key 存在断言 `sys_sqlSugar:` 前缀键)。
2. **自动失效**:`UpdateByObject`(带 IsAutoRemoveDataCache)→ 再查 `.WithCache()` 见新值(缓存已删)。
3. **DeleteByObject 失效**:删除后 `.WithCache()` 查询空集。
4. **前缀隔离**:`sys_sqlSugar:` 键不污染业务键空间(业务 `ICacheService` 直读 `sys_sqlSugar:` 键可见 = 委托同一底层)。
容器模板:in-memory config + `AddSqlSugar`+`AddCache`+`AddProjectOptions`(批 Q 口径)+ `AddAutoServices` + `IsPostgresReachable()` 降级。

## §5 参照偏差声明(初版,§7 实证后有修订)

1. **`App.GetRequiredService` 静态容器 → DI 闭包捕获**(零 Furion 红线;批 P 同裁决 g10 §5 ②)。
2. ~~**`GetAllKey` 空实现**~~ → **§7 ② 推翻**:自动失效依赖 GetAllKey 枚举删除,空实现=失效零生效;改进程内键登记簿。
3. **`GetOrCreate` 非原子**(参照 `GetOrAdd` Redis 侧原子;本仓"ExistKey→Get/Set"读-改-写,并发首查双读同值覆盖写幂等无害;缓存穿透场景性能损失非正确性问题)。
4. **挂接点位置**:参照在 `config.ConfigureExternalServices`(单连接配置段);本仓双段(多库 entries/存量 Default 回落)均在 `LoadConnectionConfigs` 静态方法→改在闭包内 scope 构造前统一追加(一处代码覆盖两配置段)。
5. **查询侧 `.WithCache()` 零启用**(参照同为零消费死挂钩;本仓接通机制+测试证闭环,业务查询 opt-in 启用——#8 最小必要,与批 P"框架层零示例实现"同口径)。
6. **同步→异步桥 `.GetAwaiter().GetResult()`**(参照仓 SysCacheService 同步包装异步同模式;SqlSugar 数据缓存接口天生同步,无替代)。

## §7 实机实证修订(批 R 开发中探针实锤,推翻初版两处判断)

1. **`Remove` 键形态不对称(参照同口径,初版移植时误加前缀)**:SqlSugar 数据缓存的固有约定——写路径
   (`Add`/`GetOrCreate`/`Get`/`ContainsKey`)传**裸键**,删/枚举路径(`Remove`/`GetAllKey` 返回值)传/给**含前缀全键**
   (Remove 的键来自 GetAllKey 枚举值)。参照 `SqlSugarCache.Remove` 不加前缀,初版移植误加 → 双前缀 `sys_sqlSugar:sys_sqlSugar:...`
   → 失效零生效(R2/R3 实锤失败)。已修:Remove 不加工前缀。
2. **`GetAllKey` 空实现不安全(初版裁决推翻)**:实机诊断——`IsAutoRemoveDataCache` 的增删改自动失效路径 =
   `RemoveDataCache` → **`GetAllKey` 枚举 + 逐键 `Remove`**(非精确单键删)。空 GetAllKey = 自动失效零生效
   (R2/R3 实锤失败)。参照委托 `GetKeysByPrefixKey` 正是为此。**修复**:适配器自持**进程内键登记簿**
   (`ConcurrentDictionary<string,byte>`,Add/GetOrCreate 写时登记、Remove 时移除),GetAllKey 返回登记键
   (含前缀)。语义:单实例口径与底层键空间一致;多实例各实例登记独立,失效只清本实例登记过的键
   (跨实例缓存值相同、各实例写操作各自失效,语义正确;未登记键残留无害)。登记表体量受二级缓存键基数
   (实体×查询形态)约束,有限。
3. **数据缓存键形态(实锤)**:`sys_sqlSugar:` + `SqlSugarDataCache.<表名>."<表名>" .ORDER BY ... . WHERE (...)....<指纹>.<参数指纹>`
   (SqlSugar 5.1.4.217 内部生成,查询条件/参数参与指纹)。
4. **`.WithCache()` 命中验证口径(实锤)**:自动失效使"物理删行后缓存仍在"的朴素命中证明被打破
   (删行本身触发 RemoveDataCache)。命中证明改用**原生 SQL 直改源行**(`Ado.ExecuteCommand` 不经 SqlSugar
   更新路径 → 不触发自动失效)→ `.WithCache()` 仍返回缓存值(不回源)= 命中。
5. **`DataCache.Servie`(SqlSugar 拼写,非 Service)**:`SqlSugarProvider.DataCache : SugarCacheProvider`,
   其 `Servie : SqlSugar.ICacheService` 惰性读 `CurrentConnectionConfig.ConfigureExternalServices.DataInfoCacheService`
   (构造时/运行时设置均可生效,探针工程实锤)。

6. **挂接降级(全量测试实锤)**:`AddSqlSugar` 闭包原用 `GetRequiredService<ICacheService>` 强解析 →
   15 个既有测试容器(只 AddSqlSugar 无 AddCache:EntityBaseTest/RepositoryTest/BatchKVerLockTest/BatchJSlowSqlTest 等)
   启动即抛 "No service for type ICacheService" 全组失败。修复=闭包 `sp.GetService` 可空降级:缓存未注册 →
   不挂 `DataInfoCacheService`(行为与批 R 前完全一致;`IsAutoRemoveDataCache` 对 SqlSugar 内建空缓存无害,
   无缓存实现的宿主业务不会启用 WithCache)。生产链 `AddCache` 恒在 `AddSqlSugar` 之前(Program.cs 装配顺序),
   生产恒挂接。裁决:二级缓存=可选增强,不反向绑架 `AddSqlSugar` 消费方(比批 Q 的"17 容器补行"方案更优:
   零测试容器改动,#8)。

## §6 范围边界(不做)

- 查询侧自动启用缓存(全仓 `.WithCache()` 扫描注入)——缓存是查询 opt-in,框架不代业务决策;
- 缓存预热/失效广播(跨实例 `RemoveDataCache` 同步)——单实例部署口径,多实例时 `IsAutoRemoveDataCache` 只清本实例查询缓存(Redis 键在共享层,跨实例查询缓存键同值→自然一致,失效广播按需后补);
- `GetAllKey` 键枚举能力(本仓缓存抽象层面不开放)。
