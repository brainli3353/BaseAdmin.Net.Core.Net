# 验收报告 task_013_ha_datascope（机构数据范围过滤：实体基类扩展 + SqlSugar QueryFilter 拦截 + 变更即失效）

- 分支：`main`（用户裁决「不再投递流水线，由 agent 直接在克隆仓执行」，本任务为直接执行模式，无 worktree/分支）
- 代码提交：与本报告**同批单提交**（代码 + 验收报告 + 约定 #13 文档；提交主题 `feat(数据权限): task_013 机构数据范围过滤落地——…`，前驱 `74804c4`；提交号以 git log 为准——含本报告的提交号随最后一次 amend 定稿，故此处不硬编码自引用哈希）
- 执行人：dev（agent 直接执行），2026-09-09
- 实跑口径：build/test 开发实跑（见「build / 回归」节）；行级过滤实测附 psql 输出 + SqlSugar 生成 SQL（探针 ToSql 捕获）

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/AdminCore.slnx` 0 警告 0 错误 | **PASS** | 干净树实跑 **Build succeeded, 0 Warning(s) 0 Error(s)**（全 8 工程），见「build / 回归」节 |
| 2 | `dotnet test src/Test --no-build` 全绿 | **PASS** | **Passed! 23/23**（Failed 0 / Skipped 0，11s）——基线 12 用例回归干净 + 本任务新增 11 用例全过；连续 3 轮复跑稳定（EventBusTest op_log 时序用例在 1s 契约边缘偶发 1049ms flaky，与本任务无关，复跑转绿） |
| 3 | 实测：机构树 + 用户分配 + 不同 DataScope → 行级过滤生效（psql 对比） | **PASS** | 见「实测证据」节：无过滤 4 行 / 机构范围过滤 2 行 / 仅本人 1 行 / 空集 fail-closed 0 行（SqlSugar 生成 `(1=2)`） |
| 4 | 数据范围=仅本人：只返回 CreateUserId==自己 | **PASS** | 测试「过滤器_仅本人_只看自己创建的行」+ psql 对照 |
| 5 | 数据范围=全部（超管）：不受过滤 | **PASS** | 测试「过滤器_全部范围_不装配」（SetOrgEntityFilter 返回 false 不挂过滤，全量可见）；超管入口 PermissionMiddleware 短路（AccountType=999 → false） |
| 6 | 授权变更后缓存即时失效（改角色 DataScope → 下次查询立即生效） | **PASS** | 测试「服务_缓存命中与变更即失效」（UpdateRoleInput.DataScope 非 null 且变更 → 删三键 → 再查取新值）+「服务_机构改挂载_受影响用户缓存即时失效」（Pid 迁移 → 新旧锚点子树受影响用户缓存全删） |
| 7 | 存量数据（无 OrgId/CreateOrgId）行为文档化 | **PASS** | 见「披露」节 5：机构范围下 NULL 行不可见（fail-closed）；仅本人范围下 CreateUserId 已设行可见、双 NULL 行不可见；AOP 仅回填新写入 |
| 8 | git commit + 验收报告附实测输出 | **PASS** | 同批单提交（代码 + 本报告 + 框架索引/使用指南/差距清单，约定 #13） |

## 交付范围（任务书 files 清单 + 契约外必要连带改动）

任务书 `files:` 清单（核心交付物）：

- **`src/Core/Entities/EntityBaseData.cs`（新增）**：`IOrgIdFilter { long? CreateOrgId }` + `EntityBaseData : EntityBase, IOrgIdFilter`（`[OwnerOrg] [SugarColumn("CreateOrgId", IsOnlyIgnoreUpdate=true, IsNullable=true)] long? CreateOrgId`）+ `EntityBaseUpdateData : EntityBaseData`（`[OwnerOrg] long? UpdateOrgId`）+ `EntityTenantBaseData : EntityTenant, IOrgIdFilter`（组合基类预留，暂无使用方）；
- **`src/Core/Attributes/OwnerOrgAttribute.cs` / `OwnerUserAttribute.cs`（新增）**：`[AttributeTargets.Property, Inherited=true]` 空标记特性（非 ModelBinding，仅过滤字段标记，任务书契约 1 明示）；
- **`src/Core/Enums/DataScopeEnum.cs`（新增）**：All=1 / OrgAndChild=2 / Org=3 / Self=4 / Define=5；多角色收敛=枚举值最小即最大范围；
- **`src/Core/SqlSugar/SqlSugarFilter.cs`（新增）**：`Init(params Assembly[])` 反射扫描 [SugarTable] 具体 EntityBaseData/EntityTenantBaseData 子类（排除 [IgnoreTable]/[LogTable]，lock 门控后写优先）；`SetOrgEntityFilter(db, userId, maxScope, orgIds)` 按范围分流装配（All 不装配 / Self 用 [OwnerUser]==userId / 机构类用 [OwnerOrg] Contains(orgIds)，多字段 OR，空集 fail-closed）；`ClearOrgFilter(db)` 按注册类型精确清除；`DeleteUserOrgCacheAsync(cache, userId)` 删三键；
- **`src/Core/SqlSugar/OrgQueryExtension.cs`（新增）**：契约 4 扩展 `IgnoreOrgData<T>()`——查询级 `ClearFilter(typeof(T))` 放行（对齐 TenantQueryExtension.IgnoreTenant 先例，查询实例级、无共享状态）；
- **`src/Core/Services/Org/SysOrgService.cs`（扩展既有类，不新建服务）**：`GetUserOrgScopeAsync(userId)`（多角色最大范围收敛 + 机构集计算：全部→空集 / 本部门及以下→子树 BFS `GetChildIdListWithSelfById` / 本部门→单点 / 仅本人→空集）；缓存 `KeyUserOrg:{userId}` + `KeyRoleMaxDataScope:{userId}`（7 天，空集负缓存）；`UpdateAsync` 补改挂载（Pid 变更）失效钩子——受影响集=OrgId 落在旧父机构子树 ∪ 新父机构子树的用户，变更即删三键（宁多勿漏）；
- **`src/Core/Services/Role/SysRoleService.cs`（改）**：`UpdateAsync` 补 DataScope 变更失效钩子（入参非 null 且变更 → 该角色全部用户删三键）+ `DeleteAsync` 既有角色删除失效路径同步扩至三键（原仅删权限键）；
- **`src/Core/Services/User/SysUserService.cs`（改）**：`GrantRoleAsync` / `DeleteAsync` 失效钩子由单键（KeyUserApiPerm）扩至三键（GrantRoleAsync 原本只删权限缓存，数据范围缓存漏删属契约 5 缺口，同 022 披露⑤口径补齐）；
- **`src/Test/DataScopeProbeEntities.cs` / `src/Test/DataScopeTest.cs`（新增）**：探针实体（EntityBaseData / EntityBaseUpdateData 两档）+ 11 用例（过滤器 5：机构范围 / 空集 / 仅本人 / 全部 / IgnoreOrgData；服务 5：多角色收敛 / 无角色 / 本部门 / 缓存命中与变更即失效 / 机构改挂载失效；AOP 1：写入侧 CreateOrgId+UpdateOrgId 自动填）。

契约连带改动（`files:` 之外，均因契约行为模型必要，逐条披露见「披露」节）：

- `src/Core/CacheConst.cs`：+`KeyUserOrg` / `KeyRoleMaxDataScope` 键常量（KeyUserApiPerm 为 022 既有）；
- `src/Core/Entities/SysRole.cs`：增量加列 `DataScope`（long，默认 4 仅本人；CodeFirst 增量列，存量行取默认不破坏）；
- `src/Core/Services/Role/Dto/UpdateRoleInput.cs`：record 扩位 `long? DataScope`（可空=不改，既有 FromBody 用法零影响）；`AddRoleInput.cs` 同扩位（新建角色可指定数据范围，缺省实体默认 4）；`RoleOutput.cs` 扩位 `long DataScope`（供前端标识角色数据可见性）；
- `src/Core/SqlSugarSetup.cs`：AOP 写入侧——InsertByObject 补 `IOrgIdFilter.CreateOrgId`（null 时按当前用户机构回填）、UpdateByObject 补 `EntityBaseUpdateData.UpdateOrgId`；新增 `CurrentUserOrgId(IHttpContextAccessor)` 助手（CurrentUserId → 每请求 Items 记忆化 → 主连接作用域查 sys_user.OrgId，JWT 不加 OrgId claim 系既有设计）；
- `src/Web.Core/Auth/PermissionMiddleware.cs`：请求进入时装配机构过滤（解析 UserId claim → scope 作用域取 `GetUserOrgScopeAsync` → `SetOrgEntityFilter`），finally 清除（与租户过滤同装配点/同生命周期；未认证 / AccountType=999 短路）；
- `src/Web.Entry/Program.cs`：InitTables 后 `SqlSugarFilter.Init(typeof(SysUser).Assembly, typeof(Class1).Assembly)`（Core + Business 全扫描）；
- **BOM 修复（2 文件）**：`EntityBase.cs` / `SysRole.cs` 在 task_008 增量改列时丢失 BOM（仓库约定 #2 BOM+CRLF），本次 diff 触及时按文件 HEAD 风格恢复 BOM（CRLF 保持），diff 与 HEAD 干净。

## build / 回归

- `dotnet build src/AdminCore.slnx`（任务书验收命令口径，仓库实际 slnx 路径，见披露 8）：**Build succeeded. 0 Warning(s) 0 Error(s)**（~8s，全 8 工程）。
- `dotnet test src/Test/AdminCore.Test.csproj --no-build`：**Passed! 23/23**（Failed 0 / Skipped 0，11s）——连续 3 轮复跑全绿；新增 11 用例全部连真实 PostgreSQL（`huluyeye_admin` 开发库，TCP 探测 2s 不可达时显式 Fail 不冒充通过），用例内 id 精确清理（sys_user_role/sys_org/sys_role/sys_user + 探针表按 Name 前缀）。

## 实测证据（psql + SqlSugar 生成 SQL）

**行级过滤前后对照**（探针表 `test_data_scope_probe`，种子 4 行：CreateOrgId=111/222/999/NULL；用毕即删）：

```
=== 无过滤(全部范围/超管视角)
    Name     | CreateOrgId | CreateUserId
 t13_ev_orgA |         111 |            7
 t13_ev_orgB |         222 |            8
 t13_ev_out  |         999 |            9
 t13_ev_null |            |           10
