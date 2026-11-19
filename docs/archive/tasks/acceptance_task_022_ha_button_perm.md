# 验收报告 task_022_ha_button_perm（按钮权限白名单：[MenuPerm] 标注特性 + IMenuPermService 权限集缓存 + 角色/菜单变更精准失效）

- 分支：`task_task_022_ha_button_perm`（worktree `/data/worktrees/admin/task_022_ha_button_perm`，HEAD 前驱 `db28a77` = main）
- 代码提交：**8735544**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-28
- 评估文件：`docs/tasks/task_022_ha_button_perm.md.assess.md`（4 点/总 6 ≤ 阈值 6，结论直行）
- 实跑口径：build/test 开发实跑（见「build / 回归」节）；curl 行为三态与缓存失效断言按本任务指令交由系统验证脚本执行（开发不写验证脚本/攻击数据，专注代码交付）

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | **PASS** | 提交后干净树实跑 **0 Warning(s) 0 Error(s)**（12.9s，全 7 工程），见「build / 回归」节；系统自动构建文件预期路径 `/data/tasks/build/task_022_ha_button_perm.txt`（commit 后生成，未生成时以本行实跑为准，披露④） |
| 2 | 有权限用户调标注接口 → 200；无权限 → 403；未标注 → 正常访问 | **代码路径就绪，交由系统验证脚本实跑** | 判定链静态走查见「行为路径走查」节（MenuPermFilter 四步判定 + PermissionMiddleware 门禁）；标注示例 `[MenuPerm("sysUser/page")]` 已贴 SysUserController.Page |
| 3 | 修改角色菜单后再次调用 → 新权限立即生效 | **代码路径就绪，交由系统验证脚本实跑** | 失效链静态走查：GrantMenuAsync/GrantRoleAsync/DeleteAsync(角色/用户)/UpdateAsync+DeleteAsync(菜单) 五处钩子全部换接 IMenuPermService，变更即删 `KeyUserApiPerm:{userId}`，不靠 TTL 兜底；见披露⑤ |

## 交付范围（按任务书 files 清单 + 契约外必要连带改动）

任务书 `files:` 清单（核心交付物）：

- **`src/Core/Attributes/MenuPermAttribute.cs`（新增，契约 1）**：`IMenuPermService` 契约（GetPermissionsAsync + 三失效方法）+ `MenuPermAttribute` 标注特性 + `MenuPermFilter`（IAsyncActionFilter，特性与 Filter 同文件）——特性与 Filter 同批交付口径满足；
- **`src/Business/Services/Menu/MenuPermService.cs`（新增，契约 2）**：IMenuPermService 实现——用户→SysUserRole→SysRoleMenu→SysMenu(Type=3 按钮) 三段链路解析 Permission 集；缓存键 `KeyUserApiPerm:{userId}`、TTL 7 天、空集负缓存；三失效方法（用户单删 / 角色→用户扩散 / 菜单→角色→用户两跳扩散）。

契约连带改动（`files:` 之外，均因契约行为模型从 008「路由白名单默认拦截」改为本任务「特性标注默认放行」而必要，逐条披露②）：

- `src/Core/CacheConst.cs`：+`KeyUserApiPerm` 键常量（前缀拼 userId，与 008 旧键同口径注释 WHY 不含租户段）；
- `src/Web.Core/Auth/PermissionMiddleware.cs`：移除 008 路由白名单 403（超管放行/身份端点放行/路由比对三段全删）；保留 /api 未认证 401 全局门禁 + [AllowAnonymous] 显式放行 + 每请求租户行级过滤（401 门禁是默认放行模型下未标注接口不匿名可达的前提，WHY 见类注释）；
- `src/Web.Core/Auth/IUserApiPermissionProvider.cs`、`src/Business/Services/Auth/UserApiPermissionProvider.cs`：**删除**（008 路由白名单 Provider，中间件不再消费后成死代码）；
- `src/Business/Services/Role/SysRoleService.cs`、`User/SysUserService.cs`、`Menu/SysMenuService.cs`：失效钩子 `IUserApiPermissionProvider` → `IMenuPermService` 换接；SysMenuService.UpdateAsync **补** `InvalidateMenuAsync`（原实现更新菜单后不删缓存——契约「菜单变更删缓存」缺口，披露⑤）；DeleteAsync 失效移到中间表物理删之前（InvalidateMenuAsync 需角色引用行尚存）；
- `src/Web.Entry/Controllers/SysUserController.cs`：Page 贴 `[MenuPerm("sysUser/page")]` 标注示例（披露①）+ 类注释更新；
- `src/Web.Entry/Program.cs`：DI 换注册（-IUserApiPermissionProvider/+IMenuPermService/+MenuPermFilter transient）+ `Configure<MvcOptions>` 加 `Filters.AddService<MenuPermFilter>()`（与 OpLogFilter 同注册口径）。

## build / 回归

- `dotnet build src/huluyeye-admin.slnx`（任务书验收命令，commit 后干净树实跑）：**Build succeeded. 0 Warning(s) 0 Error(s)**（12.91s，全 7 工程）。
- `dotnet test src/Test/Huluyeye.Test.csproj`：**Passed! 7/7**（Failed 0 / Skipped 0，2s）——旧 Provider 删除 + 三服务构造函数签名变更的回归佐证（测试工程无旧类型引用）。

## 行为路径走查（验收项 2/3 的代码级证据；实跑以系统验证脚本为准）

**验收项 2 判定链**（请求 /api/sysUser/page，MenuPermFilter 为全局 ActionFilter，MvcOptions 注册，每请求作用域解析）：

