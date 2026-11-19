# SqlSugarCore 5.1.4.217 → 新版本升级评估（G7 / G9）

> 评估日期：2026-09-25｜评估性质：**只读评估**（未修改本仓任何既有文件；仅新建本文件）
> 环境：dotnet SDK 10.0.111 / 运行时 10.0.11；PostgreSQL `127.0.0.1:5432/huluyeye_admin`（dev 库）
> 探针工程：`/tmp/g7g9probe/`（pin 5.1.4.217）＋ `/tmp/g7g9probe/alt/`（pin 5.1.4.221，复用同一 `Program.cs`）
> 编译/反编译核对副本：`/tmp/apicheck/BaseAdminCore/`（仓树整体复制，改动只落在副本）
> 动机：判定 **升级 SqlSugarCore 能否解决已登记缺陷 G7 / G9**（两者均为 P3「待触发立项」，见 `docs/框架差距清单.md`）。

**一句话结论**：本仓用到的 SqlSugar API 面在 5.1.4.221 下**编译 0 error、签名逐条一致**，但 **5.1.4.221 实测对 G7 与 G9 均无任何改善**（失败数字逐条同形）——升级不是这两个缺陷的修复手段。

---

## 一、可用版本清单

查询口径：`dotnet package search SqlSugarCore --take 30 --format json`（最新版 5.1.4.221）＋ 源 `https://nuget.azure.cn/v3-flatcontainer/sqlsugarcore/index.json`（全量 2121 个版本）＋ 注册源 `…/registration5-semver1/sqlsugarcore/index.json`（取 `published` 发布日期）。

当前 pin：**5.1.4.217**（`src/Core/AdminCore.Core.csproj`），发布日期 2026-08-13。

| 版本 | 发布日期 | 归类 | 备注 |
|---|---|---|---|
| 5.1.4.217 | 2026-08-13 | **当前 pin** | 本仓在用 |
| 5.1.4.218 | 2026-09-01 | 同 5.1.4 线内补丁 | 上游提交含 `Update Ado.ExecuteCommandAsync`、Gbase |
| 5.1.4.219 | 2026-09-04 | 同 5.1.4 线内补丁 | 上游提交主要为 nuget 依赖推进、Gbase |
| 5.1.4.220 | 2026-09-08 | 同 5.1.4 线内补丁 | Gbase、nuget |
| **5.1.4.221** | **2026-09-13** | **同 5.1.4 线内补丁（最新）** | `Update Sqlite`、bulkcopy 临时文件报错信息、demo |

- **同 5.1.4 线内补丁**：5.1.4.218 / .219 / .220 / .221（四段版本号第四段递增，语义上是补丁级）。
- **跨小/大版本**：**本包当前不存在可选目标**。2121 个版本中 5.1 线 1771 个、5.0 线 223 个，最高线即 `5.1.4.x`；**没有 5.1.5 / 5.2 / 6.x**。名为「跨版本」的只能是换包（如 `SqlSugarCoreNoDrive` 同为 5.1.4.221；`SqlSugar.GBaseCore` 5.1.4.270、`SqlSugar.ClickHouseCore` 5.1.4.201 属分驱动/其它数据库形态，与本仓 PostgreSQL 单库无关）——不构成本仓的升级选项。
- 217 之后**无预发布版**（218–221 均为正式版，`listed=true`）。
- 上游 217→221 提交标题（GitHub API，DotNetNext/SqlSugar，28 条）：仅 Gbase/Sqlite/MySqlConnector 依赖推进、bulkcopy 报错信息、`Update Ado.ExecuteCommandAsync`；**无一条涉及事务 / scope / 连接上下文语义**（关键词扫描仅命中 2/28，且均为 Gbase、ExecuteCommandAsync）。

---

## 二、G7 复现与影响面

### 2.1 复现口径修正（重要）

台账 G7 原描述是「共享单例 SqlSugarScope 同一实例并发操作会撞在同一连接上」。**本次实测推翻了这个归因的强度**：

