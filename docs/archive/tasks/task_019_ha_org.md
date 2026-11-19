# task_id: task_019_ha_org
# subject: task_019_ha_org
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: task_013_ha_datascope
# files: src/Business/Entitys/SysOrg.cs, src/Business/Services/Org/

# 任务：机构管理（SysOrg 实体 + 树形 CRUD + API）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §2.5（机构管理：结构树/代码参考/Furion 替换点）+ §2.7（数据权限——013 已实现 OrgId 关联）。
**代码纪律**：中文注释；实体 [IncreTable]；禁止修改 files 外文件。

## 背景

013 数据权限已实现 OrgId 关联与 ORM 拦截，但缺少机构实体的完整管理（CRUD + 树形接口）。本任务补齐机构管理闭环。

## 契约（按此实现）

1. **实体**（Business/Entitys/SysOrg.cs，继承 EntityTenant + [IncreTable]）：Pid（父级）、Name、Code（唯一）、Sort（排序）、Remark、Status（启用/禁用）
2. **服务**（Business/Services/Org/SysOrgService.cs）：
   - Page（分页查询，支持 Name/Code 模糊）
   - Tree（树形结构返回——Pid 递归组装，根节点 Pid=0）
   - Add / Update / Delete（Delete 校验：有子节点或关联用户时拒绝删除）
3. **控制器**（如项目已有 Controllers 模式则按模式，否则在 Service 层直接暴露路由）：`/api/sysOrg/*`
4. **种子数据**：初始根节点（Name=总部，Pid=0）

## 交付物

- `src/Business/Entitys/SysOrg.cs`
- `src/Business/Services/Org/SysOrgService.cs`（含 Dto 同批）
- 控制器文件（如适用）

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- CRUD 实测（curl 附输出）：Add 根节点→Add 子节点→Tree 返回树形→Update 改名→Delete 子节点成功→Delete 有子节点的父节点返回 400
- psql 验证 sys_org 表结构与种子数据存在
