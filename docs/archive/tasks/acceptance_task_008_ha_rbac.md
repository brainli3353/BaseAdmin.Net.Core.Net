# 验收报告 task_008_ha_rbac（M2b RBAC 权限体系 + 审计 AOP + 增量建表/种子 + 多库选库 + 租户基座 + 操作日志）

- 分支：`task_task_008_ha_rbac`（切自 main 后 fast-forward 合入 task_007 最终态 `50f62cc` 为开发基线，无历史改写）
- 代码提交：**d121d36**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-26
- 实跑环境：dev PG `172.19.0.10` / dev Redis `172.19.0.2`（等价环境，见披露①）

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | **PASS** | 实跑 0W/0E（14.6s），见「build/回归」节 |
| 2 | 权限实测：admin 超管全放行 | **PASS** | 阶段 B（B4/B5 sysRole/sysUser page=200） |
| 3 | 权限实测：仅授用户菜单的角色 → sysUser 放行 / sysRole 403 | **PASS** | 阶段 C（C6=200 / C7=403） |
| 4 | 权限实测：授权变更后旧缓存立即失效 | **PASS** | 阶段 D（D1 403 → D2 授权 → D3 立即 200） |
| 5 | 审计实测：sys_user 新建行 CreateTime/CreateUserId 非空 | **PASS** | 阶段 E（cuid=841915844415557） |
| 6 | 增量建表实测：[IncreTable] 建 / [IgnoreTable] 不建 / false 全量 | **PASS** | 阶段 J（J_incretable.txt ALL-PASS 6 断言） |
| 7 | 租户基座实测：按租户过滤 + 超管豁免 + 插入自动填 TenantId | **PASS** | 阶段 F（F3 探针不可见 / F4 超管可见 / F8 tenantid=1300000000001） |
| 8 | 种子幂等：重启两次 sys_menu 行数不变 | **PASS** | 阶段 H（19/3/47/3/3/3 重启前后一致） |
| 9 | 操作日志：调 API 后 sys_log_op 有记录（账号/耗时/IP） | **PASS** | 阶段 G（rbac_t1 三行 elapsedms/remoteip 非空） |
| 10 | 多库：log 配置开启时 sys_log_op 建日志库；注释后回落主库 | **PASS** | 阶段 I（log 库 3 行 + 单库回归 79→82） |
| 11 | git commit + 验收报告附实测输出 | **PASS** | d121d36 + 本报告 |

**全部 11 项验收口径命中。** 阶段 I（多库日志库 AOP）为本任务最难的调试项，根因与修复见专节。

## 交付范围（11 功能点，详见 .assess.md）

RBAC 四实体（SysRole/SysMenu/SysUserRole/SysRoleMenu）+ 特性族迁位 Core/Attributes + 多库通用选库（ConnectionConfigs + 按实体特性路由）+ 增量建表分流（EnableIncreTable）+ 增量种子 v2（ISqlSugarEntitySeedData + Storageable upsert）+ 审计 AOP 增强（TenantId 填充）+ 接口权限校验（PermissionMiddleware 默认拦截/显式放行/超管穿透/缓存 7 天/401-403）+ 租户基座（按请求行级过滤/超管豁免/IgnoreTenant/写入自动填）+ 操作日志（SysLogOp [LogTable] + OpLogFilter 异步落库）。

## build / 回归

- `dotnet build src/huluyeye-admin.slnx`：**Build succeeded. 0 Warning(s) 0 Error(s)**（14.6s，全 7 工程含 thirdparty/YitIdHelper、Otp.NET）。
- `dotnet test`：**6/6 通过**（0 失败）。
- 说明：首版代码提交存在 1 个 CS8604 可空警告（SqlSugarSetup 逐连接 scope 的 `cfg.ConfigId as string` 作字典键），已用 null-forgiving 消除（ConfigId 恒非空——`LoadConnectionConfigs` 以 `IsNullOrWhiteSpace` 过滤空值），amend 并入 d121d36，复跑 0/0。

## 实测证据（实跑，等价环境）

### 权限（A/B/C/D）