| 形态 | 并发模式 | 失败数 @5.1.4.217 | 不同后端 PID |
|---|---|---|---|
| prod 多配置共享单例 | 只读并发（冷/热 × async/sync × 同上下文/独立上下文） | **0/60（全部）** | 19–20 |
| 单连接共享 scope | 同上 | **0/60（全部）** | 19–20 |
| 两独立 scope 10+10 | 同上 | **0/60（全部）** | 18–20 |
| 每操作新建 scope（对照） | 同上 | 0/60 | 20–21 |
| **prod 多配置共享单例** | **父上下文先 BeginTran（连接被持开）再 20 并发** | **52/60（86.7%）** | **3** |
| 单连接共享 scope | 同上 | 41/60（68.3%） | 3 |
| 两独立 scope 10+10 | 同上 | 21/60（35.0%） | 18 |
| 每操作新建 scope（对照） | 同上 | **0/60** | 20–21 |

- 失败异常 **100% 为 `NpgsqlOperationInProgressException: A command is already in progress`**（46 次命中 `select pg_backend_pid()`，6 次命中 `SELECT COUNT(1) FROM (SELECT "id" FROM "sys_user" …`），与台账记录的异常类型一致。
- 结论：**纯并发查询本身是安全的**。触发条件是「执行上下文内已有一条被持开的连接（＝事务在开）＋ 在该上下文内并发发起操作」——此时并发操作复用了同一连接。
- 机制（反编译证据）：`SqlSugarScope.GetContext()` 以 `_configs.GetHashCode()` 为键、按调用栈「是否含 async 方法」分派到 `CallContextAsync<T>`（AsyncLocal）或 `CallContextThread<T>`（ThreadLocal）。非事务态下 `IsAutoCloseConnection=true` 使每次操作从连接池另取连接（实测 20 并发 = 20 个不同 `pg_backend_pid()`），因此并发安全；**事务期间连接被持开且在同上下文的并发操作间被复用**，才产生 `A command is already in progress`。

### 2.2 真实影响面

- **高危**：单个 HTTP 请求内 `Task.WhenAll` 并发发起多个 DB 调用，尤其是 `[UnitOfWork]`（`UseTranAsync`）包裹的事务内并发——实测失败率 **86.7%**（共享单例形态）。
- **低危**：跨 HTTP 请求并发。每个请求有独立 `ExecutionContext`，实测「20 个独立执行上下文」形态 **0/60 失败**。台账中「未覆盖：HTTP 并发请求直连仓储之间的碰撞」这一担忧，在本次口径下**未取得支持证据**。
- 另发现一条同根危害（见 §三 E6）：事务内改用**独立线程**（ThreadLocal 分支）反而**逃逸事务**——线程上的写落到了另一条连接、自动提交，事务回滚管不到它。即 `_dbSerial` 这类「换线程串行」的缓解手法在事务内会换来正确性问题。

### 2.3 升级是否解决

**否。** 5.1.4.221 同一探针、同一口径、同 3 轮：

- prod 多配置共享单例 + 事务持开并发：**45/60 失败**（217 为 52/60）
- 单连接共享：39/60（217 为 41/60）；两独立 scope：20/60（217 为 21/60）；每操作新建 scope：0/60
- 其余全部形态仍 0/60

反编译侧佐证：`SqlSugarScope` 的实现体在 217 与 221 **完全一致**（diff 无输出）——G7 的根因代码一字未动。

---

## 三、G9 复现与影响面

### 3.1 台账原命题：**未复现**

「裸 `Ado.BeginTran/Insert/CommitTran` 在线程执行上下文残留连接状态，污染其后事务」——本次以与本仓 `SqlSugarSetup` 同形态的 scope（多配置 + AOP + 软删全局过滤器）逐条实测，**未复现**：

| 场景 | 结果 @217 | 结果 @221 |
|---|---|---|
| A：同上下文，裸 Ado 提交 → 随后 `UseTranAsync` 抛异常 | 回滚生效，残留 0 | 回滚生效，残留 0 |
| A2：跨 `Task.Run`，同上 | 回滚生效，残留 0 | 回滚生效，残留 0 |
| B：对照——首步走 `UseTranAsync`，随后失败事务 | 回滚生效，残留 0 | 回滚生效，残留 0 |
| C：裸 Ado 提交后换**全新 scope 实例**再失败事务 | 回滚生效，残留 0 | 回滚生效，残留 0 |
| E3：`BeginTran → Insert → CommitTran` 后的状态 | 干净：`Ado.Transaction=null`、`ConnState=Closed`、`IsAutoCloseConnection=True`；其后失败事务仍回滚生效 | 同 |
| E4：`BeginTran → Insert → RollbackTran` 后的状态 | 干净；其后失败事务仍回滚生效 | 同 |
| A/A2/B/C × 2 轮 = 8 组合 | **8/8 回滚生效** | 8/8 |

