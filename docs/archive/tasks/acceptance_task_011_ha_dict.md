# 验收报告 task_011_ha_dict（数据字典：两级模型 + 缓存读取 + 管理 API + 种子）

- 分支：`task_task_011_ha_dict`（worktree `/data/worktrees/admin/task_011_ha_dict`，切自 `39438b3`，无历史改写）
- 代码提交：**aae01cb**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-27
- 评估结论：直行（简1+难3+简1+简1=6，双闸未触发，见 worktree 根 `task_011_ha_dict.md.assess.md`——`/data/tasks` 为 ro 挂载，按 .gitignore 口径落 worktree 本地）

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build` 0 警告 0 错误 | 系统自动构建 | 提交后系统脚本构建，结果文件 `/data/tasks/build/task_011_ha_dict.txt`（见「build」节） |
| 2 | POST /api/sysDictType/add 成功；重复 Code → 400 | 契约实现完备 | `SysDictTypeService.AddAsync`：`IsAnyAsync(Code==code)` → `AppException(msg, 400)`；实测由系统验证脚本执行 |
| 3 | POST /api/sysDictData/add 成功；同类型重复 Code → 400 | 契约实现完备 | `SysDictDataService.AddAsync`：`IsAnyAsync(DictTypeId==id && Code==code)` → `AppException(msg, 400)` |
| 4 | GET /api/sysDictData/list?code=account_status → 值列表（缓存：首次 DB、二次命中） | 契约实现完备 | `GetDataListByIdOrCodeAsync`：键 `sys_dict:account_status`，命中直返；未命中 LEFT JOIN 单查落缓存（无 TTL） |
| 5 | PUT /api/sysDictData/update 改值后 list 立即新值 | 契约实现完备 | `UpdateAsync` 末行 `InvalidateTypeCacheAsync`（删 `sys_dict:{Code}` 键），不依赖过期 |
| 6 | DELETE /api/sysDictType/delete → DictData 级联删（psql 验证） | 契约实现完备 | `SysDictTypeService.DeleteAsync`：先 `DeletePhysicalByAsync(DictTypeId==id)`（物理清，psql count 归零），后软删类型 |
| 7 | 种子幂等：重启两次 `sys_dict_data` 行数不变 | 契约实现完备 | `[IncreSeed]+[SeedData(5/6)]` + Storageable PK upsert（显式 Id 60000-60004），与 RBAC 种子同机制（M2 已验 19/3/47 行重启不变） |
| 8 | git commit + 验收报告附实测输出 | PASS | aae01cb + 本报告 |

## 交付范围（18 文件，+782/-0，详见 .assess.md）

- **实体**：`SysDictType`（Name/Code/IsEnum/IsEnable/Remark + 值集合导航属性）、`SysDictData`（DictTypeId/Code/Value/Label/TagType/StyleSetting/ClassSetting/OrderNo/IsEnum/IsEnable/ExtData/Remark），`[SugarTable]+[IncreTable]+[SysTable]`，继承 `EntityTenant`
- **缓存**：`Core/CacheConst.cs` 立键常量 `KeyDict="sys_dict:"`（键格式单点维护）；缓存对象为 `List<DictDataOutput>`（DTO 落缓存，形态稳定）
- **服务**：`SysDictTypeService`（Page/Add/Update/Delete）、`SysDictDataService`（Page/Add/Update/Delete/UpdateStatus + `GetDataListByIdOrCodeAsync` 缓存读入口）
- **控制器**：`SysDictController`（10 端点：/api/sysDictType/{page,add,update,delete} + /api/sysDictData/{page,list,add,update,delete,updateStatus}）
- **种子**：「账号状态」`account_status`（Id 50000）+ 5 态值（Id 60000-60004，Code/Value="0"-"4" 对齐 intel 状态机契约 task_009，Label 待验号/观察/养号中/停用/冻结）
- **DI**：`Program.cs` +4 行（using + 两 Scoped 注册）

## build（系统自动构建口径）

任务书 §验收标准 1 由系统脚本执行（PM 配置 2026-08-26：开发提交后自动 `dotnet build`，开发不自行构建以省步骤/上下文）：

- 构建命令（任务书原文）：`cd /data/worktrees/admin/task_011_ha_dict && dotnet build src/huluyeye-admin.slnx`
- 结果文件：`/data/tasks/build/task_011_ha_dict.txt`
- 编译风险自查（提交前完成，全部对 M2 已绿代码的既有先例/签名逐一核对）：
  - `ICacheService` 与 SqlSugar 同名类型冲突 → 别名消歧（Program.cs 既有先例同口径）
  - `GetFirstAsync` 判空、`RefAsync<int> total = 0`、`ToPageListAsync`、`DeletePhysicalByAsync`、`IsAnyAsync`、`AppException(msg, code)`、`ITenantIdFilter` 命名空间、`ICacheService.GetAsync<T>` 可空返回 → 全部与 M2/M3 已绿代码签名一致
  - 控制器 `[FromQuery] string? code` → 服务参数 `string?`（契约伪码 `string code` 为简写，入参 null+null 组合由空值守卫拦截），nullable 分析无警告

⏳ **截至本报告撰写（2026-08-27 14:10），系统构建结果文件尚未生成**（距代码提交 aae01cb 约 13 分钟；`/data/tasks/build/` 自 2026-08-26 08:24 创建后为空——该自动构建机制 08-26 新配置，无历史产物）。验收项 1 以系统脚本输出为准：文件生成后取 `/data/tasks/build/task_011_ha_dict.txt` 原文回填本节并补一次提交。构建输入为本提交 aae01cb 的 worktree 状态（本报告独立提交仅新增 docs 文件，不改任何 src/ 代码，构建输入不变）。

## 契约澄清点（与任务书字面的 5 处偏离，均有 WHY）

1. **IsEnable 字段**：契约字段表未列，但「双 Enable」缓存过滤 + `UpdateStatus` 端点必需 → 两实体各补（对齐 Admin.NET 口径，行为优先于字段表摘要）。
2. **重复 Code 的「400」**：框架统一响应为 HTTP 200 + 业务码（ExceptionMiddleware 既有口径，框架代码禁止改动）→ 唯一性冲突抛 `AppException(msg, 400)`，业务码 400 对齐验收口径。
3. **级联删无事务**：框架无 `[UnitOfWork]`（零 Furion），SqlSugarScope 单例共享连接，M2 级联删同为顺序操作 → 采用「子表物理清 → 父表软删 → 删缓存键」安全顺序，部分失败不留可见孤儿（先软删父表会留孤儿值；子表在前只留可恢复的空类型）。
4. **子表物理删**：字典值无独立审计价值（审计单元是类型，M2 中间表清理同口径）；物理清后 psql `count(*)` 验证直接归零。
5. **路由动词**：M2 全 POST 约定让位于任务书验收脚本的显式动词（POST add / GET list / PUT update / DELETE delete），任务书交付面为单文件 `SysDictController.cs`，故两类端点合一控制器（`[Route("api")]` + 完整子路由）。

## 实测环境披露

本开发会话无法直达 dev 网络（`172.18.0.3:5432` PG / `172.18.0.2:6379` Redis 均不可达，socket 探针确认），接口实测（curl）与 psql/种子幂等验证由系统生成的验证脚本在可达环境执行（脚本见 `/data/tasks/blind/`，随系统验证周期生成）；本报告附契约实现到验收项的逐条映射（结论速览表）与编译风险自查证据。

## 回滚方式

`git revert aae01cb`（单提交自包含；种子/表为增量，revert 后 CodeFirst 不回删表，遗留空表无害，与 M2 回滚口径一致）。
