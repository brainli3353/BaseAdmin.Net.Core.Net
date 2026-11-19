# 验收报告 task_011_ha_dict_fixa（整改 1/3：按盲审报告攻击项修复，对照验收标准复测）

- 任务书：`/data/tasks/running/task_011_ha_dict_fixa.md`（母任务 task_011_ha_dict，原任务书验收标准 8 项见 `/data/tasks/done/task_011_ha_dict.md`）
- 盲审 FAIL 依据：`/data/tasks/blind/task_011_ha_dict.review`（`<!-- verdict: FAIL -->`，PASS=1 FAIL=2）
  - `PASS: 构建通过(BUILD_OK)`（构建门：结果文件不存在=跳过判定，非构建通过证明）
  - `FAIL: 验收1: dotnet build src/huluyeye-admin.slnx`（编译错误 CS1061，`SysDictDataService.cs(93,18)`）
  - `FAIL: 验收2: select count(*) from sys_dict_data`（命令缺 psql 前缀，bash 语法错误）
- 分支：`task_task_011_ha_dict_fixa`（基点 `task_task_011_ha_dict` @ 5c0a817）
- 代码提交：**`1f8ee490fdbebc9979c51c46ce67100371e5f5eb`**（`fix(task_011 fixa): 分页排序 ThenByDescending→链式 OrderBy 修复 CS1061,离线 SQL 测试防回归`，本报告附该提交实测输出）
- 执行人：开发（dev-a），整改轮次 1/3
- 实跑环境：开发沙箱（worktree 内 `dotnet build` 0W/0E 实跑 + 离线 SQL 测试实跑；沙箱网络不可达 172.18.x PG/Redis 基础设施，见「限制与披露」）

## 结论速览

| # | 盲审项 / 任务书验收标准 | 盲审结果 | 本次复测 | 证据 |
|---|---|---|---|---|
| 1 | 构建：`dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | FAIL（CS1061） | **PASS（翻正）** | 实跑 0W/0E（8.24s，BOM 还原后终态） |
| 2 | 种子幂等：`select count(*) from sys_dict_data` 两次重启行数不变 | FAIL | **代码级 PASS + 脚本侧缺陷声明** | 机制=Storageable 主键 upsert（代码注释明示"天然幂等"）+ 显式 Id 60000–60004 + M2 RBAC 种子同机制先例；盲审命令本身是 shell 语法错误（缺 psql 前缀），见「根因分析 §2」 |
| 3 | 验收标准 2–6：API 实测（add/重复 Code 400/list 缓存两读/update 即时失效/级联删除） | 脚本未覆盖 | **代码级 PASS** | 静态核验（服务层+控制器层逐路径）+ 离线 SQL 测试实跑；运行时部分留 runner 复测环境执行（沙箱限制，见披露） |
| 4 | 验收标准 8：git commit | — | **PASS** | 见头部代码提交；本报告独立提交引用之 |

**盲审 2 项 FAIL：1 项（构建）代码修复翻正，1 项（种子计数）判定为盲审脚本生成缺陷（命令缺 psql 前缀）+ 代码级机制成立。任务书其余验收标准无回归——本轮变更仅 2 行排序代码 + 1 个离线测试文件。**

## 盲审 FAIL 根因分析

### 攻击项 1：构建 CS1061（代码缺陷，已修复）

盲审输出：`SysDictDataService.cs(93,18): error CS1061: 'IQueryable<SysDictData>' does not contain a definition for 'ThenByDescending'`。

**根因**：`ThenByDescending` 是 `System.Linq.Enumerable` 对 `IQueryable<T>` 的扩展方法。`SysDictDataService.cs` 所在项目未启用 ImplicitUsings、文件无 `using System.Linq`，该扩展方法不在符号表内。SqlSugar 自有 `OrderBy` 是其 `ISugarQueryable` 扩展故可解析，LINQ 的 `ThenBy*` 族不可——同一表达式里混用了两套扩展体系，编译期即死。

**为何原交付未发现**：原提交走"系统自动构建"口径（PM 2026-08-26 配置：开发提交后系统脚本构建），原验收报告已如实标注"⏳ 截至报告撰写系统构建结果文件尚未生成"——开发侧当时未实跑完整构建，CS1061 直到盲审构建才暴露。本次整改已改为**实跑 `dotnet build` 取证**（0W/0E），并新增离线 SQL 测试防回归。

### 攻击项 2：种子计数检查（盲审脚本生成缺陷，非代码缺陷）

盲审脚本第 25 行原样为：

```bash
check "验收2: select count(*) from sys_dict_data（预期: 行数不变）" "select count(*) from sys_dict_data"
```

命令直接是裸 SQL（缺 `docker exec` / `psql` 前缀），在 bash 中 `select` 不是可执行命令 → 语法/命令不存在错误 → 恒 FAIL。**该 FAIL 与代码状态无关：任何提交（含种子代码完全正确的提交）在此命令下都无法 PASS。**

代码侧幂等机制成立（静态核验）：
- `SqlSugarSetup.StoreSeedData`（Core/SqlSugarSetup.cs:526）：`conn.Storageable(list).ExecuteCommand()`——主键 upsert，方法注释明示"已存在 → 更新、不存在 → 插入，天然幂等"；
- `SysDictDataSeedData`（`[IncreSeed] [SeedData(6)]`）：显式 Id 60000–60004、`DictTypeId=50000`、值确定性（无时间戳/随机值），重启重跑主键全命中 → 只更新不插入 → `count(*)` 不变；
- 同机制 M2 RBAC 种子（菜单 10000+/角色 20000+/授权 30000+/关联 40000）已实机验证幂等（M2 验收先例）。

**复测环境执行建议**（供 runner 修复脚本命令后对照）：启动服务两次，每次启动后执行
`docker exec <pg容器> psql -U <user> -d <db> -tAc "select count(*) from sys_dict_data"`，两次输出应相等。

### 披露：盲审脚本两处生成侧问题（runner/PM 管线域，本任务不修改盲审脚本）

1. 验收 2 命令缺 psql 前缀（如上）；
2. 验收 1 的 `WT` 以 `-name "task_011_ha_dict"` 检索 worktree（不含 `_fixa` 后缀），且命令内硬编码 `cd /data/worktrees/admin/task_011_ha_dict`。若复测脚本沿用该检索口径，构建输入是 admin 原 worktree（原代码，含 CS1061）而非本 fixa worktree，构建项将不翻正——请 runner 复测时确认构建对象为 fixa worktree（`task_task_011_ha_dict_fixa` 分支）。

## 修复方案（最小改动，限域）

### 变更 1：`src/Business/Services/Dict/SysDictDataService.cs`（PageAsync，2 增 1 删）

```csharp
var list = await query
    .OrderBy(d => d.OrderNo)
    // SqlSugar 多次 OrderBy 依次追加排序列(非 LINQ 的 ThenBy 语义,项目惯例同 SysMenuService.TreeAsync)
    .OrderBy(d => d.Id, OrderByType.Desc)
    .ToPageListAsync(Math.Max(1, input.PageIndex), Math.Max(1, input.PageSize), total);