→ 「裸 Ado 事务序列本身留下残留」在本口径下**不成立**（不排除原探针形态不同，见 §六）。

### 3.2 复现出的**更严重且确定性**的变体：事务嵌套被静默提前提交

在 `UseTranAsync`（＝本仓 `[UnitOfWork]` 的实现路径）**内部**出现裸 `Ado.BeginTran/CommitTran` 时，`AdoProvider.CommitTran` 不做嵌套判定，会**直接把外层事务提交掉**；外层随后抛异常时 `RollbackTran` 变成 no-op：

| 场景 | 217 | 221 |
|---|---|---|
| **E1**：`UseTranAsync` 内混裸 `BeginTran/CommitTran`，外层抛异常 | `IsSuccess=False`，**残留 2/2 行**（应 0） | 同 |
| E1 重复 5 次 + 首跑 1 次 | **6/6 全部残留 2/2**（确定性） | **6/6 同** |
| E1c：`UseTranAsync` 内混裸 `Ado.RollbackTran`，外层抛异常 | **残留 1/2 行**（部分自动提交） | 同 |
| E2：对照——纯 `UseTranAsync` 抛异常 | 残留 0（回滚生效） | 同 |

这正是台账 G9 记录的症状签名（「insert 自动提交、回滚失效」），但**触发路径不同**：不是「裸序列之后残留」，而是「**裸事务方法混入既有事务内部**」。

### 3.3 另一条实跑阳性：事务内跨线程写入逃逸事务

- **E6**：`UseTranAsync` 事务内开独立线程做同步写入 → 写入**逃逸事务并自动提交**（失败事务回滚后，线程写的行**残留 1 行**）。217 与 221 同。根因与 G7 同源：ThreadLocal / AsyncLocal 双缓冲导致线程上拿到的是另一条上下文、另一条连接。
- **E5**（阴性对照）：裸 `Ado.BeginTran` 后经 `Insertable`（仓储路径）写入再 `RollbackTran` → 行残留 0，说明裸 Ado 与 `Insertable` 走**同一条连接、同一事务**，二者没有分叉。

### 3.4 升级是否解决

**否。** E1（6/6）、E1c、E6、A/A2/B/C 在 5.1.4.221 下**逐条给出与 217 完全相同的结果**。反编译佐证：`AdoProvider` 的 `BeginTran/CommitTran/RollbackTran` 实现体在 217→221 未变（该类型仅两处差异，见 §四）。

---

## 四、升级可行性与迁移面

### 4.1 证据 A：整仓编译（强证据，源码级兼容）

做法：把仓树整体复制到 `/tmp/apicheck/BaseAdminCore`（**仓内零改动**），仅把**副本**的 `src/Core/AdminCore.Core.csproj` 中 `SqlSugarCore` 改成 `5.1.4.221`，然后全量重编译：

```
dotnet build src/AdminCore.slnx --nologo -v q --no-incremental
  5.1.4.221 → Build succeeded. 24 Warning(s) 0 Error(s)
  5.1.4.217 → Build succeeded. 24 Warning(s) 0 Error(s)   ← 对照（同数）
```

覆盖 Core / Web.Core / Web.Entry / Test 四个工程，**0 error、告警数完全相同**（24 条为 Test 工程既有 analyzer/nullability 告警，与 SqlSugar 无关，217 基线同样存在）。

### 4.2 证据 B：公开签名逐条核对（ilspycmd 11.1.0）

对 16 个关键类型（`SqlSugarScope`/`SqlSugarClient`/`SqlSugarProvider`/`AopProvider`/`AdoProvider`/`QueryFilterProvider`/`CodeFirstProvider`/`SimpleClient\`1`/`ISqlSugarClient`/`ITenant`/`ICodeFirst`/`IAdo`/`IAopProvider`/`IQueryFilterProvider`/`IDataInfoCacheService`/`IContextMethods`）dump 公开成员签名并 diff：

- **117 条命中，217 与 221 逐条一致**（diff 无输出）；
- 全类型集合上**唯一差异**：`AdoProvider.IsOpenAsync` 由 `internal` 变 `protected`（可见性放宽，**非破坏性**，本仓未使用）。