| 场景 | 期望 | 实测 |
|---|---|---|
| A1 无 token 访问 /api/sysUser/page | 401 | **HTTP 401** |
| A2 无 token 访问 /api/demo/success（[AllowAnonymous]） | 200 | **200** `{"code":0,...}` |
| B admin 两阶段登录（挑战→OTP→token） | token | **JWT 381 字符**，Claim 含 AccountType=999/TenantId |
| B4/B5 admin 访问 sysRole/page、sysUser/page（超管） | 200 | **200 / 200** |
| C1 建角色 rbac_u | id | **842084611203141** |
| C2 仅授 sysUser 按钮+父菜单 | 成功 | **成功** |
| C3 建用户 rbac_t1 挂该角色 | id | **842084612780101** |
| C4/C5 rbac_t1 两阶段登录 | token | **JWT 385 字符** |
| C6 rbac_t1 访问 /api/sysUser/page（有权） | 200 | **HTTP 200** |
| C7 rbac_t1 访问 /api/sysRole/page（无权） | 403 | **403** `{"code":1,"msg":"无权限:sysRole/page"}` |
| D1 rbac_t1 再访 sysRole/page（负缓存） | 403 | **403** |
| D2 admin 给角色追加 sysRole 按钮 | 成功 | **成功** |
| D3 rbac_t1 立即再访 sysRole/page（免等 7 天） | 200 | **HTTP 200** |

> 菜单树路由为 `GET /api/sysMenu/page`（非 /tree）：admin=200，rbac_t1 未授权=403。

### 审计 AOP（E）

psql 直查（huluyeye_admin）：
```
sys_user:    842084612780101 | rbac_t1 | tenantid=1300000000001 | createtime非空 | createuserid=841915844415557
sys_role:    842084611203141 | rbac_u  | tenantid=1300000000001 | createtime非空 | createuserid=841915844415557
sys_role_menu: 4 行(10010/10020 授权) | createtime非空 | createuserid=841915844415557
```
插入时雪花主键为空自动赋、CreateTime=Now、CreateUserId/Name 从 JWT Claim 取（无登录态跳过），全部命中。

### 租户基座（F）

psql 直插异租户探针菜单 `id=99999, tenantid=999`：
- rbac_t1（默认租户 1300000000001）查 /api/sysMenu/page：**探针行不可见**（行级租户过滤生效）；
- admin（超管豁免）查同接口：**探针行可见**（超管不受过滤）；
- 经 API 建菜单（服务层不显式赋值）：**tenantid=1300000000001**（AOP 从 Claim 自动填充）；
- 探针行已清理。

`IgnoreTenant<T>()` 扩展（TenantQueryExtension）用**查询级 `ClearFilter<ITenantIdFilter>()`**——只作用于当前查询实例、不动共享 Scope 的过滤状态（避免并发串扰），软删全局过滤器自动保留；跨租户管理/迁移场景可用。该项为库内扩展（非公开端点），实现经代码级验证，未单设实机端点复测。

### 增量建表（J）

探针实体三态 × 增量/全量两模式，**ALL-PASS**：
```
增量: [IncreTable] probe_incre 建表 / 无标记 probe_fullonly 不建表 / [IgnoreTable] probe_ignored 不建表
全量: [IncreTable] probe_incre 建表 / 无标记 probe_fullonly 建表 / [IgnoreTable] probe_ignored 仍不建表
```

### 种子幂等（H）

重启应用两次前后 psql 计数一致（幂等 upsert 不重复插入）：
```
sys_menu=19 | sys_role=3 | sys_role_menu=47 | sys_user=3 | sys_user_role=3 | sys_user_otp=3   （两次重启均不变）
```

### 操作日志（G）

psql 直查 sys_log_op（rbac_t1 触发请求后）：
```
rbac_t1 | GET | SysRole | Page | success=True | elapsedms非空 | remoteip=127.0.0.1
rbac_t1 | GET | Auth    | Me   | success=True | elapsedms非空 | remoteip=127.0.0.1
rbac_t1 | GET | SysUser | Page | success=True | elapsedms非空 | remoteip=127.0.0.1
```

### 登出黑名单回归（K）

- rbac_t1 logout → **200**；登出后旧 token 访问 /api/auth/me → **401**（黑名单生效）。

## 多库日志库 AOP（阶段 I，本任务核心调试）

**验收口径**：log 库连接配置开启时 `sys_log_op` 建在日志库、写入走日志库（审计字段填充）；注释 log 配置 → 回落主库正常。

**现象（修复前）**：多库模式（log 指向 `huluyeye_admin_test`）下，`/api/auth/*` 触发 SysLogOp 异步落库报 `23502: null value in column "createtime"`；log 库 0 行、主库计数不变 → 操作日志丢失。

