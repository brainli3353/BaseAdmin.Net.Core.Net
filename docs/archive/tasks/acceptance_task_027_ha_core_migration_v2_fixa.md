# task_027_ha_core_migration_v2_fixa 验收报告（第 1 次整改）

- 任务书：/data/tasks/running/task_027_ha_core_migration_v2_fixa.md（盲审驳回整改，前次整改 0 次）
- 工作目录：/data/worktrees/live/task_027_ha_core_migration_v2_fixa（huluyeye-admin worktree，分支 task_task_027_ha_core_migration_v2_fixa，基于 main@20bdbf1）
- 交付提交：**c9d0af9**（26 文件 +27/−29，5 文件 git mv 保留重命名历史，业务逻辑零改动）
- 本报告提交：见 git log 紧随 c9d0af9 之后的报告提交

## 0. 环境修复声明（开工前置动作，需验收官知悉）

任务书指定 worktree 路径 /data/worktrees/live/task_027_ha_core_migration_v2_fixa 被**错建在 quant-live 仓库**（Python 量化仓，空净状态，其分支 task_task_027_ha_core_migration_v2_fixa 系错建残留）——huluyeye-admin 仓本无任何 task_027 worktree/分支，v2 迁移代码工作实际从未落库。处置：确认该 quant worktree 干净（0 改动、0 未跟踪）后移除，在同一路径以 admin 仓 main@20bdbf1 重建 worktree + 分支。此后全部读写/测试仅在此目录内，未向主工作区 /data/huluyeye-admin 同步任何副本。

## 1. 盲审 5 条 FAIL 逐条根因归因

| 条 | 归因 | 说明 |
|---|---|---|
| 验收1 build 0W/0E | **[脚本缺陷]+[环境问题]+[代码缺陷]** | 脚本 `find -name "task_027_ha_core_migration_v2"` 不匹配 `_fixa` 后缀 worktree 名，且原 worktree 错建于错误仓库 → WT 为空 → `cd $WT` 退化为 cd 到 HOME → MSB1009；盲审在带 docker 的执行机跑，本开发机无 docker。**但实跑基线证实真实代码缺陷**：main 编译失败 11 错误（与 task_025 任务书"11 错误阻塞流水线"记载完全吻合）→ 本轮已修复 |
| 验收2 test 全绿 | **[脚本缺陷]+[环境问题]** | 同验收1 的 WT 解析根因（MSB1009: src/Test 不存在于 HOME）；实跑验证见 §2（7/7 全绿） |
| 验收3 Core 零 Web.Core 引用 | **[脚本缺陷]+[代码缺陷]** | 脚本以宿主相对路径运行（当时 cwd 下无 src/Core/）；**真实代码缺陷**：9 个 Core 服务文件 `using Huluyeye.Web.Core.{Auth,Exceptions}` 而 Core.csproj 不引用 Web.Core → 编译必败。已按 v2/025 契约迁移 5 类型到 Core 并全局重写 using |
| 验收4 Business ≤1 文件 | **[脚本缺陷]** | 错 cwd（No such file or directory）；另计数口径含 MSBuild 在 obj/ 下自动生成的 3 个 .cs（任何构建后必然存在，gitignore 不入库）→ 源码实为 1 个 Class1.cs，合规 |
| 验收5 Core/Entities ≥13 | **[脚本缺陷]** | 错 cwd；实跑 15 ≥ 13 ✓ |

另两条 GAP：
- `psql 验证：重启后建表` → **[环境问题]**：本开发机无 docker/postgres（盲审执行机有）。静态论证：`Program.cs:143 db.InitTables(typeof(SysUser).Assembly, ...)` 扫 Core 程序集全部 [SugarTable] 实体，本轮改动不含任何实体定义/启动路径变更，建表数据流不变。
- `构建门留档缺失` → **[环境问题]**：build_watch 从未在可用构建环境产出 BUILD_OK（构建环境本身未建立，见 §0/§1 验收1）。

## 2. 复测证据（全部在本 worktree 实跑，SDK 10.0.111）