### 4.3 证据 C：关键实现体比对

| 类型 | 217 vs 221 实现体 |
|---|---|
| `SqlSugarScope`（含 `GetContext` AsyncLocal/ThreadLocal 分派） | **完全一致** |
| `AdoProvider` | 仅 2 处：`IsOpenAsync` 可见性；`Ado.ExecuteCommandAsync` 内 `CancellationToken = CancellationToken` → `= cancellationToken`（上游修掉参数遮蔽 bug，正是 218 那条提交） |

### 4.4 本仓用到的 SqlSugar 面逐条核对

| 本仓用到的面 | 5.1.4.221 状态 |
|---|---|
| `SqlSugarScope(List<ConnectionConfig>, Action<SqlSugarClient>)` / `(ConnectionConfig, …)` | 存在，签名未变 |
| `Aop.OnError` `Action<SqlSugarException>` | 存在，签名未变 |
| `Aop.OnLogExecuting` / `OnLogExecuted` `Action<string, SugarParameter[]>` | 存在，签名未变 |
| `Aop.DataExecuting` `Action<object, DataFilterModel>` | 存在，签名未变 |
| `Aop.OnDiffLogEvent` `Action<DiffLogModel>` | 存在，签名未变（仍为同步回调，非 async） |
| `Ado.CommandTimeOut` / `Ado.SqlExecutionTime` / `Ado.SqlStackTrace` | 存在 |
| `QueryFilter.AddTableFilter<T>(Expression<Func<T,bool>>, FilterJoinPosition)` | 存在，签名未变 |
| `CodeFirst.InitTables(...)`（`Type[]` / 泛型重载） | 存在 |
| `CodeFirst.SplitTables()` → `SplitCodeFirstProvider` | 存在 |
| `EntityMaintenance` | 存在 |
| `Ado.SqlQuery<T>` / `SqlQueryAsync<T>` / `ExecuteCommand` / `ExecuteCommandAsync` | 存在（`ExecuteCommandAsync` **内部实现有变**，见 §4.6） |
| `SimpleClient<T>` → `Deleteable` / `Updateable` / `EnableDiffLogEventIF` | 存在 |
| `Storageable` / `ToPageListAsync` / `WhereIF` / `SplitTable()` / `SplitInsertable` / `ClearFilter<T>()` | 存在 |
| `GetConnectionScope(object configId)` | 存在，签名未变 |
| `UseTranAsync(Func<Task>)` / `UseTranAsync<T>(Func<Task<T>>)` | 存在，签名未变 |
| `ConfigureExternalServices.DataInfoCacheService` + `IDataInfoCacheService` 7 方法 | 存在（`SqlSugarDataCacheService` 编译通过） |
| `SqlSugarTransaction` / `TrasactionBegin/TranSuccess/TranFail` 事件 | **仍不存在**（与 217 同，G9 台账记录的 CS1061 依旧） |

**逐条结论：全部「存在 / 签名未变」，无「已移除」项。**

### 4.5 传递依赖与漏洞关系（SQLitePCLRaw pin）

实测（副本内 `dotnet list package --include-transitive`）：

| | 5.1.4.217 | 5.1.4.221 |
|---|---|---|
| `Microsoft.Data.Sqlite(.Core)` | 10.0.9 | 10.0.11 |
| `SQLitePCLRaw.bundle_e_sqlite3` / `.core` / `.provider` / `.lib.e_sqlite3` | **2.1.11**（滞后，故仓内显式 pin 2.1.12 补 GHSA-2m69-gcr7-jv3q） | **2.1.12** |
| `Npgsql` | 5.0.18 | 5.0.18（未变） |
| `MySqlConnector` | 2.2.5 | 2.6.2（本仓 PG 路径不活跃） |

**探针结论**：在 5.1.4.221 下**删除** `SQLitePCLRaw.lib.e_sqlite3` pin 后，解析仍为 **2.1.12**，`dotnet list package --vulnerable --include-transitive` 报 **"has no vulnerable packages"**，Core 编译 0 warning/0 error。

→ 升级到 221 后，该安全 pin 由「必需」变为「冗余」（保留无害，删除可减一处手工版本面）。请注意：这只是 restore 解析 + 漏洞扫描层证据，**未跑 SQLite 代码路径**（本仓 PG-only，该路径不活跃）。

