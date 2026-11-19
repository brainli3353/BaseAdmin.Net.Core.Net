# task_id: task_015_ha_job
# subject: task_015_ha_job
# source: huluyeye
# created: 2026-08-28
# format: v3
# repo: huluyeye-admin
# priority: P2
# dep_on: task_024_ha_auto_service
# files: src/Core/Job/, src/Core/Entities/SysJobDetail.cs, src/Core/Services/Job/, src/Web.Entry/Controllers/

# 任务：[框架] 定时任务基础重写版（BackgroundService 调度器 + 任务注册 + 管理 API）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**重写说明（2026-08-28 葫芦爷爷）**：本版取代 2026-08-27 旧稿（已废弃）。依据最新架构定案对齐：①框架代码一律落 **Core/**（023）——SysJobDetail/SysJobService 落 Core 而非 Business；②服务注册**不声明入口**（024 [Service] 自动发现 + §99③ 开发与注册分离——调度器 HostedService 注册方式由实现方按仓库既有约定完成，任务书不指定 Startup/Program 具体行；③旧稿引用的 Web.Core/Startup.cs 不存在（入口仅 Web.Entry/Program.cs）。
**必读参考**：仓库 docs/框架开发参考手册.md §5.14（定时任务：特性式+持久化）+ §5.16（防重入：锁+running 标记）；现有 Core/Services/Log/SysLogLoginService.cs（021 交付，日志清理参考）。

## 背景

框架无定时任务能力（intel 养号巡检、报表定时、日志清理都需要）。实现**基础版**：BackgroundService 调度器 + 代码注册任务 + 数据库持久化（重启恢复）+ 防重入——Furion Schedule 的原生替代。

## 契约（按此实现，框架代码全落 Core/）

1. **调度器核心**（Core/Job/）：
   - `JobScheduler : BackgroundService`：
     - 启动时从数据库加载任务定义（SysJobDetail：JobName/Interval 秒/Enabled/LastRunTime）→ 注册到调度
     - 循环：每 5s 检查到期任务（Interval 到点）→ 触发执行（异步，不阻塞调度循环）
     - 执行异常：记录 ILogger + 更新 LastRunTime（不退出调度器）
   - **防重入**（§5.16 简化）：执行前 ICacheService 取锁（`sys_job:{name}`）——拿不到锁跳过（任务正在跑）
   - 停止：OnStopping 取消 token（正在执行的任务允许跑完）

2. **任务定义**（Core/Job/）：
   - `IJob` 接口：`Task ExecuteAsync(JobContext context, CancellationToken ct)`
   - 代码注册：`[JobName("xxx")]` 特性标记实现类 → 扫描注册（同事件总线模式）
   - 首个内置任务：`CleanSysLogJob`（日志清理：保留天数从配置读，默认 30 天）

3. **实体与 API**（Core/）：
   - `SysJobDetail`（继承 EntityTenant + [IncreTable]）：JobName（唯一）/Interval（秒）/Enabled/LastRunTime/Remark
   - `SysJobService`：Page/Add/Update/Enable/Disable/RunNow（立即执行一次）——管理 API `/api/sysJob/*`（控制器落 Web.Entry/Controllers/）
   - 启动预载：数据库任务注册（重启恢复）；数据库新增任务 → 重启生效（动态注册留待迭代；如实现方选择 Add 时动态注册亦可，注明即可）

## 交付物

- `/data/worktrees/admin/task_015_ha_job/src/Core/Job/JobScheduler.cs`（BackgroundService 调度器 + IJob/[JobName] 特性 + CleanSysLogJob 同批）
- `/data/worktrees/admin/task_015_ha_job/src/Core/Entities/SysJobDetail.cs`
- `/data/worktrees/admin/task_015_ha_job/src/Core/Services/Job/SysJobService.cs`（管理 API + Dto/ + 控制器同批）

## 验收标准

- `cd /data/worktrees/admin/task_015_ha_job && dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误；`dotnet test src/Test --no-build` 全绿
- 实测（验收报告附输出）：
  - 注册一个测试任务（间隔 10s）→ 运行后每 10s 执行（日志/副作用验证）
  - CleanSysLogJob：配置保留 1 天 → 执行后 sys_log_op 只留近 1 天（psql 验证）
  - 防重入：任务执行中（模拟 sleep 长任务）再次到点 → 跳过（日志记录"上一轮未完成跳过"）
  - RunNow：手动触发立即执行（不等到点）
  - 重启恢复：数据库任务在重启后自动重新注册（启动日志验证）
  - 禁用任务不执行；重新启用后恢复
- git commit（提交号写入验收报告），验收报告 acceptance_task_015_ha_job.md 附实测输出