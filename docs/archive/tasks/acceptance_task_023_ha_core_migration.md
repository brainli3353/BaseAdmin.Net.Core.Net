# task_023_ha_core_migration 验收报告

- 任务：框架核心代码迁移（Business → Core，恢复 Business 为业务预留层）
- 执行：开发（dev-b）
- 日期：2026-08-28
- 代码提交：`b85139542ca2e4abf36103409b926f40783ea187`（分支 `task_task_023_ha_core_migration`，worktree `/data/worktrees/admin/task_023_ha_core_migration`）

## 一、交付物清单（对照任务书「交付物」节逐项）

| 交付物 | 状态 | 证据 |
|---|---|---|
| 迁移后的 `src/Core/`（含全部系统实体/服务/种子数据） | ✅ | 13 实体→`src/Core/Entities/`；9 服务域 53 文件→`src/Core/Services/{Auth,Dict,File,Log,Menu,Org,Pos,Role/User}/`（各域含 Dto 子目录）；9 种子→`src/Core/SeedData/`。git 识别 75 处重命名（相似度 76–99%，历史保留） |
| 清空后的 `src/Business/`（仅剩 Class1.cs 占位 + csproj） | ✅ | `find src/Business -name "*.cs" | wc -l` 实跑 = 1（`src/Business/Class1.cs`，与 4d5a2e2 删除前的原始占位逐字节一致，含 BOM）；csproj 保留（补业务预留层 WHY 注释） |
| 全项目 namespace/using 重写完成 | ✅ | 残留检查 `grep -rn "Huluyeye.Business" src --include=*.cs`（排除 bin/obj）= 仅 Class1.cs 自身裸 namespace `Huluyeye.Business`（占位类属预留层，六条规则按设计不覆盖），其余 0 命中 |

### Class1.cs 冲突处置说明

HEAD~1 提交 `4d5a2e2`（「清理 SDK 模板残留 Class1.cs」）删除了三处 Class1.cs，与本任务书交付物「Business 仅剩 Class1.cs 占位」及验收断言「Business .cs 数 = 1」矛盾。**以本任务书（更新、更具体）为准恢复** `src/Business/Class1.cs`，内容与删除前逐字节一致（含 BOM，已脚本验证）。Core/Web.Core 的 Class1 未恢复（本任务书未要求）。

## 二、验收标准逐条证据

### 1. `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误

由系统提交后自动执行（2026-08-26 用户定，不占开发会话），**权威结果文件：`/data/tasks/build/task_023_ha_core_migration.txt`**（期望：0 警告 0 错误）。时间线说明：build 在任务提交后由系统触发，本报告提交时尚未生成，盲审/验收直接引用该文件判定。

### 2. `dotnet test src/Test --no-build` 全绿

随系统 build 流程执行，结果见同一结果文件（期望：既有测试套件全绿——迁移未改任何测试逻辑/断言，唯一改动是 `SysDictOrderingTest.cs` L2 的 using 重写，测试对象 `SysDictTypeService` 仅程序集归属变化）。

### 3. psql 验证：重启后 sys_user/sys_role/sys_menu/sys_dict_type 表仍正常创建（CodeFirst 未破坏）

静态数据流论证（逐函数读完，非函数名推断）+ 待盲审/验收环境实证：

- Program.cs L142/143：`db.HealColumnCase(typeof(SysUser).Assembly, typeof(SysConfig).Assembly)` / `db.InitTables(...同参...)`；L151/152：`db.InitSeeds(typeof(SysUser).Assembly)` / `db.InitSeedData(同参)`。
- 迁移后 `typeof(SysUser).Assembly` 与 `typeof(SysConfig).Assembly` **同为 Core 程序集**。全部 15 个 [SugarTable] 实体（13 Sys* + SysConfig + SysLogOp）与全部 9 个种子类均在 Core 内，扫描覆盖等价于迁移前（迁移前种子实现全部在 Business、Core 仅接口+扫描器）。
- 「同一程序集传两次」的影响已逐分支核对：`InitTables`/`HealColumnCase` 的 `SelectMany(a => a.GetTypes())` 会使每个类型出现两次，但 CodeFirst 建表（先查表存在性、只补缺列、不删不重建）与列名自愈（精确匹配即跳过；重命名后二次执行命中精确匹配分支跳过）均幂等，重复处理仅为冗余元数据查询，无 DDL 副作用。
- `InitSeeds`/`InitSeedData` 仅传单一程序集（Core），9 个种子恰好扫到、各执行一次（upsert 幂等）。
- 结论：CodeFirst 机制未被破坏；重启后四表创建行为与迁移前等价。实证由盲审/验收环境按任务书口径执行（启动服务 + psql 查表）。

### 4. 文件断言（交付时 worktree 裸盘实跑）

| 断言 | 期望 | 实跑 | 判定 |
|---|---|---|---|
| `find src/Business -name "*.cs" \| wc -l` | 1 | 1（仅 Class1.cs） | ✅ |
| `find src/Core/Entities -name "Sys*.cs" \| wc -l` | ≥13 | 15（迁移 13 + 原在 Core 的 SysConfig/SysLogOp） | ✅ |
| `find src/Core/Services -type d \| wc -l` | ≥9 | 19（Services 根 + 9 域 + 9 Dto 子目录） | ✅ |