### 4.6 需实测核对清单（编译通过 ≠ 行为不变）

1. **`Ado.ExecuteCommandAsync`**（218 上游明确修改，参数遮蔽修复）：本仓 `SqlSugarSetup` 种子、DDL、`SqlSugarRepository` 写路径都经此方法 → 必须回归种子 upsert / `InitTables` / 仓储写入。
2. **`Microsoft.Data.Sqlite 10.0.9 → 10.0.11` 传递升级**：SQLite 在本仓不活跃，理论无影响，但会在 restore 图里变化。
3. **建表 / 分裂表链路**：`CodeFirst.InitTables` + `SplitTables().InitTables` 未变签名，但需跑一次以确认 `HealColumnCase` / 租户库建表行为不变。
4. **AOP 回调时序**：`OnDiffLogEvent`（差异日志）、`DataExecuting`（雪花/审计/租户/机构/流水号填充）签名未变，仍需跑 `BatchXDiffLogTest` 等用例确认填充仍生效（`OnDiffLogEvent` 仍是同步 `Action<DiffLogModel>`，本仓 fire-and-forget 口径不变）。
5. **`EntityMaintenance` / `QueryFilter`**：签名未变，随全量回归覆盖。
6. **事务语义（G7/G9 相关）**：不是「待核对」而是**已实测未变**——升级不改善。

### 4.7 上游发布说明的可得性

`web_search` 工具本次不可用（HTTP 401 认证失败，非本任务可为），故**未取到官方逐版本 release notes**；改用 GitHub API 提交日志（`DotNetNext/SqlSugar`，`since=2026-08-13`，28 条）替代，并以反编译实现体 diff 交叉验证。因此「上游未改事务语义」这一判断的依据是**提交标题 + 实现体一致**，不是逐行上游代码审查。

---

## 五、建议与需授权点

> **本次评估不执行、不建议在无授权情况下执行任何版本改动**。以下三项均需人授权；本仓 `CLAUDE.md` 红线「SqlSugarCore 为唯一新 NuGet 包」＋「改动 NuGet 版本需人授权」，任何 csproj 改动都必须显式授权。

### (a) 保持 5.1.4.217 + 现有缓解 —— 推荐（本次评估倾向）

- **能解决什么**：什么都不变。G7/G9 仍是已知限制（P3），与升级与否无关。
- **代价**：0（不动版本）。
- **风险**：留存两条**无自动化守卫**的真实缺陷：① 事务内并发 DB 调用 → 实测 86.7% 失败；② 事务内混裸 `Ado.CommitTran` → 静默提前提交（6/6 确定性复现，且 `[UnitOfWork]` 正是被污染的那一层）。现有缓解（JobScheduler `_dbSerial` 串行、统一走 `[UnitOfWork]`/`UseTranAsync`）覆盖已知消费点，但靠约定不靠机制。
- **建议的替代动作（成本远低于升级）**：把两条实测出来的禁令变成机制守卫——① 事务内禁并发 DB 调用；② 事务内禁裸 `Ado.BeginTran/CommitTran/RollbackTran`（含跨线程逃逸）。落点可从「`SqlSugarRepository`/UnitOfWork 侧加断言或日志告警 + 回归单测（就用本评估的 E1/E6/`tran-open-inline` 三形态做用例）」起步。此项同样需授权（改代码）。

### (b) 升到同线最新补丁 5.1.4.221 —— 可选，但**不能作为 G7/G9 的修复手段**

- **能解决什么**：仅依赖与杂项层面——`Microsoft.Data.Sqlite 10.0.9→10.0.11`、`MySqlConnector 2.2.5→2.6.2`、Gbase8s、`Ado.ExecuteCommandAsync` 参数遮蔽修复、bulkcopy 报错信息；并让 `SQLitePCLRaw.lib.e_sqlite3` 的显式安全 pin 变为冗余。
- **代价**：csproj 一行版本号（改包版本＝人授权）＋ 全量回归一轮 ＋ 测试版观察窗口。
- **风险**：**低**。整仓编译 0 error（告警数与 217 同）、16 类型 117 条公开签名逐条一致、`SqlSugarScope` 实现体完全一致、`AdoProvider` 仅两处非破坏性差异。残余风险集中在 218 那条 `Ado.ExecuteCommandAsync` 的行为变化（本仓写路径高频使用）。
- **验证成本**：`dotnet build` 双工程 ＋ 仓内全量测试（实测基线 32+ 用例）＋ 种子/建表链路实测 ＋ 测试版（`http://192.168.1.202:5090`）观察窗口。
- **执行前需人授权**：① 改 `src/Core/AdminCore.Core.csproj` 的 `SqlSugarCore` 版本；② 是否同步删除 `SQLitePCLRaw.lib.e_sqlite3` pin；③ 跑全量回归并可接受 `ExecuteCommandAsync` 路径的回归成本；④ 测试版部署与观察窗口时长。

