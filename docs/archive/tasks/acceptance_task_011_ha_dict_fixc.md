# 验收报告 task_011_ha_dict_fixc（整改 3/3：按盲审报告攻击项修复，对照验收标准复测）

- 任务书：`/data/tasks/running/task_011_ha_dict_fixc.md`（母任务 task_011_ha_dict，原任务书验收标准见 `/data/tasks/done/task_011_ha_dict.md`）
- 盲审 FAIL 依据：`/data/tasks/blind/task_011_ha_dict_fixb.review`（`<!-- verdict: FAIL -->`，PASS=1 FAIL=1 覆盖缺口=8，2026-08-27T10:35）+ `/data/tasks/blind/task_011_ha_dict_fixb.result`
  - `BUILD_UNVERIFIED`（构建门留档 `/data/tasks/build/task_011_ha_dict_fixb.txt` 缺失，build_watch 未产出 BUILD_OK）
  - `FAIL: 验收2: select count(*) from sys_dict_data`（`eval: line 8: syntax error near unexpected token '('`——裸 SQL 被 bash 解析）
- 分支：`task_task_011_ha_dict_fixc`（基点 `task_task_011_ha_dict_fixb` @ `7e85cf9`，含 fixa 全部代码修复 `1f8ee49` 与 fixb 全部处置）
- 代码修复提交：`1f8ee490fdbebc9979c51c46ce67100371e5f5eb`（fixa 交付，本分支继承；本轮逐行独立复验 + 实跑构建/离线测试取证，**零代码 diff**）
- 执行人：开发（dev），整改轮次 3/3（超限升级葫芦爷爷前最后整改轮）
- 实跑环境：开发沙箱（fixc worktree `dotnet build` 0W/0E 实跑 + 离线回归测试 7/7 实跑 + 脚本侧缺陷原样复现；沙箱不可达 172.18.x PG/Redis（TCP 探针实测），运行时项留 runner 复测环境，见「限制与披露」）

## ⚠ 环境侧重要披露（先读）：fixc worktree 误路由，已同路径替换为 admin 仓库 worktree

- 派发指定 worktree `/data/worktrees/live/task_011_ha_dict_fixc` 实际是 **quant-live 仓库**（gitea:claude/quant-live.git）的 worktree（分支 `task_task_011_ha_dict_fixc` tip = main tip `14a72f5`，目录内无 `src/` C# 代码）——与 fixb 轮同一误路由（派发侧 worktree 准备逻辑对 huluyeye-admin 任务误挂 quant-live 仓库）。数据字典模块代码不存在于该仓库，「只在此目录内读写/测试」对 C# 交付面字面不可执行。
- 处理：核实该 quant-live worktree 无本地提交、`git status` 干净（tip 与 main 重合、`git log main..task_task_011_ha_dict_fixc` 空）→ `git worktree remove` 移除 → **同路径**重建为 huluyeye-admin 仓库 worktree，分支 `task_task_011_ha_dict_fixc` 基点取 fixb 分支 tip `7e85cf9`（已含全部修复代码）。quant-live 仓库中分支 `task_task_011_ha_dict_fixc` 保留（指向 `14a72f5`，与 main 重合，无内容）——与 fixb 轮处置先例一致。
- 依据：母任务书 `files:`/验收标准均指向 huluyeye-admin（`/data/worktrees/admin/task_011_ha_dict`）；fixa/fixb 轮先例同为 admin 仓库 worktree（位于 /data/worktrees/live/ 下）。主工作区 `/data/huluyeye-admin`（main @ `760586c`）本轮未触碰。

## 结论速览

