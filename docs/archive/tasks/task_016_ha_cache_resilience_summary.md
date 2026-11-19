# task_016_ha_cache_resilience 任务完成总结报告

- 汇总人：PM，2026-08-27
- 任务链：task_007_ha_otp（ICacheService Redis/Memory 双实现，done+accepted）→ task_008_ha_rbac（M2b，done+accepted）→ **task_016_ha_cache_resilience（本报告对象，dep_on task_008_ha_rbac）**
- 分支：`task_task_016_ha_cache_resilience`（worktree `/data/worktrees/admin/task_016_ha_cache_resilience`），合并前领先 main 1 提交（d97abb2）、落后 0，worktree 干净
- **合并状态：已合并（fast-forward da7f235→d97abb2）+ push origin main**

## 一、任务背景

2026-08-27 发布门首战实测：main 稳定版部署 test 环境启动即崩——`CacheSetup.AddCache` 注册期同步强制 `ConnectionMultiplexer.Connect`（abortConnect 默认 true），Redis 不可达时 `RedisConnectionException` 未处理 → **整个应用无法启动**（依赖中间件故障致应用瘫痪）。另：appsettings.json 内 PG/Redis 地址为网桥 IP（172.18.0.x），容器重启漂移风险高 → 统一改容器名主机名（redis/postgres 容器已接入 agent 网络）。

## 二、实现（提交 d97abb2，2 文件，71+/47-）

- `src/Core/Cache/CacheSetup.cs`：
  - Redis 实现由注册期同步 Connect 改为**单例工厂延迟到首次解析**（WHY：注册期在日志管道配置之前，降级告警会丢；单例工厂语义天然满足「降级为应用生命周期定终身，不做运行期探测切换」）
  - Connect 失败（不可达/认证失败等）→ 捕获 → `LogError`（含目标地址 + 降级语义：单实例内存语义，恢复需重启应用）→ 返回 `MemoryCacheService` 等效实现（`IMemoryCache` 预注册入容器供降级路径复用），**不抛出不崩溃**
  - `Cache:Type` 显式 Memory → 行为不变；连接串缺失 → 退回 Memory（007 原语义保留）
- `src/Web.Entry/appsettings.json`：
  - `ConnectionStrings:Default` 与 `ConnectionConfigs[0].ConnectionString` 的 `Host` 由网桥 IP 改 `postgres`
  - `ConnectionStrings:Redis` 改 `redis:6379`（注释纪律保留：密码由环境变量注入不入库）
  - 其余配置节不动

## 三、盲审与验收

- 盲审脚本：2 项（构建标记 BUILD_OK + `dotnet build` 0 警告 0 错误，huluyeye-dev-a 容器内执行）→ **PASS=2 FAIL=0**（`/data/tasks/blind/task_016_ha_cache_resilience.result`，verdict PASS，脚本判定）
- 验收标记：`/data/tasks/accepted/task_016_ha_cache_resilience` 已就位（队列流转）
- 任务书其余验收标准（不可达 Redis 降级实测 / 正常路径回归 / 主机名化回归）：验收环节执行，输出日志未留存；如需复核按任务书验收标准段命令重跑

## 四、合并前自查（2026-08-26 依赖锁纪律）

`git diff main..HEAD --name-only` → **2 文件**，对照任务书 `# files: src/Core/Cache/, src/Web.Entry/appsettings.json`：

| 文件 | 声明范围 | 判定 |
|---|---|---|
| `src/Core/Cache/CacheSetup.cs` | `src/Core/Cache/` | 范围内 |
| `src/Web.Entry/appsettings.json` | `src/Web.Entry/appsettings.json` | 范围内 |

**无跨界改动** → 允许合并。执行方向（方向铁律，M1 教训）：主仓库内 `git checkout main` → `git merge task_task_016_ha_cache_resilience`（禁止在 worktree 里执行 `git merge main`——那是把 main 合入分支，main 永不更新、代码丢失于发布链）。合并方式：fast-forward（main 的 da7f235 为分支 d97abb2 直系祖先，无合并提交）。

## 五、归档确认

- 任务书：`/data/tasks/done/task_016_ha_cache_resilience.md` ✓
- 验收标记：`/data/tasks/accepted/task_016_ha_cache_resilience` ✓
- 盲审产物：`/data/tasks/blind/task_016_ha_cache_resilience.{result,review}` + `blind_test_task_016_ha_cache_resilience.{checklist,sh}` ✓
- worktree：按 002–006 先例（合并后保留）→ 保留 `/data/worktrees/admin/task_016_ha_cache_resilience` + 分支 `task_task_016_ha_cache_resilience`（清理时 `git worktree remove` + `git branch -d`）
- 远端：`git push origin main` 已执行（d97abb2 + 本报告归档提交）

## 六、交付最小清单

- **限域**：缓存初始化健壮化（Redis 连接失败降级 Memory + 连接串主机名化），2 文件
- **引用影响面**：`CacheSetup.AddCache`（降级语义；ICacheService 契约不变，业务侧零改动）；`appsettings.json`（开发连接串，生产连接串环境变量注入不受影响）
- **回滚方式**：`git revert d97abb2`（连接串 IP→容器名纯配置变更，无数据迁移，revert 干净）
- **最小验证**：`dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 + 盲审 PASS=2 FAIL=0
