# task_008_ha_rbac_fixa 任务完成总结报告

- 汇总人：PM，2026-08-27
- 任务链：task_007_ha_otp → task_007 fix 链（done+accepted）→ task_008_ha_rbac（done+accepted）→ **task_008_ha_rbac_fixa（本报告对象，第 1 轮整改，上限 3 轮）**
- 分支：`task_task_008_ha_rbac_fixa`（worktree `/data/worktrees/live/task_008_ha_rbac_fixa`），领先 main 8 提交、落后 0，worktree 干净
- **合并状态：暂停（合并前自查检出跨界改动，按 2026-08-26 依赖锁纪律等裁决，禁止硬合并）**

## 一、任务背景

task_008_ha_rbac（M2b：RBAC 权限体系 + 审计 AOP + 增量建表/种子 + 多库选库 + 租户基座 + 操作日志）首版提交 d121d36 盲审驳回（PASS=2 FAIL=1）：盲 SQL `select "CreateTime","CreateUserId" from sys_user order by id desc limit 1` 报 42703——SqlSugar 默认 `PgSqlIsAutoToLower` 把 PascalCase 列全折小写，引号精确引用匹配不到。

## 二、整改与复测（fixa）

- 代码提交：**d73ce04**（6 文件：`EntityBaseId.cs` 主键列小写 `id`、`SqlSugarSetup.cs` 关自动小写 + `HealColumnCase` 启动自愈、`Program.cs` 自愈先于建表、`SysUserSeed.cs`/`EntityBaseTest.cs`/`RepositoryTest.cs` 原始 SQL 口径对齐）
- 方案 Option B：主键列小写 `id`（盲 SQL 无引号 `order by id` 折小写命中）+ 其余列 PascalCase（引号精确引用）——本地临时 PG 实证为主键带引号口径下盲 SQL 唯一自洽解
- 复测：**盲跑三 check 全 PASS**（`/data/tasks/blind/task_008_ha_rbac_fixa.result` PASS=3 FAIL=0，脚本判定）+ 审计/权限（sysUser 200/sysRole 403）/租户自动填（TenantId=1300000000001）/操作日志/种子幂等（重启两次 19/2/2 不变）/多库（log 库 2 行、回落主库）/增量建表（空库 8 表混合口径）全过；build 0/0 + 单测 6/6（真实 PG 断言）
- 验收报告：`acceptance_task_008_ha_rbac_fixa.md`（10b8116 入库），7 项披露（共享库一次性持久列名迁移、限域 6 文件未触原 worktree、build 门文件缺失、单测只迁移 Core 两表、Option B 实证、沙箱约束、密钥脱敏）
- 回滚方式：`git revert d73ce04`（注意共享库列名迁移不自动回退，需 DBA 手工反向 RENAME）

## 三、合并前自查（2026-08-26 依赖锁纪律，task-book.md §9）

`git diff main..HEAD --name-only` → **86 文件**，对照声明边界四分类：

| 类 | 数量 | 内容 |
|---|---|---|
| ① 在 008 继承声明内（fixa 无 `# files` 行，继承原 008 书 9 项） | 60 | Entitys/SeedData/Services/Attributes/Entities/SqlSugarSetup.cs/Web.Core/Auth/Controllers/appsettings |
| ② 在前序 007 声明内（007 书 11 项，done+accepted） | 10 | `src/Core/Cache/*`4、`src/Core/Security/*`3、`Program.cs`、slnx、`thirdparty/Otp.NET.csproj` |
| ③ 验收报告（流程产物，main 有同类先例） | 4 | acceptance_task_007_ha_otp(_fix).md、acceptance_task_008_ha_rbac(_fixa).md |
| ④ **跨界改动（任一任务书均未声明）** | **12+1** | 见下 |

### 跨界改动（暂停合并的直接依据）