| # | 盲审项 / 任务书验收标准 | fixb 轮盲审 | 本轮复测 | 证据 |
|---|---|---|---|---|
| 1 | 构建 0 警告 0 错误 | PASS（fixb 轮已翻正） | **PASS（实跑复验）** | fixc worktree 实跑 0W/0E（16.18s，dotnet 10.0.111）；盲审构建对象 `/data/worktrees/admin/task_011_ha_dict` HEAD=`7e85cf9` 与 fixc 基点**逐字节同提交**（只读核验），同口径必然同结果 |
| 2 | 种子幂等：重启两次 `select count(*)` 行数不变 | FAIL（脚本语法错误） | **代码级 PASS（独立逐行复验）+ 脚本侧缺陷复现** | 机制链=每次启动全量重跑 `[IncreSeed]`（SqlSugarSetup.cs:471-523，无一次性标记）+ `Storageable().ExecuteCommand()` 主键 upsert（:536-546）+ 主键 `IsPrimaryKey=true`（EntityBaseId.cs:13）+ 种子显式确定性 PK 50000/60000–60004 → 二启 5 行全主键命中只更新不插入，count 恒 5；盲审命令为裸 SQL（复现见攻击项 2），**任何代码状态不可 PASS** |
| 3 | 构建门留档 BUILD_OK | BUILD_UNVERIFIED | **管线侧（非代码）** | `/data/tasks/build/` root 所有，沙箱实测不可写（`Permission denied`）；build_watch 运行于 runner/PM 侧，开发侧无产出通道——等价证据=本轮实跑构建输出（见攻击项 1），请 runner 在 build_watch 恢复后复核留档 |
| 4 | 接口实测（add/重复 Code 400/list 缓存两读/update 即时失效/级联删除） | 覆盖缺口（脚本未机械覆盖） | **代码级 PASS（本轮逐文件逐分支独立复验）+ 离线测试实跑** | 见「其余验收标准逐条核验」；运行时部分留 runner 复测环境（沙箱限制，复测命令见攻击项 2 末） |
| 5 | git commit（提交号写入验收报告） | 覆盖缺口 | **PASS** | 继承代码提交 `1f8ee49`（本轮复验）；本报告独立提交（提交号见「交付清单与提交」） |

**盲审 FAIL 项处置定性（本轮独立复核维持 fixb 结论）**：FAIL 验收 2 = 盲审脚本生成缺陷（任务书验收标准原文 `` `select count(*) from sys_dict_data` `` 为裸 SQL，生成器未包 psql 前缀即取为 shell 命令），**非代码缺陷，连续两轮（fixb/fixc）已披露并附复现，待 runner/PM 管线侧处置**；BUILD_UNVERIFIED = build_watch 留档缺失（管线侧）。代码面三轮（原交付 + fixa + fixb + 本轮复验）确认无未决缺陷，**本轮零代码 diff 为如实结论，非回避**。

## 攻击项 1：构建 + 构建门留档 —— 实跑复验 + 管线侧披露

**构建实跑（本轮 fixc worktree，非引用前轮结论）**：

```
$ cd /data/worktrees/live/task_011_ha_dict_fixc && dotnet build src/huluyeye-admin.slnx
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:16.18
```

（dotnet SDK 10.0.111；8 个工程全部编译成功，含 `Huluyeye.Web.Entry`/`Huluyeye.Business`/`Huluyeye.Test`）

**构建对象口径核验（只读，不写 admin worktree）**：盲审脚本验收 1 命令为 `cd $WT && cd /data/worktrees/admin/task_011_ha_dict && dotnet build src/huluyeye-admin.slnx`——第二个 `cd` 使构建实际落在 admin worktree。`git -C /data/worktrees/admin/task_011_ha_dict rev-parse HEAD` = `7e85cf9fc8946ea4f1c69a5312b94d7e48198` = 本 fixc worktree 基点，工作区干净（`git status --short` 空）→ 两 worktree 代码逐字节一致，fixc 实跑 0W/0E 即 admin 口径结果。

**离线回归测试实跑（本轮）**：

```
$ cd /data/worktrees/live/task_011_ha_dict_fixc && dotnet test src/Test
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s
```

（含 `SysDictOrderingTest`：断言分页 SQL `ORDER BY` 恰为 `"orderno" ASC,"id" DESC` 两列——fixa 修复 CS1061 的防回归项）

**构建门留档（管线侧披露）**：盲审脚本 G5 门检查 `/data/tasks/build/task_011_ha_dict_fixc.txt` 首行 `BUILD_OK`。该目录 `root:root drwxr-xr-x`，沙箱实测 `touch` → `Permission denied`；`build_watch` 进程/脚本不存在于本沙箱文件系统（`/data` 全域检索无实现文件）——留档产出通道在 runner/PM 侧，开发侧不可代行（代行将伪造管线证据，违红线）。**等价证据**：上实跑构建输出（0W/0E）。请 runner 复核 build_watch 对本任务的留档产出；若留档机制确认废弃，建议 PM 从生成器移除 G5 门或改为引用验收报告构建输出。