**根因（逐探针定位）**：
1. 5.1.4.217 的**多连接构造函数 action 只作用于第一个连接**（探针：ctor action 仅被调用于 main，log 连接 AOP 全缺）；
2. 改为「启动期对每个 ConfigId 调 `GetConnection(configId)` 并挂 AOP」后**仍失败**——实机 hash 诊断证明：**多连接 `SqlSugarScope.GetConnection(configId)` 按线程/按次返回新的 `SqlSugarProvider` 实例**（启动期配置实例 hash ≠ 请求期实例 hash），启动期挂的 AOP/软删过滤**不跟随**到请求期实际执行插入的那个 provider 实例；
3. 单线程探针里 `GetConnection` 看似缓存同实例（hash 一致、AOP 全触发），掩盖了线程/按次漂移——这正是探针通过而应用失败的差异来源。

**修复**：放弃「多连接 scope 的 GetConnection」做请求期路由，改为**每个 ConfigId 另建独立单连接 `SqlSugarScope`**（单连接 scope 的构造 action 作用于自身、线程安全，主连接 Phase G 全链路已验证该模式），存入 `SqlSugarSetup.ConnectionClients`（ConfigId→scope），`GetConnectionById` 统一供给仓储/建表/种子。主连接路径（`SelectConnection` 命中 current）不变。

**验证**：
- 多库模式：admin 两阶段登录 + /me → log 库 `sys_log_op` **3 行**，雪花 id 非 0（842096597348421 等）、createtime 全非空、admin 行 createuserid=841915844415557、anonymous 行 NULL（正确）、success 布尔正确；**主库 sys_log_op 计数保持 79 不变**（未被 log 写入污染）。
- 单库回归（无 log 配置，生产默认）：`[LogTable]` 回落 main，触发后主库 79→82，审计填充正常，**修复对单库幂等、零影响**。

## 披露

1. **等价环境实跑**：dev-a 沙箱在 `172.19.x` 网络，不可达官方 `172.18.x` 网络（同 task_007 披露②口径）；全部实跑经环境变量注入 172.19.0.10(PG)/172.19.0.2(Redis)，非官方容器侧。
2. **日志库权限制约**：`huluyeye` 角色 `rolcreatedb=False`，无法新建 `huluyeye_admin_log`；log 库验证**复用已存在的 `huluyeye_admin_test`**（仅换库名，验证口径不变——[LogTable] 实体落非主库）。生产 appsettings 的 log 配置**默认仍注释**（由 DBA 预建 `huluyeye_admin_log` 后启用），单库模式回落主库已回归验证。
3. **build 自动验证文件未生成**：checkpoint 约定提交后系统自动跑 `dotnet build` 并写 `/data/tasks/build/task_008_ha_rbac.txt`；commit 后轮询 ~20 分钟该文件未出现（目录为空，亦无 task_007 的历史 build 文件）。为给权威验收证据并满足 build 0/0 口径，**dev 实跑任务书验收命令** `dotnet build src/huluyeye-admin.slnx`（0W/0E）+ `dotnet test` 6/6，输出见「build/回归」节。
4. **首版 1 警告**：CS8604 可空警告已用 null-forgiving 消除并 amend 入 d121d36（非逻辑变更，复跑 0/0 + 6/6）。
5. **迭代留位（未做，代码注释标注）**：数据权限（M2.5）、接口黑名单差集（M2.5）、操作日志异步事件化（M4）、多租户业务模块（SysTenant CRUD/InitNewTenant/独立库初始化/套餐/前端页）。
6. **基线**：worktree 正确挂载 huluyeye-admin 仓库（非 task_007 曾挂错的 quant 仓库）；基线 `50f62cc`=task_007 盲审整改后最终态、已验收，fast-forward 合入、无冲突、无历史改写。
7. **密钥脱敏**：dev appsettings 密钥为存量可接受（同 task_007 口径）；OTP 密钥/令牌不入本报告（脱敏）。

## 交付最小清单

- **限域**：RBAC 权限体系 + 审计 AOP + 增量建表/种子 + 多库选库 + 租户基座 + 操作日志，共 60 文件（新增 40 / 修改 14 / 迁移特性 2 处 / 含 .assess.md）；核心改动 `src/Core/SqlSugarSetup.cs`（多库+AOP+增量+选库单点）。
- **引用影响面**：`SqlSugarSetup`（多库 scope 供给方式变更，GetConnectionById 返回逐连接 scope）、`SqlSugarRepository`（SelectConnection 取连接）、`OpLogFilter`（[LogTable] 落库路由）、`Program.cs`（注册 PermissionMiddleware/OpLogFilter）、`appsettings.json`（ConnectionConfigs + log 注释 + DbSettings）。
- **回滚方式**：`git revert d121d36`（整体回退至 50f62cc 基线）。
- **最小验证**：build 0/0 + dotnet test 6/6 + 本阶段 A–K 实测套件（命令与输出留存于 dev-a `/tmp/dshprobe/task008_out/`，临时文件用后即删）。
