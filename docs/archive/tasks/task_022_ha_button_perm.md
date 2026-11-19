# task_id: task_022_ha_button_perm
# subject: task_022_ha_button_perm
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: task_008_ha_rbac
# files: src/Core/Attributes/, src/Business/Services/Menu/

# 任务：按钮权限白名单 + 接口黑名单校验

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §2.10（按钮权限白名单 + 接口黑名单校验——★ 接口鉴权核心）。
**代码纪律**：中文注释；禁止修改 files 外文件。

## 背景

RBAC 已实现用户-角色-菜单授权链（008），菜单表含按钮级条目（MenuType=按钮），但缺少接口层面的按钮权限校验——当前任何持有 JWT 的用户可以调用任意 API。本任务补齐接口鉴权闭环。

## 契约（按此实现）

1. **特性**（Core/Attributes/，单文件）：
   - `[MenuPerm("sys:user:page")]` 标注 Controller Action——值为菜单表的 Permission 字段值
   - ASP.NET Core ActionFilter 或中间件：请求到达时检查当前用户角色关联的菜单 Permission 集合是否包含标注值
   - 未标注 `[MenuPerm]` 的接口默认放行（向后兼容）
   - 无效 Token → 401（已有 JWT 中间件处理）；Token 有效但权限不足 → 403
2. **权限集合缓存**：用户角色→菜单 Permission 集合缓存 Redis（KeyUserApiPerm:{userId}），角色/菜单变更时删除缓存
3. **示例标注**：在 SysUserController 的 Page 方法加 `[MenuPerm("sys:user:page")]` 作为示范

## 交付物

- `src/Core/Attributes/MenuPermAttribute.cs`（特性+Filter 同批）
- `src/Business/Services/Menu/MenuPermService.cs`（权限集合查询+缓存）

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 实测（curl 附输出）：有权限的用户调用标注接口 → 200；无权限的用户调用 → 403；未标注接口 → 正常访问
- 缓存失效断言：修改角色菜单后再次调用 → 新权限立即生效