(4 rows)

=== 机构范围过滤(OrgAndChild, 可见集={111,222})
 t13_ev_orgA |         111 |            7
 t13_ev_orgB |         222 |            8
(2 rows)

=== 仅本人过滤(Self, userId=7)
 t13_ev_orgA |         111 |            7
(1 rows)

=== 空机构集(fail-closed)
(0 rows —— SqlSugar 对空集 Contains 生成 (1=2)，探针 ToSql 捕获：
 WHERE  (1=2) )
```

**SqlSugar 5.1.4.217 探针 ToSql 捕获**（真实过滤 SQL，非手写模拟）：

- 机构范围：`SELECT ... FROM "test_data_scope_probe" WHERE ("CreateOrgId" IN (111,222))`
- 仅本人：`SELECT ... FROM "test_data_scope_probe" WHERE ( "CreateUserId" = @CreateUserId0 )`
- 空集：`SELECT ... FROM "test_data_scope_probe" WHERE  (1=2)`

**ORM 拦截语义反编译核实**（ilspycmd 对 NuGet 实际 DLL，非源码仓库——源码仓与 5.1.4.217 不同步，无 QueryFilterProvider）：

1. `QueryBuilder.AppendTableFilter` 读 `exp` 强转 LambdaExpression 并取 `Parameters[0].Name` 作列前缀翻译 → **Type 重载注册的过滤器必须为带命名参数的 LambdaExpression**（裸 body → 强转 null → NRE；未命名参数 → 前缀映射失败）。已固化为 `SqlSugarFilter.BuildCondition` 的 WHY 注释 + 测试覆盖。
2. `Clear(Type[])` / 查询级 `ClearFilter(Type[])` 均按 `item.type` 匹配 → 精确清除具体实体注册项；接口级软删/租户过滤（`item.type`=接口）幸存（探针实测 Clear 前后清单）。
3. **`Clear<T>()` 无法清除 Type 重载注册项**（只匹配 `TableFilterItem<T>` 泛型项，Type 重载项为 `TableFilterItem<object>`）→ 清除必须走 `Clear(Type[])`（`SqlSugarFilter.ClearOrgFilter` 已按此实现）；查询级放行走 `ClearFilter(typeof(T))`（`IgnoreOrgData`，探针实测同语义）。

## 行为路径走查（请求侧装配链）

1. 请求 → PermissionMiddleware：未认证 → 不装配（401 门禁同前）；AccountType=999（超管）→ 短路不装配（全量可见，008 穿透语义延续）；
2. 已认证 777 账号 → 解析 JwtClaimNames.UserId → `IServiceScopeFactory` 作用域取 SysOrgService → `GetUserOrgScopeAsync(userId)`（缓存命中直返 / 未命中按角色收敛 + 机构集计算落缓存）→ `SqlSugarFilter.SetOrgEntityFilter(db, userId, maxScope, orgIds)` 挂主单例 Scope 的 QueryFilter；
3. 业务仓储查询（主库实体复用主 Scope，ConnectionClients 分作用域仅日志库）→ 命中已注册实体 → WHERE 注入机构/本人条件 → finally `ClearOrgFilter` 清除（租户/软删接口过滤不受影响，探针实测幸存）。

## 披露

1. **契约 3 方法名与签名**：任务书 `GetUserOrgIdList(userId)` → 实现为 `GetUserOrgScopeAsync(userId)` 返回 `(DataScopeEnum MaxScope, List<long> OrgIds)`——契约要求的机构集合是子集，但 ORM 装配还需收敛后的最大范围，单一入口一次取齐（否则中间件要再查一次角色缓存）；Async 后缀为项目约定 #3。行为语义与契约一致（多角色取枚举值最小=最大范围；含本人直属/子树/单点）。
2. **契约 3「非超管授权自定义范围时校验所选机构在自己集合内」未落地**：Define(5) 自定义范围依赖「角色-机构授权表」，本仓 RBAC 既有体系（008）无此表 → 收敛遇 Define 返回**空集 fail-closed**（查无行，宁缺毋滥）+ 本文档留痕；授权表落地后补校验与并集逻辑（差距清单 G1 备注）。
3. **契约 4「过滤器按 dbConfigId:userId 缓存」→ 改为按请求内存装配**：与租户过滤（008）同模型——每请求装配/finally 清除，不建持久过滤器缓存。理由：租户过滤既有先例即此模型（PermissionMiddleware 单装配点），机构过滤同点同生命周期扩展侵入最小；用户范围集本身已有 7 天持久缓存（KeyUserOrg/KeyRoleMaxDataScope），每请求成本=2 次缓存读（命中时）。代价=主单例 Scope 的 QueryFilter 存在并发装配窗口（基座期单请求作用域口径，与租户过滤同风险同披露口径，升级点=中间件）。
4. **契约 5 删 5 键 → 实删 3 键**：`KeyUserOrg:{userId}` / `KeyRoleMaxDataScope:{userId}` / `KeyUserApiPerm:{userId}`（022 既有键，角色 DataScope 变更同时影响按钮权限语义边界，一并失效）；`KeyUserRole`（本仓无角色列表缓存）与 `db:{dbConfigId}:orgList:{userId}`（披露 3 无持久过滤器缓存）两键不存在 → 不删。
5. **存量数据行为（验收项 7 文档化）**：存量行 `CreateOrgId=NULL`（AOP 仅回填新写入，不回溯存量）→ 机构范围（2/3/5）过滤下**不可见**（fail-closed，`NULL IN (...)`=不命中）；仅本人（4）过滤下 `CreateUserId` 已设行可见、`CreateUserId` 亦 NULL 的行不可见；全部（1）/超管不受影响。存量数据如需纳入机构范围，业务侧按数据归属补 `CreateOrgId`（UPDATE 批量回填，非框架职责）。
6. **仅本人范围语义=CreateUserId 而非登录人 OrgId**：契约 4 明示 Self 用 `[OwnerUser]` 生成 `CreateUserId==userId`（本人创建的行），与 OrgId 无关——「自己机构里别人建的行」在仅本人范围下不可见，系契约语义（baseadmin 同义）。
7. **`sys_role.DataScope` CodeFirst 增量列**：首次启动（测试 InitTables）自动加列，存量角色行取列默认 4（仅本人）——比改前「无数据范围概念」更保守，行为变化=存量角色下用户查询收紧至仅本人，属数据权限上线的预期语义（任务书契约 2 明示默认 4）。
8. **任务书验收命令路径勘误**：任务书写 `dotnet build src/huluyeye-admin.slnx`（023 更名前路径），仓库实际为 `src/AdminCore.slnx`（葫芦爷爷裁决全局更名 Huluyeye.→AdminCore. 所致）——按实际路径实跑。
9. **测试库复用开发库**：新增用例连 `huluyeye_admin` 开发库（与基线 12 用例同口径），探针表 `test_data_scope_probe` / `test_data_scope_update_probe` 由 InitTables 建表，用例内 id 精确清理（含 sys_user 种子——sys_user.RealName/Status 等 NOT NULL 列按实体默认值/显式赋值满足约束）；`EventBusTest.op_log 落库延迟 <1s` 为基线既有时序敏感用例（实测偶发 1049ms），复跑转绿，与本任务无代码交集。
10. **写入侧 CurrentUserOrgId 记忆化**：每请求首查 `sys_user.OrgId`（主连接作用域）后入 `HttpContext.Items` 记忆化，同请求多次 AOP 写入只查一次；无 HttpContext（后台/测试直接仓储写）→ 返回 null → CreateOrgId/UpdateOrgId 保持 null（=存量数据同态，fail-closed 口径）。JWT 不加 OrgId claim 系既有设计（机构变更不重签 token，行级过滤走查询期解析）。

## 交付最小清单

- **限域**：新增 8 文件（Core：EntityBaseData.cs、OwnerOrg/OwnerUser 特性×2、DataScopeEnum、SqlSugarFilter、OrgQueryExtension；Test：探针实体 + DataScopeTest）+ CacheConst 2 键；改 8 文件（SysRole.DataScope、UpdateRoleInput、SqlSugarSetup AOP、PermissionMiddleware、Program.cs、SysOrg/SysRole/SysUserService 失效钩子、EntityBase/SysRole BOM 恢复）；删 0 文件。
- **影响面**：新增能力零存量破坏——存量实体不继承 EntityBaseData → 不注册过滤 → 行为不变；`sys_role` +1 列（默认 4）；主库仓储查询在已登录 777 账号请求内新增 WHERE 注入（仅已注册实体）；超管/未认证路径零变化；测试 23/23 回归干净；DB 结构变更=1 增量列 + 2 测试表（探针，可删）。
- **回滚**：单提交自包含，`git revert <提交号>` 即全量恢复（列/表保留但无代码消费，无害；如需彻底 `drop column`/`drop table`）。
- **最小验证**：`cd src && dotnet build AdminCore.slnx && dotnet test Test/AdminCore.Test.csproj --no-build` → 0W/0E + 23/23。
