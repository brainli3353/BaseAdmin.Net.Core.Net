# task_id: task_008_ha_rbac
# subject: task_008_ha_rbac
# source: huluyeye
# created: 2026-08-26
# format: v3
# priority: P0
# dep_on: task_007_ha_otp
# files: src/Business/Entitys/, src/Business/Services/, src/Business/SeedData/, src/Web.Entry/Controllers/, src/Web.Core/Auth/, src/Core/SqlSugarSetup.cs, src/Core/Attributes/, src/Core/Entities/, src/Web.Entry/appsettings.json

# 任务：RBAC 权限体系 + 审计自动填充 + 增量建表/增量种子 + 多库通用选库 + 租户基座 + 操作日志（M2b）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第二节「ORM 初始化设计」（审计 AOP/增量建表/增量种子/多库）+ 第六节「RBAC」+ 第八节「代码目录与文件命名约定」+ 第九节「租户基座设计」——照此实现，禁止引入 Furion/Admin.NET 任何包。注：baseadmin 源码服务器无 clone，设计精华参考已提炼全部所需模式，无需读源码；任务书契约内已含关键类签名与代码骨架。第三方 NuGet 包**本任务不新增**（SqlSugar 多库/种子为框架内置能力）。
**代码纪律**：关键位置必须补中文注释（类/方法头一句话职责、复杂逻辑 WHY）。
**投递前提**：007 完成且验收通过后投递（ApiResult 来自 task_006，M1 终审已保证；008 与 007 在 Business/Entitys、Web.Core/Auth 有冲突域重叠，靠 dep_on 串行解决——禁止并行派发）。

## 背景

M1 + task_007 已完成（登录两阶段、Redis、统一响应）。本任务落地**权限体系（RBAC）**与剩余框架基建：接口级权限校验（默认拦截、显式放行）、审计字段自动填充 AOP、增量种子机制（对齐 [IncreSeed] 约定）、多库切换（主库+日志库）、操作日志落库。数据权限（机构范围 ORM 拦截）为迭代项，本任务不做（任务书末尾注明）。

## 契约（按此实现）

### 1. RBAC 实体（src/Business/Entitys/，均 [SugarTable] + [IncreTable]）

- **SysRole**：Name（名称）、Code（编码，唯一）、Remark
- **SysMenu**：Pid（父Id，自关联构树）、Name（路由名，如 dashboard）、Title（菜单名）、Type（int：1目录 2菜单 3按钮）、Permission（按钮权限标识=接口路由，如 `sysUser/add`——与 API 路由一致，一处配置两端生效）、OrderNo、Icon
- **SysUserRole**：UserId、RoleId
- **SysRoleMenu**：RoleId、MenuId
- **SysUser 增量加列**：OrgId（long?，机构字段预留——数据权限迭代用）

### 2. 接口权限校验（src/Web.Core/Auth/PermissionMiddleware.cs）

- 对齐 baseadmin 核心安全模型：**默认拦截、显式放行**
- 路由取请求 Path 去 `/api` 前缀（如 `/api/sysUser/page` → `sysUser/page`）
- 超管（Claim AccountType==999）直接放行
- 普通用户：查按钮权限集合（菜单 Type=按钮 的 Permission，经 角色→SysRoleMenu→SysMenu 链路），缓存 `sys_user_api:{userId}`（Redis 7 天）；当前路由在集合内 → 放行，否则 403
- 缓存失效：角色授权变更（GrantRoleMenu）后主动 Remove `sys_user_api:{userId}`（对该角色下所有用户）——变更即失效，不靠过期兜底
- 黑名单差集机制（未授权按钮全量清单）为 M2.5 迭代，本任务注释留位

### 3. 审计字段自动填充（修改 src/Core/SqlSugarSetup.cs 的 SetDbAop）

- 对齐设计精华参考 §2.1：`Aop.DataExecuting`——插入时雪花主键为空自动赋值、CreateTime 空赋值 Now、CreateUserId/CreateUserName 从 `IHttpContextAccessor` 的 JWT Claim 取（无登录态跳过）；更新时 UpdateTime=Now、IsDelete==true 补 DeleteTime、UpdateUserId/UpdateUserName 同源
- 软删过滤器：`QueryFilter.AddTableFilter<IDeletedFilter>(u => u.IsDelete == false)`（task_003 的 IDeletedFilter 已就位）

### 4. 增量种子机制（src/Core/Attributes/ + SqlSugarSetup 扩展）

- `IncreSeedAttribute`（空标记特性）：种子类贴此特性才执行（对齐 baseadmin [IncreSeed]）
- `ISqlSugarEntitySeedData<TEntity>` 接口：`IEnumerable<TEntity> HasData()`
- `SqlSugarSetup.InitSeedData`：扫描程序集实现接口的种子类 → 过滤贴 [IncreSeed] 的 → 按 `[SeedData(Order)]` 排序 → `StorageableByObject(...).ToStorage()` 按主键**有则更新、无则插入**（幂等）——重复启动不产生重复数据
- 与 [IncreTable] 成对使用（实体默认 [IncreTable]、种子默认 [IncreSeed]，硬性约定）