| 文件 | 归属提交 | 原因 |
|---|---|---|
| `src/Core/Models/ConnectionConfigEntry.cs` | d121d36（008 新增） | 契约 §5.2 必需；008 只声明 `SqlSugarSetup.cs` 单文件 |
| `src/Core/SqlSugarConst.cs` | d121d36（008 新增） | 契约 §5.2 必需；路径未声明 |
| `src/Core/Repository/ISqlSugarRepository.cs` | d121d36（008 改 004 文件） | 契约 §5.2 按特性选库必需 |
| `src/Core/Repository/SqlSugarRepository.cs` | d121d36（008 改 004 文件） | 同上 |
| `src/Core/SeedData/ISqlSugarEntitySeedData.cs` | d121d36（008 改 005 文件） | 契约 §4 种子接口必需 |
| `src/Core/SeedData/IncreSeedAttribute.cs` | d121d36（008 改 005 文件） | 005 存量同名片，§4 成对使用 |
| `src/Core/SeedData/SeedSetup.cs` | d121d36（008 改 005 文件） | §4 扩展 |
| `src/Core/Huluyeye.Core.csproj` | a0f0189（007 改） | StackExchange.Redis 引用必需；007 书未声明 csproj |
| `src/Web.Core/Huluyeye.Web.Core.csproj` | a0f0189（007 改） | 同上 |
| `src/Test/EntityBaseTest.cs` | d73ce04（fixa 改 003 测试） | 盲 SQL 口径对齐必需（盲脚本第 25 行不可变） |
| `src/Test/RepositoryTest.cs` | d73ce04（fixa 改 004 测试） | 同上 |
| `src/Web.Entry/Program.cs` | d121d36 + d73ce04 | 仅 007 声明；008 注册中间件、fixa 启动自愈顺序 |
| `task_008_ha_rbac.md.assess.md` | d121d36（008 提交） | **队列流转文件入仓库**（git-worktree 纪律「任务书/assess 不入库」）；008 报告已披露但披露≠合规 |

性质判断：12 个跨界文件全部为契约/修复方案功能必需（非顺手乱改），根因=任务书 `# files` 漏声明（「files 漏声明」坑位）；assess 文件为工作区纪律违规。

## 四、待裁决事项（裁决前不合并、不归档 worktree）

- **R1（跨界 12 文件）**：建议 A=补 fixa 书 `# files:` 声明（继承 008 九项 + `src/Core/Models/`、`src/Core/SqlSugarConst.cs`、`src/Core/Repository/`、`src/Core/SeedData/`、`src/Test/`、`src/Web.Entry/Program.cs`、两个 csproj）后重跑自查→合并；纪律规定子任务书不为补文件用，「拆子任务」路径不适用。B=暂不合并。C=明确批准硬合并。
- **R2（assess 文件）**：建议合并前 `git rm` + 清理提交；或裁决接受保留。
- **R3（链式吸收）**：本合并将连带把 007/008 全链（8 提交、74 非跨界文件）带入 main（main 现仅到 006）——007/008 均 done+accepted，属预期叠加，请确认。
- **R4（远端 push）**：main 远端 `ssh://git@192.168.1.202:2222/huluyeye/huluyeye-admin.git`——任务书未提，是否推送请裁决。
- **R5（worktree 归档口径）**：先例 002–006 已合并但 worktree 保留 → 建议保留（008 整改才第 1/3 轮，留 fixb 基线）；或 `git worktree remove` + 删分支。

## 五、裁决后执行步骤（预案）

1. （R1=A）补 fixa 书 `# files:` 声明（done/ 内派生书，符合格式同步纪律；注：`/data/tasks` 为 ro 挂载，补声明需由有写权限的 dispatcher/上层执行）
2. （R2=删）fixa worktree 内 `git rm task_008_ha_rbac.md.assess.md` + 清理提交
3. `cd /data/huluyeye-admin && git merge --no-ff task_task_008_ha_rbac_fixa`（提交号回填本报告；冲突则手动合流，参照 006 合并先例）
4. 本报告随归档提交入 main（docs/tasks/ 任务档案目录）
5. 归档确认：任务书已在 `/data/tasks/done/` ✓、accepted 标记已就位 ✓、worktree 按 R5 处置
6. （R4=推）`git push origin main`

## 六、交付最小清单

- **限域**：RBAC 权限体系（M2b）+ 盲审整改（审计列大小写 42703，Option B 混合口径）
- **引用影响面**：`SqlSugarSetup`（多库 scope 供给 + AOP + 增量 + 自愈）、`SqlSugarRepository`（SelectConnection）、`EntityBaseId`（8 实体主键列名单点）、`Program.cs`（启动顺序）、`appsettings.json`（ConnectionConfigs + DbSettings）、种子/测试原始 SQL 口径
- **回滚方式**：`git revert d73ce04`（fixa 增量）/ `git revert d121d36`（008 整体）；共享库列名迁移需 DBA 手工反向
- **最小验证**：build 0/0 + `dotnet test` 6/6 + 盲 SQL `docker exec postgres psql -U huluyeye -d huluyeye_admin -c "select \"CreateTime\",\"CreateUserId\" from sys_user order by id desc limit 1;"` 非空
