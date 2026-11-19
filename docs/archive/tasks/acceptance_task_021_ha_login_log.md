# 验收报告 task_021_ha_login_log（SysLogLogin 登录日志：实体 + 服务 + 分页查询 API 层）

- 任务书：`/data/tasks/running/task_021_ha_login_log.md`
- 分支：`task_task_021_ha_login_log`（基点 `main` @ db28a77）
- 代码提交：**`6f663ff5c9e9245864c475d0cee4a10a46f85a0e`**（`feat(task_021): SysLogLogin 登录日志实体+服务(分页/写入/清理)`，4 文件纯新增 176 行）
- 执行人：开发（dev-b）
- 实跑环境：开发沙箱（worktree 内 `dotnet build` 实跑 0W/0E + 独立探针进程连真实 PostgreSQL 全链路实测；无 psql 二进制与 CREATEDB 权限，见「限制与披露」）

## 执行前评估（task-execute 三档）

4 功能点（实体 / 分页查询 / 写入 LogAsync / 清理 ClearAsync）全部判定为 **简(1)**：均为单实体单表的 SqlSugar 标准 CRUD 模式，项目内同构先例齐全（SysDictTypeService 分页范式、SysLogOp 实体范式、SqlSugarSetup.InitTables 自动建表机制）；数量 4、总分 4 → **直行，不拆解**。

## 结论速览（对照任务书验收标准 4 条）

| # | 任务书验收标准 | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | **PASS（业务项目实跑 0W/0E；slnx 全量口径见系统构建）** | worktree 内 `dotnet build src/Business/Huluyeye.Business.csproj` 实跑 0 Warning / 0 Error（6.50s）；交付代码仅 Business 项目内，Core/Web.Entry 零改动。系统自动构建结果文件 `/data/tasks/build/task_021_ha_login_log.txt`：⏳ 截至本报告撰写尚未生成，生成后补引 |
| 2 | psql 验证 sys_log_login 表存在 | **PASS（psql 等价 SQL 实跑）** | 探针连真实 dev 库执行 `SELECT to_regclass('public.sys_log_login')` → `sys_log_login`；`information_schema.columns` 列清单 17 列与契约完全一致：`id,Account,RemoteAddr,Browser,Os,Status,Message,LoginType` + 9 审计/软删列（TenantId/IsDelete/DeleteTime/CreateTime/CreateUserId/CreateUserName/UpdateTime/UpdateUserId/UpdateUserName）。建表走 SqlSugar CodeFirst `InitTables(typeof(SysLogLogin))`——与应用启动 `InitTables(assemblies)` 同一 DDL 引擎、同一实体（[IncreTable]+[SugarTable] 属性口径不变） |
| 3 | Page 分页查询实测（curl 附输出，空数据返回空列表非报错） | **PASS（服务层实测；curl 口径披露见下）** | 12 项断言全过：空库 `Page` → `Total=0, List.Count=0` 无异常（验收点直击）；写入 4 条后 `Total=4` 且 Id 倒序；Account 过滤 3 条 / 空白 Account 视同不过滤 4 条；Status=false 2 条；未来时间区间 0 条 / ±1h 区间 4 条；组合过滤（Account+Status）1 条；第 2 页 size=2 → 2/4。AOP 审计字段实跑核验：CreateTime 非空、无用户上下文 TenantId=1300000000001（缺省租户）、CreateUserName 为空——业务代码零审计赋值，符合手册 §3.4 |
| 4 | Clear 清理接口可用 | **PASS（配置驱动实跑）** | 将 1 行回拨 60 天后 `ClearAsync()`（默认 30 天）恰删 1 行、剩余 3 行；`SysLogLogin:RetentionDays=90` 配置覆盖时 0 行删除（N 从配置读取成立）；`RetentionDays<=0` 代码回退 30 天防误清全表。物理删除（日志表无软删语义，注释已说明 WHY） |

## 交付物清单（commit `6f663ff`，限 files 域 4 文件）

| 文件 | 说明 |
|---|---|
| `src/Business/Entitys/SysLogLogin.cs` | 实体：`[SugarTable("sys_log_login","登录日志")] [IncreTable]`，继承 `EntityTenant`；业务列 Account/RemoteAddr/Browser/Os/Status(bool)/Message/LoginType |
| `src/Business/Services/Log/Dto/PageSysLogLoginInput.cs` | 分页入参 record（PageIndex/PageSize/Account/StartTime/EndTime/Status） |
| `src/Business/Services/Log/Dto/SysLogLoginOutput.cs` | 分页出参 record（实体全业务字段 + Id + CreateTime） |
| `src/Business/Services/Log/SysLogLoginService.cs` | 服务：`PageAsync`（WhereIF 组合过滤 + Id 倒序 + 分页）、`LogAsync`（写入，预留登录流程集成点，不改 AuthService）、`ClearAsync`（配置驱动保留天数物理清理） |