### (c) 跨版本升级 —— **当前无目标可选，不建议**

- **能解决什么**：无。`SqlSugarCore` 最高线即 `5.1.4.x`（2121 个版本中不存在 5.1.5 / 5.2 / 6.x）。可想象的「换包」路径（`SqlSugarCoreNoDrive` 同为 5.1.4.221；`SqlSugar.GBaseCore` 5.1.4.270 / `SqlSugar.ClickHouseCore` 5.1.4.201 属其它数据库分驱动）与本仓 PostgreSQL 单库场景无关，且违反「唯一新 NuGet 包 / 最小必要改动」。
- **代价 / 风险**：换包要重跑全部 ORM 面核对，收益为 0。
- **验证成本**：不适用。
- **执行前需人授权**：不适用（建议不作此选项）。

### G7 / G9 的正路（仅记录，不在本次授权范围）

实测给出的方向性证据，供后续「待触发立项」时取用：

1. **每操作独立 scope / scope 池化**：`fresh-per-op` 在**全部形态**（含事务持开并发）下 **0/60 失败**，是唯一全绿的并发形态 → 与台账 G7「修复方向：scope 池化/每操作独立 scope」一致，本次实测支持该方向。
2. **禁止事务内并发**：`[UnitOfWork]` 内并发是最危险的组合（86.7% 失败）。
3. **禁止事务内裸 Ado 事务方法**：E1 的 6/6 确定性提前提交是最严重的一条（静默数据错误，非报错）。
4. 三条都与「升级 SqlSugar 版本」无关——**版本升级不在修复路径上**。

---

## 六、本次评估的证伪边界

### 6.1 有实跑证据的结论

- G7 全形态 × 5 种并发模式矩阵（`/tmp/g7g9probe`，每格 3 轮 × 20 并发 = 60 次），**217 与 221 各跑一遍**；失败数、异常类型直方图、不同后端 `pg_backend_pid()` 计数均为实跑输出（§2.1 表格）。
- G9 §3.1 的 A/A2/B/C（各 2 轮）、§3.2 的 E1/E1b（6 次）/E1c/E2、§3.3 的 E5/E6、E3/E4 状态打印，**217 与 221 各跑一遍**，全部为实跑。
- 整仓编译门（副本内 `dotnet build src/AdminCore.slnx --no-incremental`，221 与 217 对照，均 24 warning / 0 error）。
- 反编译签名 diff（16 类型 / 117 条命中逐条一致）与实现体 diff（`SqlSugarScope` 一致；`AdoProvider` 两处差异）。
- 传递依赖解析与 `--vulnerable --include-transitive` 扫描（217 / 221 / 221-去 pin 三组）。
- 库面副作用已清理：探针表 `probe_g7g9_tx` 已 `drop`，`information_schema.tables` 查得 **count = 0**（探针只创建/写入/删除自己的探针表，未触碰任何 `sys_*` 业务表数据）。

### 6.2 只有「编译层 / 文档层」证据的结论

- **「源码级完全兼容」只证明编译通过，不证明运行期行为一致**。除 `Ado.ExecuteCommandAsync` 外，其余 API 的行为等价性属推断（签名一致 + 实现体一致），未逐一做行为级实测。
- 「上游 218–221 未触及事务/scope 语义」的依据是 **GitHub 提交标题 + 反编译实现体一致**；未做逐 commit 上游 diff，也未取到官方 release notes（`web_search` 本次 401 不可用）。
- 「221 下 SQLitePCLRaw pin 冗余」只有 restore 解析 + 漏洞扫描 + Core 编译三层证据；**未跑 SQLite 代码路径**。
- 版本发布日期来自 NuGet 注册源 `published` 字段，未做二次核对。