```

- `ThenByDescending(d => d.Id)` → 链式 `.OrderBy(d => d.Id, OrderByType.Desc)`：SqlSugar 对 `ISugarQueryable` 的多次 `OrderBy` **依次追加排序列**（非覆盖、非 LINQ 的 ThenBy 语义），项目既有惯例（`SysMenuService.TreeAsync` 同口径）；
- 排序语义保持不变：`ORDER BY orderno ASC, id DESC`（同 OrderNo 时新 Id 靠后，列表页稳定排序）；
- 影响面：仅字典值分页查询（`GET /api/sysDictData/page`）；无接口契约变更、无共享路径（基类/枚举/泛型）变更、无历史数据影响。

### 变更 2：`src/Test/SysDictOrderingTest.cs`（新增，离线测试）

- 与 PageAsync 完全相同的链式排序调用 → `ToSql()` 离线生成 SQL（不连库、任何环境可实跑）；
- 断言 ORDER BY 恰好两列：第 1 列 `orderno` ASC、第 2 列 `id` DESC；
- 实跑证据（本次）：`Passed! - Failed: 0, Passed: 1`；
- 取证探针（临时，已删除）实跑生成的 SQL 全文：

```sql
SELECT "dicttypeid","code","value","label","tagtype","stylesetting","classsetting","orderno","isenum","isenable","extdata","remark","tenantid","isdelete","deletetime","createtime","createuserid","createusername","updatetime","updateuserid","updateusername","id"
FROM "sys_dict_data"
ORDER BY "orderno" ASC,"id" DESC
```

## 任务书验收标准逐项对照（原 8 项）

| # | 验收标准 | 判定 | 依据 |
|---|---|---|---|
| 1 | `dotnet build` 0 警告 0 错误 | **PASS（实跑）** | `Build succeeded. 0 Warning(s) 0 Error(s)`（8.24s） |
| 2 | POST /api/sysDictType/add 成功；重复 Code → 400 | 代码级 PASS | `SysDictTypeService.Add`：查 Code 存在 → `AppException(msg, 400)`；控制器 `POST api/sysDictType/add` 映射正确 |
| 3 | POST /api/sysDictData/add 成功；同类型重复 Code → 400 | 代码级 PASS | `SysDictDataService.Add`：同 `DictTypeId` 下查 Code 存在 → 400 |
| 4 | GET /api/sysDictData/list?code=account_status 返回列表（1 次 DB、2 次命中缓存） | 代码级 PASS | `GetByCodeAsync`：`GetAsync(sys_dict:{Code})` → miss 查库 → `SetAsync` 回填（SysDictDataService.cs:59–78） |
| 5 | PUT 更新后 list 立即返回新值（缓存失效） | 代码级 PASS | 数据写路径（增/改/删）均 `RemoveAsync(sys_dict:{Code})`（:138/:207）；类型写路径同（SysDictTypeService.cs:88/:109）——变更后读必走 DB 取新值 |
| 6 | DELETE 类型级联删 DictData（psql 验证） | 代码级 PASS | `SysDictTypeService.Delete`：先 `DeletePhysicalByAsync` 子表 → 再 `DeleteByIdAsync` 类型 → 缓存失效；物理删除可 psql 验证 |
| 7 | 种子幂等：2 次重启 `count(*)` 不变 | 代码级 PASS | 见「根因分析 §2」：Storageable 主键 upsert + 显式 Id + 确定性值 + M2 先例 |
| 8 | git commit（提交号写入验收报告） | **PASS** | 代码提交见头部；本报告独立提交引用之 |

2–7 的**运行时验证**（真实 curl/psql 输出）由 runner 复测环境执行——本沙箱不可达 PG/Redis（见下节），与原版验收报告同一口径披露。

## 限制与披露

1. **沙箱网络限制**：172.18.0.3:5432（PG）、172.18.0.2:6379（Redis）自本会话不可达（socket 探针超时），无 docker/psql 客户端。API/级联/种子计数等运行时项本会话无法实跑，由 runner 复测环境（盲审脚本从容器动态提取 redis requirepass，证明该环境 Redis 可达）执行。
2. **启动崩溃实跑披露（既有设计，非本轮引入）**：沙箱内启动服务实测（`ASPNETCORE_URLS=http://127.0.0.1:5091`，bin 目录，25s 超时）：
   `Unhandled exception. StackExchange.Redis.RedisConnectionException: It was not possible to connect to the redis server(s)` → 进程 exit 134，`/health` 000（绑定前崩溃）。崩溃点 `Program.cs:101`（缓存预热，task_007 起"快速失败优于带病运行"设计，预热在 `app.Run()` 前且无容错）。**本分支基点（39438b3）早于 task_010 fixa 线（9e24d04 存活/就绪解耦），本轮未改启动序列、未引入回归**；Redis 路径的等价容错属 task_010 fixa 线/后续任务域。runner 复测环境 Redis 可达，该路径不受影响。
