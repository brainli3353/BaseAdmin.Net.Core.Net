# 验收报告 task_108_ha_job（定时任务：BackgroundService 调度器 + DB 持久化 + 防重入 + 管理六端点）

- 分支：`main`（接手会话直接开发，用户裁决不走流水线；无 worktree/分支）
- 代码提交：与本报告**同批单提交**（代码 + 测试 + 验收报告 + 约定 #13 文档三件；前驱以 git log 为准——含本报告的提交号随最后定稿，不硬编码自引用哈希）
- 执行人：接手会话（dev），2026-09-09
- 实跑口径：build/test 开发实跑（真实 PostgreSQL `huluyeye_admin`）；活体服务实跑（`dotnet run` 5090 端口 + psql 对照 + HTTP 六端点 + 两阶段登录）
- 任务书勘误：任务书验收段文件名写 `acceptance_task_015_ha_job.md`（015 为退休旧编号）——按"重做必新编号"制度，本报告按 108 落名

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/AdminCore.slnx` 0W/0E | **PASS** | 干净树实跑 **Build succeeded. 0 Warning(s) 0 Error(s)**（全 8 工程） |
| 2 | `dotnet test --no-build` 全绿 | **PASS** | **Passed! 32/32**（Failed 0 / Skipped 0，26s）——含本任务新增 JobSchedulerTest 9 用例（全连真实 PG）；既有 op_log 时序用例本轮亦绿（已知偶发 flake，见披露④） |
| 3 | 注册测试任务（10s）→ 每 10s 执行 | **PASS** | 活体：`sys_job_detail` 行 Interval=10 → 日志连续「定时任务 clean_sys_log 执行完成(scheduled)」；psql LastRunTime `18:31:26 → 18:31:56`（25s 内推进 3 周期）；测试「到期任务执行并落库 LastRunTime」 |
| 4 | CleanSysLogJob 保留期配置生效 | **PASS** | 测试「日志清理任务真实删超期行」（`SysJob:LogRetentionDays` 指向 3 天前 cutoff：10 天前行删、5 天前行留，双表）；活体默认 30 天日志「保留 30 天(截止 2026-08-10 …),清理 0/0 行」 |
| 5 | 防重入：执行中再触发 → 跳过 | **PASS** | 测试「防重入_执行期间到点跳过且执行后继续调度」（DelayMs=1500/Interval=1s：执行期间 2 次到点跳过 Executions 保持 1，执行后恢复 2）；「RunNow_上一轮未完成时拒绝」 |
| 6 | RunNow 手动立即执行 | **PASS** | 测试「RunNow立即执行_未注册拒绝」＋活体 `POST /api/sysJob/runNow?id=…` → 200，LastRunTime 即时推进（18:38:42） |
| 7 | 重启恢复：重启后自动重新注册 | **PASS** | 测试「重启恢复_宿主重启后调度继续」（首实例跑过 → StopAsync → 新实例 StartAsync → 继续触发）；活体启动日志「定时任务预载完成:数据库 1 行 / 代码注册 1 个 / 生效 1 个」 |
| 8 | 禁用不执行；重新启用恢复 | **PASS** | 测试「禁用任务不执行_重新启用后恢复」（Enabled=false 期间 3 轮 tick 零触发，enable 后恢复） |
| 9 | git commit + 验收报告附实测输出 | **PASS** | 同批单提交；本报告 §活体证据 附 psql/日志/HTTP 输出 |

## 交付范围（files 清单 + 契约外必要连带改动）

任务书核心交付物（全新增，9 文件）：

- **`src/Core/Job/IJob.cs`（新增）**：`IJob.ExecuteAsync(JobContext, CancellationToken)` 契约＋`record JobContext(JobName, Trigger, TriggeredAt)`（Trigger：`scheduled`/`manual`）；
- **`src/Core/Attributes/JobNameAttribute.cs`（新增）**：`[JobName("name")]` 调度名标记（标注 IJob 实现类；与 `[Service]` 双标记——Service 管 DI、JobName 管调度名）；
- **`src/Core/Job/JobScheduler.cs`（新增，20KB）**：`IJobScheduler`（RunNowAsync/GetRegisteredJobNames/GetScheduledJobNames）＋`JobScheduler : BackgroundService, IJobScheduler`（`[Service(ServiceType=typeof(IJobScheduler), Singleton)]` 零改 Program.cs）——5s 粒度常驻循环/启动预载/60s 快照刷新/[JobName] 反射扫描（重名抛）/防重入 ICacheService 原子锁/DB 串行 `_dbSerial`/降级自愈（DB 不可达不阻塞宿主）/失败隔离（任务异常 LogError 不退出）；
- **`src/Core/Job/CleanSysLogJob.cs`（新增）**：内置任务 `[JobName("clean_sys_log")]`——物理删 SysLogOp/SysLogLogin 超 `SysJob:LogRetentionDays`（缺省 30 天）行；
- **`src/Core/Entities/SysJobDetail.cs`（新增）**：`sys_job_detail`（JobName 64/Interval 默认 3600/Enabled 默认 true/LastRunTime/Remark 256）；`[IncreTable][SysTable]`，EntityTenant；
- **`src/Core/Services/Job/SysJobService.cs`＋`Dto/`×5（新增）**：Page(Name 模糊)/Add(已注册+库内唯一校验)/Update(增量,约定 #9)/Enable/Disable/RunNow（经 IJobScheduler 同触发路径）；无删除（禁用即停用）；
- **`src/Web.Entry/Controllers/SysJobController.cs`（新增）**：`api/sysJob` 六端点（page 查询式 add/update/enable/disable/runNow 动作式）；BizError→400＋ApiResult 统一出口（与 SysOrgController 同口径）；enable/disable/runNow 双源取 id（query 优先 body 兜底）；
- **`src/Test/JobSchedulerTest.cs`（新增）**：9 用例（代码注册/到期执行/未到期不执行/防重入/禁用不执行/RunNow 拒绝路径/重启恢复/无代码实现行跳过/日志清理真实删行）＋坑回归（共享 SqlSugar 客户端禁传可取消令牌——停止后仍可用）；真实 PG，`test_probe` 探针行自清理；
- **验收报告**：本文件。

契约外必要连带改动（2 文件，均披露）：

- `src/Core/CacheConst.cs`：＋`KeyJobLock = "sys_job:"`（防重入锁键前缀，约定 #13 常量入表）；
- `src/Test/EventBusTest.cs`：DI 一致性用例补 `services.AddCache(config)`（JobScheduler 单例经 AddAutoServices 入容器后其依赖 ICacheService 须注册，否则整容器构建失败——DI 一致性测试的连带面）。

## build / 回归

- `dotnet build src/AdminCore.slnx`：**Build succeeded. 0 Warning(s) 0 Error(s)**（全 8 工程，增量 ~8s）。
- `dotnet test src/AdminCore.slnx --no-build`：**Passed! 32/32**（Failed 0 / Skipped 0，26s）。`--filter FullyQualifiedName~JobSchedulerTest` 单独复跑 **9/9**。
- 全部 9 用例连真实 PostgreSQL（TCP 探测不可达 → Assert.Fail 显式降级，不冒充通过）；用例内按 `JobName == "test_probe"` 精确清理。

## 活体证据

**启动与调度日志**（`dotnet run --project src/Web.Entry --launch-profile http`，5090；dev Redis 需密码不入库 → 按 task_007/010 设计走"连接串缺失→回退 Memory"路径，输出目录副本注入空连接串跑验，**入库文件零改动**）：

```
info: AdminCore.Web.Entry[0]
      配置自检: 环境=Development … 缓存=Redis 已配置但连接串缺失 → 回退 Memory(需注入 ConnectionStrings:Redis)
