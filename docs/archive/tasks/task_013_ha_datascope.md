# task_id: task_013_ha_datascope
# subject: task_013_ha_datascope
# summary: 机构数据范围过滤(基类扩展+ORM 拦截+缓存失效)
# source: huluyeye
# created: 2026-08-28
# format: v3
# repo: huluyeye-admin
# priority: P1
# dep_on: task_024_ha_auto_service
# files: src/Core/Entities/EntityBaseData.cs, src/Core/Attributes/OwnerOrgAttribute.cs, src/Core/Attributes/OwnerUserAttribute.cs, src/Core/Enums/, src/Core/SqlSugar/, src/Core/Services/Org/, src/Core/Services/Role/

# 任务：[框架] 数据权限重写版——机构数据范围过滤（基于既有 019 机构体系，聚焦缺口）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**重写说明（2026-08-28 葫芦爷爷）**：本版取代 2026-08-27 旧稿（已废弃）。依据最新架构定案对齐：①框架代码一律落 **Core/**（023），Business 为空置预留层；②服务注册一律 **[Service] 自动发现**（024），任务书不声明注册入口；③删去已落地部分——**SysOrg 实体+服务（019 已建，现 Core/Entities/SysOrg.cs + Core/Services/Org/SysOrgService.cs）、SysUser.OrgId（已增量加列）**——本任务只做数据权限缺口。
**必读参考**：仓库 docs/框架开发参考手册.md §2.5（机构）+ §2.7（数据范围）+ §2.8（ORM 拦截 ★）+ §2.13（缓存一致性）；现有 Core/Services/Org/SysOrgService.cs（019 交付，含树形 CRUD/GetUserOrgIdList 待扩）。

## 背景

框架已有租户基座（EntityTenant）+ 机构体系（019），缺**机构数据权限过滤**（实体继承基类即自动获得机构范围过滤，业务零侵入）——baseadmin 最精华机制，intel 等业务必需。

## 契约（按此实现，全部落点 Core/）

1. **实体基类扩展**（Core/Entities/，继承链扩展）：
   - `EntityBaseData : EntityBase, IOrgIdFilter`：+`[OwnerOrg] long? CreateOrgId`
   - `EntityBaseUpdateData : EntityBaseData`：+`[OwnerOrg] long? UpdateOrgId`
   - `EntityTenantBaseData : EntityTenant, IOrgIdFilter`（租户+机构组合基类——现有租户实体如需数据权限改继承它）
   - 特性：`OwnerOrgAttribute`/`OwnerUserAttribute`（Core/Attributes/，空标记——**注意：不是 ModelBinding 特性**，仅过滤标记）

2. **数据范围定义**（Core/Enums/）：
   - `DataScopeEnum`：1 全部 / 2 本部门及以下 / 3 本部门 / 4 仅本人 / 5 自定义
   - **SysRole 增量加列 `DataScope`**（默认 4 仅本人；增量列不破坏存量）

3. **机构集合计算**（挂现有 Core/Services/Org/SysOrgService.cs，§2.7——**不新建服务，扩既有类**）：
   - `GetUserOrgIdList(userId)`：多角色最大范围收敛（枚举值最小即最大）→ 返回机构集合（含本人直属/本部门及以下子树/自定义角色机构并集）
   - 缓存：`KeyUserOrg:{userId}` 7 天 + `KeyRoleMaxDataScope:{userId}`
   - 非超管授权自定义范围时校验所选机构在自己集合内

4. **ORM 层统一拦截**（Core/SqlSugar/SqlSugarFilter.cs + SqlSugarSetup.cs，§2.8）：
   - `SetOrgEntityFilter(db)`：读取侧——按用户最大数据范围分流（All 跳过/Self 用 [OwnerUser] 生成 CreateUserId==userId/机构类用 [OwnerOrg] 生成 Contains(orgIds)）
   - 反射遍历 EntityBaseData/EntityTenantBaseData 子类（排除非当前库实体），表达式树拼条件（多 [OwnerOrg] 字段 OR），挂 QueryFilter
   - 过滤器按 dbConfigId:userId 缓存；超管入口短路（已有）
   - 扩展：`IgnoreOrgData()`（ClearFilter 放行）

5. **缓存一致性**（§2.13）：授权/机构变更落点调 `DeleteUserOrgCache(userId)`（删 5 键：KeyUserOrg/KeyUserApi/KeyUserRole/KeyRoleMaxDataScope/orgList 过滤器缓存）——**现有 SysRoleService/SysOrgService/SysUserService 的变更路径补失效调用**（增量侵入最小化）

## 交付物

- `/data/worktrees/admin/task_013_ha_datascope/src/Core/Entities/EntityBaseData.cs`（EntityBaseUpdateData/EntityTenantBaseData + IOrgIdFilter + OwnerOrg/OwnerUser 特性同批）
- `/data/worktrees/admin/task_013_ha_datascope/src/Core/SqlSugar/SqlSugarFilter.cs`（SetOrgEntityFilter + 装配 + 缓存，同批 DataScopeEnum/SysRole.DataScope）
- `/data/worktrees/admin/task_013_ha_datascope/src/Core/Services/Org/SysOrgService.cs`（GetUserOrgIdList 扩展 + 三服务失效钩子，同批）

## 验收标准

- `cd /data/worktrees/admin/task_013_ha_datascope && dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误；`dotnet test src/Test --no-build` 全绿
- 实测（验收报告附 curl/psql 输出）：
  - 建机构树（父/子/孙；沿用现有机构接口）+ 用户分配 OrgId + 角色设不同 DataScope
  - 数据范围=本部门及以下：该用户查询含 OrgId 实体的列表 → 只返回本部门及以下机构数据（psql 对比过滤生效）
  - 数据范围=仅本人：只返回 CreateUserId==自己 的数据
  - 数据范围=全部（超管）：不受过滤
  - 授权变更后缓存即时失效（改角色 DataScope → 用户下次查询立即生效）
  - 存量数据（无 OrgId/CreateOrgId 的行）在机构过滤下行为符合语义（文档化说明）
- git commit（提交号写入验收报告），验收报告 acceptance_task_013_ha_datascope.md 附实测输出