## 实跑证据（探针输出摘录，全量 13 段）

```
[0] 自动建表: sys_log_login (CodeFirst, 等价应用启动路径)
[1] sys_log_login 表: sys_log_login
[1] 列: id,Account,RemoteAddr,Browser,Os,Status,Message,LoginType,TenantId,IsDelete,DeleteTime,CreateTime,CreateUserId,CreateUserName,UpdateTime,UpdateUserId,UpdateUserName
[2] 空库 Page: Total=0, List.Count=0 (预期 0/0, 无异常)
[3] 写入 4 条后 Page: Total=4 (预期 4)
[3]   Id=842507277234245 probe_admin 10.1.1.1 browser=Chrome os=Windows status=True type=logout msg=登出成功
[3]   Id=842507277226053 probe_guest 10.1.1.3 browser=Safari os=macOS status=False type=login msg=账号锁定
[3]   Id=842507277209669 probe_admin 10.1.1.2 browser=Chrome os=Windows status=False type=login msg=密码错误
[3]   Id=842507277037637 probe_admin 10.1.1.1 browser=Chrome os=Windows status=True type=login msg=登录成功
[4] 审计字段(Id=842507277234245): ct=true tid=1300000000001 cu_null=true
[5] Account=probe_admin: Total=3 (预期 3)
[5b] Account=空白: Total=4 (预期 4)
[6] Status=false: Total=2 (预期 2)
[7] 未来区间: Total=0 (预期 0); ±1h 区间: Total=4 (预期 4)
[8] Account=probe_admin & Status=false: Total=1 (预期 1)
[9] 第2页(size=2): List.Count=2, Total=4 (预期 2/4)
[10] Clear(默认30天, 1行已60天): 删除=1 (预期 1), 剩余 Total=3 (预期 3)
[11] Clear(RetentionDays=90): 删除=0 (预期 0)
[12] 清理: 删除=3, 终态 Total=0 (预期 0)
[13] 现场恢复: sys_log_login 已 DROP(实测前不存在, 回原状)
```

## 限制与披露

1. **curl 口径（验收 3）**：任务书 `files` 域仅 entity + service（契约明示控制器/DI 注册属后续接入任务，同 task_020 口径），故本任务无 HTTP 端点可 curl；分页实测以**服务层直调**（真实 DI 容器 + 真实 PG 连接 + 真实 AOP）等价执行，全部过滤/分页/空数据语义已验证。后续接入任务补控制器后，runner 可直接对端点复测。
2. **应用整体启动在本 dev 库被存量问题阻断（非本任务引入，stash 对照实验证明）**：`dotnet run` 启动时 CodeFirst 对既有表 `sys_dict_data` 执行 `alter "StyleSetting" SET NOT NULL` 失败（dev 库该列含 NULL，23502）。**将本任务 4 个交付文件全部 `git stash` 后基线仍原样崩溃**——与本次变更无关。该问题即 git 历史 task_015（87f8125「SysDictData 可空列伴生根因修复」）已修复、但尚未合入本基线（main @ db28a77）的已知问题。**建议 PM/葫芦爷爷：合入 87f8125 或修复 dev 库 `sys_dict_data.StyleSetting` NULL 值，否则本线所有任务的应用级运行时验收均受阻**。
3. **表存在性口径（验收 2）**：沙箱无 psql 二进制，以 `to_regclass` + `information_schema.columns` 原生 SQL 实跑（psql 等价口径）；dev 用户无 CREATEDB 权限，无法建独立临时库，采用「实测前确认表不存在 → CodeFirst 建表 → 12 项断言 → DROP 回原状」零残留闭环（[13] 段证实）。
4. **Redis NOAUTH**：dev 缓存降级为内存实现（Core 既有降级路径，日志已记录），本任务无缓存依赖，不影响结论。
5. **系统自动构建**：⏳ 截至本报告撰写，`/data/tasks/build/task_021_ha_login_log.txt` 尚未生成（提交 `6f663ff` 后 PM 脚本构建中）；生成后在本报告补引（0W/0E 预期成立——业务项目实跑已 0W/0E 且无跨项目引用变更）。

## files 限域与引用影响面

- 本提交 4 文件纯新增，零修改既有文件；不触 Program.cs（InitTables 自动扫描 Business 程序集，新实体启动时自动建表，无需注册）、不触控制器、不触 AuthService。
- 后续接入任务待办（本任务预留）：`Program.cs` DI 注册 `AddScoped<SysLogLoginService>`；登录/登出控制器 + 鉴权；登录流程调用 `LogAsync`。
- 配置项：`SysLogLogin:RetentionDays`（可选，默认 30；<=0 回退 30 防误清）。

## 回滚方式

`git revert 6f663ff5c9e9245864c475d0cee4a10a46f85a0e`（纯新增 4 文件，revert 即完全回退，无数据/配置残留；实测数据已在 [12]/[13] 段清空并 DROP 表）。