1. 端点元数据无 `[MenuPerm]` → `next()` 放行（未标注默认放行，覆盖全部未标注端点与非 API 页面端点）；
2. 标注端点 + 未认证 → 401（防御性兜底；正常路径 PermissionMiddleware 先行拦截）；
3. 标注端点 + AccountType=999（超管）→ 放行（008 超管穿透语义保留）；
4. 标注端点 + 有效 Token 无 UserId → 401（签发缺陷口径与中间件一致）；
5. 标注端点 + 权限集含 `sysUser/page`（OrdinalIgnoreCase）→ 200；不含 → **403** `{"code":1,"msg":"无权限:sysUser/page"}`（匿名对象经全局 JSON 选项 camelCase 序列化，与 ApiResult.Fail 同构）。

权限集来源：`KeyUserApiPerm:{userId}` 缓存命中直接返回；未命中走 SysUserRole→SysRoleMenu→SysMenu(Type=3,Permission≠null) 三段查询落缓存（含空集负缓存，TTL 7 天兜底）。种子数据：按钮行 Id=10010 Permission=`sysUser/page`，授权该按钮的角色下用户 → 200；未授该按钮的 777 账号 → 403；未标注端点（如 sysUser 其余端点）对任意已登录用户正常访问。

**验收项 3 失效链**（五处写路径钩子，全部「变更即删、不靠 TTL」）：

| 变更动作 | 服务方法 | 失效调用 | 覆盖范围 |
|---|---|---|---|
| 用户分配角色 | SysUserService.GrantRoleAsync | InvalidateUserAsync(userId) | 该用户 |
| 用户删除 | SysUserService.DeleteAsync | InvalidateUserAsync(id) | 该用户 |
| 角色授权菜单 | SysRoleService.GrantMenuAsync | InvalidateRoleAsync(roleId) | 角色下全部用户 |
| 角色删除 | SysRoleService.DeleteAsync | 软删前固化 userId 快照 → 逐个 InvalidateUserAsync | 角色下全部用户 |
| 菜单更新/删除 | SysMenuService.UpdateAsync/**DeleteAsync** | InvalidateMenuAsync(menuId)（菜单→引用角色→角色下用户两跳扩散；删除路径失效先于中间表物理删） | 持有该菜单的全部用户 |

## 披露

1. **标注值取 `sysUser/page` 而非任务书示例 `sys:user:page`**：任务书契约正文「值为菜单表的 Permission 字段值」为规范条款，示例 `sys:user:page` 与菜单种子数据（route 格式：`sysUser/page`、Id=10010）不一致——按示例贴注将永久 403（菜单表无冒号值行）。取种子真实值，「有权限用户 → 200」验收口径成立。
2. **`files:` 之外改动逐条必要**：契约行为模型由 008「路由白名单默认拦截」改为本任务「特性标注默认放行」，中间件路由 403 段与旧 Provider 必须退役（留置则旧 403 与新 403 双裁决冲突，未标注接口会被旧白名单误拦——与「未标注默认放行」直接矛盾）；三域服务钩子与 DI 注册随 Provider 删除连带换接；CacheConst 新增为契约键常量化（项目既有键管理约定）。
3. **超管穿透保留**：AccountType=999 在 MenuPermFilter 放行（008 语义延续，防超管无按钮授权时自锁）；任务书未禁止，测试「无权限用户」应为 777 账号。
4. **系统自动构建文件未生成**：commit `8735544` 后轮询 >2 分钟 `/data/tasks/build/task_022_ha_button_perm.txt` 未出现（目录为空，012 报告披露⑥同先例）——以开发实跑任务书验收命令 0W/0E（见「build / 回归」节）为准；若文件后续生成，其结果应一致。
5. **SysMenuService.UpdateAsync 原无缓存失效**：008 实现更新菜单（含按钮 Permission 变更）后不删权限缓存，属契约「角色/菜单变更删缓存」的既有缺口；本次补 `InvalidateMenuAsync(menu.Id)`（菜单变更会使缓存中的权限集过期）。
6. **手册 §2.10 未更新**：`docs/框架开发参考手册.md` §2.10 仍描述 008 路由白名单模型（默认拦截、显式放行），与本任务后实际行为不符——手册维护不属本任务 files 清单，留待后续文档任务，此处留痕。
7. **评估文件位置**：`/data/tasks/running` 对开发会话只读（dispatcher 持有）→ 评估文件落 worktree `docs/tasks/task_022_ha_button_perm.md.assess.md`（*.assess.md 已被 .gitignore 排除，不入库），同 012 披露⑤。

## 交付最小清单

- **限域**：新增 2 文件（Core/Attributes/MenuPermAttribute.cs、Business/Services/Menu/MenuPermService.cs）+ CacheConst 1 常量；改 7 文件（PermissionMiddleware 路由段移除、三域服务钩子换接、SysUserController 标注+注释、Program.cs DI+Filter 注册、MenuService.Update 补失效）；删 2 文件（008 旧 Provider 契约+实现）。
- **影响面**：PermissionMiddleware 行为收窄（仅 /api 401 门禁 + 租户过滤；原路由 403 裁决移入 MenuPermFilter 且仅对标注端点生效）——未标注 /api 端点对已登录用户由 008 的「默认 403」变为「放行」，系契约要求的模型切换；测试 7/7 回归干净；DB 无结构变更。
- **回滚**：`git revert 8735544`（单提交自包含；回滚即恢复 008 路由白名单行为，缓存键空间不同互不污染）。
- **最小验证**：build 0/0 + test 7/7 + 系统验证脚本三态 curl（200/403/未标注）+ 角色菜单变更即时生效断言；复跑命令 `dotnet build src/huluyeye-admin.slnx && dotnet test src/Test/Huluyeye.Test.csproj`。