| 验收标准 | 实跑结果 | 判定 |
|---|---|---|
| `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | `Build succeeded. 0 Warning(s) 0 Error(s)`（日志 /tmp/task027_fix_build3.log；基线失败日志 /tmp/task027_baseline_build.log 留档 11 错误 1 警告） | PASS |
| `dotnet test src/Test --no-build` 全绿 | `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7`（日志 /tmp/task027_test.log） | PASS |
| `grep -rc "using Huluyeye.Web.Core" src/Core/` → 0 | 0 行 / 0 文件 | PASS |
| `find src/Business -name "*.cs" \| wc -l` → ≤1 | 源码 1 个（Class1.cs）+ obj/ 构建产物 3 个 = 4；**[脚本缺陷]** 口径含构建产物，源码面合规 | PASS（源码口径）/ 脚本口径待修 |
| `find src/Core/Entities -name "Sys*.cs" \| wc -l` → ≥13 | 15 | PASS |
| psql 重启建表 | [环境问题] 本机无 docker/postgres，静态论证见 §1 | 待盲审环境复跑 |

注：盲审脚本验收3 的 check() 以 grep 退出码判定——代码干净（0 匹配）时 grep 返回 1 恒报 FAIL，属断言口径缺陷，建议下轮脚本改为按计数判定（`[ "$(grep -rc ... | awk -F: '{s+=$2}' END{print s+0}')" = "0" ]`）。

## 3. 交付面（c9d0af9，diff 已逐行核验仅移动/替换）

- **类型迁移（git mv，重命名相似度 83%–98% 保历史）**：
  - `Web.Core/Models/ApiResult.cs` → `Core/Models/ApiResult.cs`（ns → Huluyeye.Core.Models）
  - `Web.Core/Auth/UserTokenState.cs` → `Core/Models/UserTokenState.cs`（ns → Huluyeye.Core.Models）
  - `Web.Core/Auth/IUserTokenStateProvider.cs` → `Core/Interfaces/IUserTokenStateProvider.cs`（ns → Huluyeye.Core.Interfaces，补 using Core.Models）
  - `Web.Core/Exceptions/{AppException,BadRequestException}.cs` → `Core/Exceptions/`（ns → Huluyeye.Core.Exceptions；依据 025 契约第 4 条"其他 Web.Core 类型引用同样处理"）
- **using 重写 20 文件**：Core 服务 9（Web.Core.Auth/Exceptions → Core.Interfaces/Models/Exceptions）+ Web.Core 3（PermissionMiddleware/ExceptionMiddleware/JwtAuthHandler）+ Web.Entry 控制器 8。CRLF 文件逐行锚定替换保持原行尾，无全文件行尾转换噪音。
- **清理 1 处**：SysDictDataService.cs 重复 `using Huluyeye.Core.Entities;`（v1 sed 残留，基线唯一警告 CS0105 的来源）。
- 三查自检：① 接口契约——ApiResult 字段/Json 序列化规则零变更，API 输出不变；② 共享路径——全仓旧 namespace 引用清零，残留仅 Web.Core 自身 3 个 namespace 声明 + Web.Entry→Web.Core 合法方向；③ 历史数据——实体/CodeFirst/DB 值零变更。

## 4. 回滚方式

`git revert c9d0af9`（单提交可整体回退）；或弃分支 `task_task_027_ha_core_migration_v2_fixa` 不动 main。

## 5. 最小验证（验收官可一键复跑）

```bash
cd /data/worktrees/live/task_027_ha_core_migration_v2_fixa
export PATH=/usr/lib/dotnet:$PATH DOTNET_ROOT=/usr/lib/dotnet
dotnet build src/huluyeye-admin.slnx        # 预期 0 警告 0 错误
dotnet test src/Test --no-build             # 预期 7/7 全绿
grep -rc 'using Huluyeye.Web.Core' src/Core/ | awk -F: '{s+=$2} END{print s+0}'   # 预期 0
find src/Core/Entities -name 'Sys*.cs' | wc -l   # 预期 15
```

## 6. 披露：main 并行重组（非本任务操作）

工作期间另一会话对 /data/huluyeye-admin main 执行了历史重组（reflog：回退 023 迁移代码 → reset 4d5a2e2 → cherry-pick 023 迁移为 164fb79），main HEAD 由 20bdbf1 变为 164fb79。本任务分支独立基于 20bdbf1，交付与全部复测证据不受影响；后续合并入重组后的 main 时需按内容合并（cherry-pick 件与 b851395 内容同构）。