3. **worktree 重建披露**：dispatcher 初建的 fixa worktree 指向错误仓库（quant-live），开发侧已从正确仓库（huluyeye-admin）以 `task_task_011_ha_dict` @ 5c0a817 为基点重建（`git worktree add -b task_task_011_ha_dict_fixa`），错误侧 worktree/分支已清理。
4. **盲审脚本缺陷**：见「根因分析 §2」与「披露」——验收 2 命令缺 psql 前缀、验收 1 worktree 检索口径不含 `_fixa`。盲审脚本属 runner/tester 域，本任务不修改。

## 回滚方式

`git revert 1f8ee49`（单提交、2 文件：1 改 1 增）；或分支级回滚：`git worktree remove --force /data/worktrees/live/task_011_ha_dict_fixa && git branch -D task_task_011_ha_dict_fixa`（与 dispatcher 侧清理口径一致）。无 DB/配置/接口契约变更，无数据回滚项。

## 最小验证清单（已执行）

| 验证 | 结果 |
|---|---|
| `dotnet build src/huluyeye-admin.slnx` | 0W/0E（8.24s） |
| 离线排序测试 `SysDictOrderingTest` | Passed（1/1，125ms） |
| 生成 SQL 取证 | `ORDER BY "orderno" ASC,"id" DESC`（两列、方向正确） |
| 沙箱启动探针 | 按预期在 Redis 预热处崩溃（沙箱无 Redis，证据见披露 §2） |
| 改代码三查 | 前端/接口契约：无变更；共享路径：仅字典值分页单点；历史数据：无 |

【人格：开发】