## 攻击项 2：种子计数检查 —— 脚本侧缺陷原样复现 + 代码级机制独立复验

**复现（本轮实跑，命令与盲审脚本 line 23 逐字一致）**：

```
$ bash -c 'select count(*) from sys_dict_data'
bash: -c: line 1: syntax error near unexpected token `('
bash: -c: line 1: `select count(*) from sys_dict_data'
$ echo $?
2
$ bash -c 'type select'
select is a shell keyword
```

bash 将 `select` 解析为保留关键字（选择循环），`(` 在解析期即语法错误——**解析失败与代码/DB 状态无关，任何提交在此命令下恒 FAIL**（exit=2）。根因在生成器：从任务书验收标准提取反引号内文本 `` select count(*) from sys_dict_data `` 直接作 shell 命令，未识别为 SQL、未包 `psql` 前缀。此缺陷 fixb 轮已披露（`acceptance_task_011_ha_dict_fixb.md` 攻击项 2），本轮维持定性，**连续两轮待 runner/PM 处置**（修复方向：生成器对裸 SQL 自动包 `psql -U ... -d ... -tAc`，或任务书验收标准改写为完整 shell 命令后重新提取）。

**代码级幂等机制（本轮独立逐行复验，非引用前轮结论）**：

1. `InitSeedData`（src/Core/SqlSugarSetup.cs:471-523）：每次服务启动扫描程序集内全部 `[IncreSeed]` 且实现 `ISqlSugarEntitySeedData<T>` 的类，按 `[SeedData(Order)]` 升序（同序按类名，跨重启稳定）**全量重跑**——无「已执行过」一次性标记，种子类每启必执行；
2. `StoreSeedData`（:536-546）：`conn.Storageable(list).ExecuteCommand()` = **主键 upsert**（已存在→更新、不存在→插入）；remarks 记录该 SqlSugar 版本（5.1.4.217）`ToStorage()` 不执行、`ExecuteCommand()` 才完整执行（分类+插入+更新）的实机探针结论；
3. upsert 锚点：`EntityBaseId.Id` 标 `IsPrimaryKey = true`（src/Core/Entities/EntityBaseId.cs:13），种子显式赋 Id（类型 `50000`、值 `60000–60004`，SysDictTypeSeedData.cs:24 / SysDictDataSeedData.cs:24-82），全部字段确定性（无时间戳/随机值）；
4. 推论：第二次（及之后）重启重跑种子，`sys_dict_data` 5 行全主键命中 → 只更新不插入 → `select count(*) from sys_dict_data` 恒为 5、两次重启输出相等。先例：同机制 M2 RBAC 种子（菜单 10000+/角色 20000+/授权 30000+/关联 40000+）已实机验证幂等。

**runner 复测命令（需可达 172.18.x 基础设施的环境；PG 172.18.0.3:5432 / db huluyeye_admin / 用户 huluyeye，密码见仓库 `src/Web.Entry/appsettings.json`，此处不落明文）**：

```bash
# 0) 服务启动（fixc worktree 根目录；http profile → http://localhost:5132）
cd /data/worktrees/live/task_011_ha_dict_fixc
dotnet run --project src/Web.Entry &

# 1) 登录取 token（开发账号，task_008 验收先例同口径）
TOKEN=$(curl -s -X POST http://localhost:5132/api/auth/login -H 'Content-Type: application/json' \
  -d '{"account":"<dev账号>","password":"***"}' | jq -r .data.token)
AUTH="Authorization: Bearer $TOKEN"

# 2) 类型新增成功 + 重复 Code → 400（用探针类型，勿动种子 50000）
curl -s -X POST http://localhost:5132/api/sysDictType/add -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"name":"fixc探针","code":"fixc_probe"}'
# → {"code":0,...,"data":<新Id>}；重放同 body → "code":400

# 3) 值新增成功 + 同类型重复 Code → 400
curl -s -X POST http://localhost:5132/api/sysDictData/add -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"dictTypeId":<探针Id>,"code":"0","value":"0","label":"fixc探针值"}'
# → code:0；重放 → code:400（同类型下已存在）

# 4) 缓存两读（首次 DB、二次命中）
curl -s "http://localhost:5132/api/sysDictData/list?code=account_status" -H "$AUTH"
# → 5 个值按 OrderNo 升序（待验号/观察/养号中/停用/冻结）；再读一次 → 内容一致（Redis 命中，可 redis-cli GET "sys_dict:account_status" 佐证）

# 5) update 后 list 立即新值（缓存失效实测）
curl -s -X PUT http://localhost:5132/api/sysDictData/update -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"id":60002,"code":"2","value":"2","label":"养号中改","tagType":1,"orderNo":2,"isEnum":true,"isEnable":true}'
curl -s "http://localhost:5132/api/sysDictData/list?code=account_status" -H "$AUTH"
# → OrderNo=2 的 label 立即为「养号中改」；测毕请改回「养号中」

# 6) 级联删除（用探针类型，psql 验证）
curl -s -X DELETE "http://localhost:5132/api/sysDictType/delete?id=<探针Id>" -H "$AUTH"
docker exec <pg容器> psql -U huluyeye -d huluyeye_admin -tAc \
  "select count(*) from sys_dict_data where \"DictTypeId\"=<探针Id>"
# → 0（子表物理清）；类型行软删保留（select "IsDeleted" from sys_dict_type where id=<探针Id> → true）

# 7) 种子计数两次重启（验收标准「重启两次行数不变」）
docker exec <pg容器> psql -U huluyeye -d huluyeye_admin -tAc "select count(*) from sys_dict_data"   # 第 1 次（服务首启后，预期 5）
# 停止并重启服务一次
docker exec <pg容器> psql -U huluyeye -d huluyeye_admin -tAc "select count(*) from sys_dict_data"   # 第 2 次（重启后，预期 5，两次相等）
```

## 其余验收标准逐条核验（代码级，本轮逐文件逐分支独立复验）

| 验收项 | 代码路径（本轮实读行号） | 核验结论 |
|---|---|---|
| POST /api/sysDictType/add 成功 | SysDictController.cs:37-42 → SysDictTypeService.AddAsync:47-68 | 空值守卫（:51-54）→ 唯一校验 → InsertAsync 返回新雪花 Id ✅ |
| 重复类型 Code → 400 | SysDictTypeService.cs:55-58 | `IsAnyAsync(t => t.Code == code)` → `AppException(…, 400)`（业务码 400 经全局异常中间件映射）✅ |
| POST /api/sysDictData/add 成功 | SysDictController.cs:77-82 → SysDictDataService.AddAsync:106-140 | 类型存在性（:108-112）+ 空值（:114-117）+ 同类型唯一（:118-121）后 InsertAsync ✅ |
| 同类型重复值 Code → 400 | SysDictDataService.cs:118-121 | `IsAnyAsync(d => d.DictTypeId == … && d.Code == code)` → `AppException(…, 400)` ✅ |
| GET /api/sysDictData/list?code=account_status | SysDictController.cs:69-74 → GetDataListByIdOrCodeAsync:43-80 | 空参守卫（:45-48）→ 跨租户类型解析（:49-52）→ 类型不存在/**停用**返空集**不落缓存**（:53-57，防负向缓存钉死）→ 命中直返（:60-64）→ 未命中 LEFT JOIN 单查只取双 Enable（:67-73）OrderNo 升序 → 落缓存无 TTL（:77-78）；种子 Code=account_status/Id=50000、5 值其下 OrderNo 0-4，可返回 ✅ |
| PUT update 后 list 立即新值 | SysDictDataService.cs:143-172 → InvalidateTypeCacheAsync:200-209 | 更新成功后 `RemoveAsync("sys_dict:{Code}")`（:171/:207），写路径主动失效不靠过期 ✅ |
| DELETE 类型级联删值 | SysDictTypeService.cs:100-110 | 子表 `DeletePhysicalByAsync`（:107，物理清→psql count 归零可验）→ 类型软删 `DeleteByIdAsync`（:108）→ 缓存失效（:109）；子表在前的失败语义见 :91-99 remarks（部分失败不留可见孤儿）✅ |
| 写路径缓存失效全覆盖 | AddAsync:138 / UpdateAsync:171 / DeleteAsync:183 / UpdateStatusAsync:196 / 类型 UpdateAsync:88 / 类型 DeleteAsync:109 | 值四条 + 类型两条写路径均主动删键，无遗漏 ✅ |
| 排序语义（fixa 修复回归面） | SysDictDataService.cs:91-95 链式双 `OrderBy`（SqlSugar 追加列语义，同 SysMenuService.TreeAsync 惯例） | 离线测试断言 `ORDER BY "orderno" ASC,"id" DESC` 本轮实跑 PASS ✅ |

## 交付清单与提交

- 代码修复提交：`1f8ee490fdbebc9979c51c46ce67100371e5f5eb`（fixa 交付，本分支继承；本轮逐行独立复验 + 实跑构建/离线测试取证）
- 本轮交付文件：本报告（`docs/tasks/acceptance_task_011_ha_dict_fixc.md`，按仓库约定验收报告归位 docs/tasks/）；**无代码文件变更**（三轮修复链 1f8ee49→fixb 处置均已就位，本轮独立复验确认无新增代码缺陷）
- 提交号补记：本报告提交 = `dfe233a`（task_task_011_ha_dict_fixc；本补记行为独立提交）
- 管线动作：无（本轮未合并其他分支；fixb 轮已将原任务分支 ff 至 `7e85cf9`，盲审构建对象已含全部修复）

## 限制与披露

1. 沙箱不可达 172.18.x PG/Redis（TCP 探针实测 UNREACHABLE）：接口实测（curl 五项）与种子计数两次重启实测本轮未执行，留 runner 复测环境（复测命令见攻击项 2 末，含探针类型隔离/种子还原提示）；代码级逐路径核验见上表。
2. 盲审脚本生成侧缺陷（runner/PM 管线域，本任务不修改盲审脚本），**连续两轮披露待处置**：
   - 验收 2 命令为裸 SQL 无 psql 前缀（bash 保留字 `select` 解析期语法错误，恒 FAIL，复现见攻击项 2）；
   - 验收 1 命令含 `cd $WT && cd /data/worktrees/admin/task_011_ha_dict` 双 cd（第二 cd 恒胜，构建实际落 admin worktree）——fixb 轮已把原任务分支 ff 翻正，该口径本轮同提交等价（见攻击项 1），不再阻塞。
3. 构建门留档通道（build_watch → `/data/tasks/build/`）在 runner/PM 侧，开发沙箱不可写（实测 Permission denied），开发侧以实跑构建输出作等价证据（见攻击项 1）。
4. fixc worktree 误路由（quant-live → admin 仓库同路径替换），过程与依据见文首披露；quant-live 侧同名空分支保留（无内容，可 `git -C /data/quant/live branch -D task_task_011_ha_dict_fixc` 清理，非必须）。
5. 本轮为整改上限轮（3/3）：代码面无未决缺陷（三轮累计复验），残余 FAIL 面全部位于管线侧（脚本生成缺陷 + 构建门留档）——若 runner 复测环境按本报告复测命令实测通过而规则化判定仍因验收 2 语法错误 FAIL，建议升级葫芦爷爷裁决（代码交付面已穷尽开发侧可行动作）。

## 回滚方式

1. fixc 分支处置：`git -C /data/huluyeye-admin worktree remove /data/worktrees/live/task_011_ha_dict_fixc` 后 `git branch -D task_task_011_ha_dict_fixc`（该分支仅含本报告提交，删除后 fixb tip `7e85cf9` 仍完整保留全部代码与处置，无任何代码损失）；
2. 本报告文件随分支删除，无需单独清理；
3. 主工作区 `/data/huluyeye-admin`（main @ `760586c`）未触碰，无回滚面；admin worktree（`7e85cf9`）与 fixb 分支不动，回滚不影响既有关卡状态。

## 红线自检

- 密钥脱敏：DB/Redis/JWT 凭据未落本报告明文（仅引用 `appsettings.json` 路径与 host:port；runner 复测命令中账号/密码以占位符呈现）；
- 无 pkill/pgrep、无资金/实盘操作、无 GPU 重活；
- `git add` 仅限交付文件（git-worktree 规范：禁 `git add .`）；worktree 交付时 `git status` 无残留（构建产物 bin/obj 为 .gitignore 覆盖的常规编译输出，非临时文件）；
- 未同步复制到主工作区 `/data/huluyeye-admin`（交付仅经 worktree 分支提交）。