**管道特征披露（盲审/验收执行环境请注意）**：`bin/`、`obj/` 为 gitignore 物但存在于磁盘，`obj/` 内自动生成的 `AssemblyInfo.cs` 等会被裸 `find` 计入。开发交付前已清理 worktree 全部 bin/obj，保证提交时刻与交付时刻裸盘断言通过；**若验收脚本在 worktree 内先执行 build 再跑 find，Business 下会再生成 3 个 obj 产物（计数变 4）**——此为构建管道固有特征，非交付偏差。判定建议以 git 跟踪态为准：`git ls-files 'src/Business/*.cs'` = 1。

### 5. `git diff main..HEAD --stat` 文件变更全部在 files 声明范围内

- 代码提交 `b851395` 变更 88 文件：75 重命名（Business→Core，两端均在声明目录 `src/Business/Entitys|Services|SeedData/`、`src/Core/Entities|Services|SeedData/` 内）+ 2 csproj（`src/Core/` 声明目录内；Business.csproj 属 `src/Business/` 项目根）+ 8 Controller（`src/Web.Entry/` 下，**files 声明仅列 `src/Web.Entry/Program.cs` 未含 Controllers 目录**，但任务书契约第 2 条明文要求 sed 覆盖「全项目 src/ 下所有 .cs 文件」且 Controller 的 using 不改则 build 必失败——两处条款以可编译性为准，Controller 改动为契约第 2 条的必然结果）+ Program.cs（声明内）+ SysDictOrderingTest.cs（`src/Test/`，禁止事项第 3 条已预置「测试引用的 namespace 会自动被 sed 重写覆盖」）+ Class1.cs 新增（交付物要求）。
- 验收报告本身（本文件）按历史先例（task_021 `616ab94` docs 提交模式）独立提交，不在代码提交内。

## 三、代码纪律符合性（「只做文件移动和 namespace/using 替换」）

- 75 个重命名文件 + 10 个引用方 .cs（Program.cs/8 Controller/1 测试）的内容差异经**脚本逐行验证**（difflib 行级比对 + 模式断言）：全部改动行均为 `namespace/using Huluyeye.Business.{Entities,Services.*,SeedData}` → `Huluyeye.Core.{...}` 的重写，无业务逻辑/方法签名/类定义/中文注释改动。
- 精确匹配符合契约第 2 条：按三条完整前缀（`Entities` / `Services.` 含子路径 / `SeedData`）替换，未做宽泛 `Huluyeye.Business` 全替换；Class1 的裸 `namespace Huluyeye.Business` 未被触碰。
- **勘误**：任务书契约第 2 条所写 `namespace Huluyeye.Business.Entitys` 与实际代码不符——实际命名空间为 `Huluyeye.Business.Entities`（目录名 Entitys、命名空间 Entities，历史遗留拼写差异）。按实际文本替换，目标命名空间 `Huluyeye.Core.Entities` 与任务书一致。
- BOM 一致性：全部改动文件的首行 BOM 状态与原 blob 逐一核对一致（含 Core.csproj——编辑工具曾剥离其 BOM，已恢复并复检）。
- csproj 唯一超「移动+替换」的内容改动 = JwtBearer `PackageReference` 自 Business 迁至 Core（契约第 3 条括号内许可项：「除非有特殊 PackageReference 需迁移」；AuthService 迁 Core 后必须直接可见 token 库，版本 10.0.11 不变）+ Business.csproj 预留层 WHY 注释（开发人格协议要求关键位置补注释）。两处均已在上文披露。

## 四、影响面与回滚

- **引用影响面**：Web.Entry（Program.cs + 8 Controller）、Test（1 文件 using）、csproj 包依赖（Core 新增 JwtBearer，Business 移除）。Web.Core 零代码级改动（仅 1 处 doc 注释提及 Business，按纪律未触碰，见「已知残留」）。slnx 无改动（项目路径未变）。
- **无数据库/序列化结构变更**：实体类完全未动（[SugarTable] 表名/列声明、种子数据 Id 分段全部原样），存量库零影响。
- **回滚方式**：`git revert b851395`（单提交自包含，git 重命名可完整逆向）。
- **最小验证**：系统 build 结果文件 + 上文文件断言 + 盲审按任务书口径实跑测试与 psql 查表。

## 五、已知残留（有意不处理，供终审知悉）

1. Program.cs L138 等 4 处 .cs 注释仍提及「Business 程序集/实现在 Business」——迁移后表述过期，但任务书「只做文件移动和 namespace/using 替换」禁止改动注释，留待后续文档同步任务。
2. Web.Core/Auth/IUserTokenStateProvider.cs L4-7 doc 注释同上述性质（「Business 层实现并注册」）。
3. `InitTables/HealColumnCase` 现收到同一程序集两次（Core, Core），冗余但幂等无害；如需消除可后续任务将调用点改为单参（属逻辑微调，本任务禁止）。

## 六、过程合规披露

- 执行前评估：`task_023_ha_core_migration.md.assess.md`（4 功能点/5 分/直行；因 `/data/tasks/` 只读挂载，按 .gitignore 的 `*.assess.md` 规则落 worktree 根，不入库）。
- build 未由开发会话执行（2026-08-26 用户定，系统自动）；未写验证脚本/攻击数据（系统模板化生成，2026-08-26 简化）。
- 交付前 worktree 已清理全部 bin/obj；`git status` 干净。