info: AdminCore.Core.Job.JobScheduler[0]
      定时任务代码注册完成:1 个(clean_sys_log)
info: AdminCore.Core.Job.JobScheduler[0]
      定时任务预载完成:数据库 1 行 / 代码注册 1 个 / 生效 1 个
info: AdminCore.Core.Job.CleanSysLogJob[0]
      定时任务 clean_sys_log 执行完成:保留 30 天(截止 2026-08-10 18:31:26),清理 sys_log_op 0 行 / sys_log_login 0 行
info: AdminCore.Core.Job.JobScheduler[0]
      定时任务 clean_sys_log 执行完成(scheduled)
（10s 间隔连续重复,截略）
```

**周期执行 psql 对照**（sys_job_detail，Interval=10）：

```
LastRunTime = 2026-09-09 18:31:26.764764   （now=18:31:26）
LastRunTime = 2026-09-09 18:31:56.777475   （now=18:32:00,25s 内推进 3 周期）
```

**管理 API 全链路**（两阶段登录 admin/Admin@123：`POST /api/auth/login` → bindRequired=true＋setupSecret → python TOTP → `POST /api/auth/login/otp` → JWT；以下均带 Bearer token）：

```
GET  /api/sysJob/page                      → 200 {"total":0,"list":[]}
POST /api/sysJob/add {clean_sys_log,3600}  → 200 data=846991851925573
POST /api/sysJob/add 重复                  → 400 "任务 clean_sys_log 已存在"
POST /api/sysJob/add {ghost_job}           → 400 "任务 ghost_job 未注册代码(无 [JobName] 实现类)——先实现代码再建库任务"
POST /api/sysJob/update {id,interval:10}   → 200 操作成功
POST /api/sysJob/update {id:不存在}        → 400 "任务不存在"
POST /api/sysJob/disable?id=               → 200 操作成功
POST /api/sysJob/enable?id=                → 200 操作成功
POST /api/sysJob/runNow?id=                → 200 操作成功(LastRunTime 即时推进 18:38:42)
POST /api/sysJob/runNow {id:不存在}        → 400 "任务不存在"
GET  /api/sysJob/page                      → 200 {"total":1,"list":[{jobName:"clean_sys_log",interval:10,enabled:true,lastRunTime:"…18:38:42",remark:"G6活体验收-已更新"}]}
```

**重加行持续调度**（add→update 10s 后）：`LastRunTime=18:39:36`（now=18:39:40）——快照刷新前旧 JobName 快照即命中新行（GetFirstAsync by JobName），刷新后口径一致。

## 披露

1. **并发探针实证出框架级限制（G7，同批立账）**：AddSqlSugar 共享单例 SqlSugarScope 同一实例并发不安全——生产 scope 20 并发查询 6–8/20 失败（"A command is already in progress"/"Connection already open"）；对照组：手搓单 scope（无完整 AOP/无 InitTables）20/20 通过、两个独立 scope 实例 20/20 通过、单/多配置构造内部成员相同（非构造分支差异）。未隔离变量：完整 DataExecuting AOP/InitTables/逐连接 clients。**缓解**：JobScheduler 以 `_dbSerial` 串行自身三类 DB 操作（预载/任务执行/RunNow），G6 引入的调度器内部并发全消解。**未覆盖**：HTTP 并发请求直连仓储之间的碰撞（既有、低概率、非 G6 引入）→ 立 G7（P3，触发时立项）。`docs/框架差距清单.md` 已同步。
2. **坑回归固化**：向共享 SqlSugar 客户端传可取消令牌且令牌后被取消 → 命令缓存滞留已取消令牌，后续一切 SQL 直接 OCE（停止链路生产同险）。JobScheduler 全路径禁止向 SQL 传 stoppingToken（WHY 注释在案），测试「调度器停止后共享客户端仍可用」回归守护。
3. **活体环境**：dev Redis 启用密码（不入库）→ fail-fast 设计（task_010）下无法裸跑；验收以"输出目录配置副本置空连接串 → 回退 Memory"路径实跑（**入库 appsettings/模块配置零改动**，跑完还原副本）。单实例 Memory 缓存对 G6 验收面（防重入锁/调度）等效。
4. **既有 flake（不修，留痕）**：`EventBusTest.op_log_发布后由订阅者异步落库` 1s 契约边缘，套件负载下偶发 ~1055ms 超时（task_013 验收已披露同现象）；本批次全量 32/32 一轮绿，JobSchedulerTest 单独多轮绿。
5. **dev 数据操作留痕**：验收期间软删重建 `sys_job_detail` clean_sys_log 行（旧行 JobName 为前会话残留值 `CleanSysLogJob` 与注册名不符）；admin OTP 绑定因两阶段登录重绑机制轮换（软删旧绑定→登录重绑，dev 库自愈）。
6. **任务书文件名勘误**：任务书验收段写 `acceptance_task_015_ha_job.md`（015 退休编号）——按 108 落名。
7. **SqlSugarSetup.cs 未动**：实施中曾试验"单配置构造分支"隔离变量，反射证实单/多配置构造内部成员（`_configs`/`_configAction`）完全相同（无差异源），已回退——框架零改动，符合约定 #8。