### 6.3 明确「未复现 / 环境不允许」的项（不得当作已被否证）

- **G9 台账原命题未复现**：裸 `Ado.BeginTran/Insert/CommitTran` 序列之后的「上下文残留 → 污染其后事务」在本口径下 **8/8 回滚生效、状态干净**。这不等于原探针是错的——原探针的宿主形态（测试容器？不同 scope 构造？不同 PG 驱动路径？）本次未复原，属口径差异而非结论冲突。本次采用了与本仓 `SqlSugarSetup` 同形态的 scope（多配置 + AOP + 软删全局过滤器）以尽量贴近生产。
- **G7 台账原命题「20 并发查询 ⇒ 6-8/20 失败」未复现**为「纯查询」形态：纯查询在**所有** scope 形态下均 0/60。本次只在「事务持开连接 + 同上下文并发」时复现，且失败率更高（52/60）。原记录的数字未能重现，差异原因未查明。
- **未做 HTTP 端到端并发压测**：无 Web 宿主探针、未启动 `Web.Entry`、无生产配置。因此「跨请求碰撞概率低」只有「独立 `ExecutionContext` 形态 0/60」的间接证据，非真实 HTTP 并发证据。
- **未跑仓内测试套件**：本次为只读评估，未执行 `dotnet test`（避免对 dev 库产生测试副作用），故「升级后全量回归」的成本是估计值而非实测值。
- **未评估下游 fork（IntelCore 等）**的影响与同步成本。
- 本次评估**未做任何版本改动**：仓库内既有文件（含 `src/Core/AdminCore.Core.csproj`、`NuGet.config`、所有 `.cs`）**均未被本评估修改**；唯一新增文件即本文件。

### 6.4 评估期仓库并发状态声明（重要，供审计）

- 本评估**开工瞬间**的工作区是**干净**的（首次 `git status --porcelain` 无输出）。
- 评估过程中**另一个并发会话**在同一仓工作，陆续产生了以下改动：`M docs/框架使用指南.md`、`M docs/框架差距清单.md`、`M docs/框架索引.md`、`M src/Core/SqlSugarSetup.cs`（其行数在评估期间由 1066 变为 1085，`sha256` 由 `c59a06c4…` 变为 `998456bb…`）、`?? src/Core/Logging/HttpClientBuilderExtensions.cs`、`?? src/Test/BatchAETechDebtTest.cs`。**这些均非本评估引入**。
- 本评估对仓库的**写操作只有一处**：新建 `docs/sqlsugar升级评估-G7G9.md`。`src/Core/AdminCore.Core.csproj`、`NuGet.config`、所有 `.cs` 文件在本评估全程**只读**。
- 唯一的版本号改动发生在**仓外副本** `/tmp/apicheck/BaseAdminCore/`（仓树 tar 复制），不落仓、不影响工作区。


## 结论已执行（批 AM，2026-09-26）

本评估的升级**已真正实施**：`SqlSugarCore` 5.1.4.217 → **5.1.4.221**（`src/Core/AdminCore.Core.csproj` 单点）。
- **验证链**：`dotnet restore` 成功（0 新增间接依赖）→ 严格门 `-warnaserror` **0W/0E** → 版本相关专项 **19/19**（`BatchAETechDebtTest` G7/G9 探针＋`BatchAEAdoTransactionGuardTest`＋`BatchDUnitOfWorkTest`＋`BatchXDiffLogTest`）→ **全量回归 335/335**（23m41s）。
- **逐条复验结论不变**：本文档此前在 5.1.4.221 上做的行为对比（G7 探测 45/60、G9 六类组合同结果）在**真实升级后**仍成立——G7（SqlSugarScope 单例并发限制）与 G9（裸 Ado.CommitTran 线程上下文残留/嵌套提前提交）**均未修复**，故两差距维持 P3 已知限制＋静态守卫＋`[UnitOfWork]` 纪律的既有处置。
- **口径同步**：`docs/框架索引.md`（GetConnectionById 逐连接 scope WHY、实体/分表实测口径）、`docs/框架使用指南.md`（InsertByObject oldData 判别、差异审计 opt-in、慢 SQL 堆栈首帧）的版本号已改为 5.1.4.221；差距清单 G9 行补记本次升级。