### 5. 增量建表 + 多库通用选库（修改 src/Core/SqlSugarSetup.cs + appsettings）

**5.1 增量建表语义（补 task_002 缺口）**：
- `InitTables` 不再全量扫描——按配置 `DbSettings.EnableIncreTable`（appsettings，默认 true）分流：
  - 增量模式：只处理贴 `[SugarTable] + [IncreTable]` 且**无 `[IgnoreTable]`** 的实体（`CodeFirst.InitTables(type)` 本身幂等：只建缺失表/补缺失列，不删表）
  - 全量模式（首次部署开关）：处理全部 `[SugarTable]` 实体
- `[IncreTable]`/`[IgnoreTable]` 特性定义（Core/Attributes/，空标记）

**5.2 多库通用选库（对齐 baseadmin 仓储按特性选库）**：
- `ConnectionConfigs` 配置模型（数组）：主库 `main`（现有连接串）+ 可选日志库 `log`（连接串可注释禁用）+ 预留任意 ConfigId
- 特性定义：`[LogTable]`（日志实体→日志库）、`[SysTable]`（系统表→主库）、`[Tenant]`（未来租户库，基座先定义特性与机制，租户库懒加载逻辑后补）
- `SqlSugarRepository<T>` 构造函数按实体特性选库：`[Tenant]`→指定 ConfigId、`[LogTable]`→日志库、`[SysTable]`→主库、无特性→主库——业务代码零感知（对齐 baseadmin SqlSugarRepository 模式）
- 日志库连接未配置 → 回落主库（对齐 baseadmin `LogDb` 静态取库）
- **约定（写死，后补多租户不重构的前提）**：`SqlSugarConst.MainConfigId`、`LogConfigId`、`DefaultTenantId` 常量；**ConfigId 即租户 Id**（未来租户库 ConfigId=租户 Id）；种子 Id 空间按库标识递增（对齐 baseadmin `SqlSugarExtension`）

### 5.3 租户基座（只做基座，不做租户业务模块——2026-08-26 用户拍板"基座先行"）

**设计目标**：业务实体继承 `EntityTenant` 即自动获得租户隔离（查询过滤 + 写入自动填 TenantId + 选库预留），存量数据天然带默认租户值，**未来完整多租户（租户 CRUD/独立库初始化/套餐）后补时零重构**。

- **`src/Core/Entities/EntityTenant.cs`**：继承 `EntityBase` + 实现 `ITenantIdFilter`（接口含 `long? TenantId`），字段 `[SugarColumn(ColumnDescription = "租户Id", IsOnlyIgnoreUpdate = true)] long? TenantId`——**业务实体一律继承 EntityTenant**（本任务起，含本任务新建的 SysRole/SysMenu 等）
- **查询过滤**：`SetDbAop` 在软删过滤后加 `AddTableFilter<ITenantIdFilter>(u => u.TenantId == 当前租户)`（从 JWT Claim `TenantId` 取；无 Claim 或超管跳过——对齐 baseadmin 超管豁免）
- **写入自动填**：`Aop.DataExecuting` 插入时 TenantId 为空 → 填 Claim 值，缺省 `SqlSugarConst.DefaultTenantId`
- **扩展**：`IgnoreTenant()`/`ClearFilter<ITenantIdFilter>()` 跨租户查询放行（对齐 baseadmin）
- **不做（后补清单，注释留位）**：SysTenant 表与 CRUD、InitNewTenant（自动造租管机构/角色/账号）、租户独立库初始化、租户套餐/有效期、前端租户页——未来需要时走新任务书，基座无需改动

### 6. 操作日志落库（src/Web.Core/Auth/OpLogFilter.cs，IAsyncActionFilter）

- 记录：ControllerName/ActionName/HttpMethod/RemoteIp/Elapsed 毫秒/账号（Claims）/是否成功（异常状态码）→ **SysLogOp**（继承 EntityBase，[SugarTable("sys_log_op")] + [IncreTable] + [LogTable] 特性）
- 落库走事件解耦的轻量替代：直接 `Task.Run` 异步写（本任务不做事件总线，注释留位 M4）
- 请求体/返回体大字段本任务不落（省带宽，对齐 baseadmin 列表豁免设计）

### 7. 管理 API（src/Business/Services/ 按域分目录 + src/Web.Entry/Controllers/，目录约定见 baseadmin设计精华参考.md 第八节）

- **用户管理域** `Services/User/SysUserService.cs` + `Controllers/SysUserController.cs` 路由 `/api/sysUser/*`：Page（分页+账号模糊）、Add（密码 PBKDF2 哈希 + 默认角色）、Update（禁止改超管账号，校验唯一）、Delete（软删，禁删超管/自己）、GrantRole（分配角色）——DTO 就近 `Services/User/Dto/`（AddUserInput/PageUserInput/UserOutput）
- **角色管理域** `Services/Role/SysRoleService.cs` + `Controllers/SysRoleController.cs` 路由 `/api/sysRole/*`：Page/Add/Update/Delete（级联清 SysUserRole/SysRoleMenu）、GrantMenu（角色授权菜单，全量替换"删旧插新"）——DTO 就近 `Services/Role/Dto/`
- **菜单管理域** `Services/Menu/SysMenuService.cs` + `Controllers/SysMenuController.cs` 路由 `/api/sysMenu/*`：Tree（树形）、Add/Update/Delete（有子节点禁删）——DTO 就近 `Services/Menu/Dto/`
- 以上接口全部落在权限校验之下（默认拦截——未授按钮权限即 403）

### 8. 种子数据（src/Business/SeedData/）

- **SysMenuSeedData**：基础菜单（Id 分段 10000 起）——工作台（dashboard 菜单）、系统管理目录、用户管理/角色管理/菜单管理三菜单 + 按钮权限（`sysUser/page|add|update|delete|grantRole`、`sysRole/page|add|update|delete|grantMenu`、`sysMenu/page|add|update|delete`）——Permission 与路由一致
- **SysRoleSeedData**：系统管理员（Code=sys_admin，SysFlag 内置标记字段加 Role 上：内置角色禁删）
- **SysRoleMenuSeedData**：系统管理员 = 全部菜单（种子间互相引用：`new SysRoleSeedData().HasData()` 取角色 Id——跨表外键一致性，对齐 baseadmin）
- **admin 用户关联系统管理员角色**（SysUserRoleSeedData）

## 交付物

- `/data/worktrees/admin/task_008_ha_rbac/src/Business/Services/Role/SysRoleService.cs`（User/Menu 域服务 + 各域 Dto/ 同批；SysRole/SysMenu/SysUserRole/SysRoleMenu 实体同目录同批——**实体继承 EntityTenant**）
- `/data/worktrees/admin/task_008_ha_rbac/src/Business/SeedData/SysMenuSeedData.cs`（SysRole/SysRoleMenu/SysUserRole 种子同批）
- `/data/worktrees/admin/task_008_ha_rbac/src/Core/Entities/EntityTenant.cs`（含 ITenantIdFilter 接口同批）

（附带修改：IncreSeed/IncreTable/IgnoreTable/LogTable/SysTable/Tenant 特性入 Core、SqlSugarSetup 审计 AOP+增量建表+增量种子+通用选库+租户过滤、appsettings 加日志库配置与增量开关、SysUser 加 OrgId、PermissionMiddleware/OpLogFilter——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_008_ha_rbac && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 权限实测（验收报告附 curl 输出）：
  - admin（超管）登录 → 所有接口放行
  - 新建角色「只授用户管理菜单」+ 新建用户挂该角色 → 访问 `/api/sysUser/page` 放行、访问 `/api/sysRole/page` 403
  - 角色授权变更后旧缓存失效：新授权立即生效（不等 7 天）
- 审计实测：`docker exec postgres psql -U huluyeye -d huluyeye_admin -c "select \"CreateTime\",\"CreateUserId\" from sys_user order by id desc limit 1;"` 非空（登录/新建用户后）
- **增量建表实测**：新建一个贴 `[IncreTable]` 的测试实体 → 重启应用 → 表自动创建；`[IgnoreTable]` 实体不建表；`EnableIncreTable=false` 时全量建表
- **租户基座实测**：带 TenantId Claim 的请求查询自动过滤（`sys_menu` 按当前租户返回）；超管不受过滤；跨租户查询 `IgnoreTenant()` 放行；插入记录 `TenantId` 自动填充（psql 验证非空）
- 种子幂等：重启应用两次，`sys_menu` 行数不变（psql 前后对比）
- 操作日志：调用任一 API 后 `sys_log_op` 表出现对应记录（含账号/耗时/IP）
- 多库：日志库连接配置开启时 `sys_log_op` 建在日志库；注释掉日志库配置 → 回落主库正常
- git commit（提交号写入验收报告），验收报告 `acceptance_task_008_ha_rbac.md` 附以上实测输出

## 迭代留位（本任务不做，注释标注）

- 数据权限（机构范围 ORM 统一拦截：EntityBaseData + [OwnerOrg] + 过滤器）——M2.5
- 接口黑名单差集（全量按钮清单 - 已授权）——M2.5
- 操作日志异步事件化（事件总线）——M4
- **多租户业务模块**（SysTenant CRUD / InitNewTenant 自动造租管机构角色账号 / 租户独立库初始化 / 套餐有效期 / 前端租户页）——后补，基座（5.3）已就位，后补零重构
