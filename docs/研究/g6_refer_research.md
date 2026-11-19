# 批 L 参照调研：用户分页机构范围约束（2.1）

> 依据件：参照仓 Admin.NET 用户 Page 机构范围机制逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/Service/User/SysUserService.cs` `Page`（L61-99）＋`SysOrgService.GetUserOrgIdList`/`GetChildIdListWithSelfById`。

## §0 映射总表

| 参照 | 本仓现状/落点 | 处置 |
|---|---|---|
| 非超管只能看自己机构范围内的用户（`orgList = userOrgIdList`） | `PageAsync` 无数据范围过滤（任何登录用户见全量） | **补**：接 `GetUserOrgScopeAsync`（批 B 2.7 已建，含缓存/失效） |
| `input.OrgId > 0` → 子树（`GetChildIdListWithSelfById`）∩ 用户机构范围 | `GetChildIdListWithSelfById` 已有（internal，同程序集可达） | 复用 |
| 超管不过滤（`orgList = null`） | `UserManager.IsSuperAdmin`（AccountType==999）已有 | 复用 |
| `GetUserOrgIdList`（本人创建+扩展机构+角色机构+所属机构并集，7 天缓存） | `GetUserOrgScopeAsync`（批 B B2：MaxScope+OrgIds，7 天缓存＋变更失效 `DeleteUserOrgCacheAsync`）——**本仓口径强于参照**（含 DataScope 收敛，参照无 Self/All 枚举收敛） | 复用，不重造 |
| `PageUserInput.OrgId` 指定机构查询 | `PageUserInput` 无 OrgId | **补** `long? OrgId` |
| 排除超管账号行（`Where(u => u.AccountType != SuperAdmin)`） | 无 | **不补**（裁决②） |
| extOrg 扩展机构用户集（`SysUserExtOrg`） | 本仓无此实体（垂直扩展） | 不做（裁决③） |
| RealName/PosName/Phone/Keyword 多条件搜索 | 本仓仅 Account | 不做（裁决③，非本点范围） |

## §1 参照原文要点

```csharp
var userOrgIdList = await _sysOrgService.GetUserOrgIdList();   // 当前用户机构范围(超管=空)
if (input.OrgId > 0) {
    orgList = await GetChildIdListWithSelfById(input.OrgId);   // 指定机构子树(含自身)
    orgList = _userManager.SuperAdmin ? orgList : orgList.Where(u => userOrgIdList.Contains(u)).ToList(); // 非超管取交集
} else {
    orgList = _userManager.SuperAdmin ? null : userOrgIdList;  // 超管全量
}
// WhereIF(orgList != null || extOrgUserIdList != null, u => orgList.Contains(u.OrgId) || ...)
```

`GetUserOrgIdList` 恒含"当前所属机构"（`if (!orgIdList.Contains(userOrgId)) orgIdList.Add(userOrgId)`）——非超管最窄也可见同机构用户。

## §2 本仓现状

- `SysOrgService.GetUserOrgScopeAsync(userId)` → `(MaxScope, OrgIds)`（批 B B2）：All→空集（不过滤）；Org→[user.OrgId]；OrgAndChild→子树；Define→角色授权机构集（fail-closed）；Self→空集（[OwnerUser] 路径）；无角色→(Self,空集)。缓存 `KeyRoleMaxDataScope:{userId}`＋`KeyUserOrg:{userId}` 7 天，变更即失效。
- `SysOrgService.GetChildIdListWithSelfById(orgId)`（internal，BFS 整表载入）。
- `SysUserService.PageAsync`：仅 Account 模糊＋Id 倒序分页，投影 UserOutput（敏感字段不进响应）。
- `UserManager`：`IsSuperAdmin`/`UserId`（JWT Claims）。
- 测试先例（BatchBDataScopeTest）：`DefaultHttpContext` Claims 模拟身份＋`FakeHttpContextAccessor`（AddSqlSugar 前注册）＋真实 PG。

## §3 设计（本批实施口径）

`PageAsync` 数据范围过滤（最小必要，复用优先）：
1. 超管（`IsSuperAdmin`）或 `MaxScope == All` → 不过滤；
2. 否则 `visibleOrgs = OrgIds ∪ {user.OrgId?}`（同参照"恒含所属机构"：Define 授权不含本人机构时仍可见同机构，Self 无角色未挂机构时=空集）；
3. `input.OrgId > 0`（指定机构）→ `visibleOrgs ∩ GetChildIdListWithSelfById(input.OrgId)`；
4. `u.OrgId in visibleOrgs`（`visibleOrgs` 空 → 空结果，fail-closed）；
5. `PageUserInput` +`long? OrgId`。

## §4 验证口径（真实 PG＋身份模拟）

探针数据：机构树 A→A1→A2＋各机构挂用户＋角色（OrgAndChild/Org/Define 授权 A2）＋无角色未挂机构用户。用例：
1. 超管 → 全量
2. 超管指定 OrgId → 子树全量
3. OrgAndChild 用户（挂 A1）→ A1+A2，不含 A
4. Org 用户（挂 A1）→ 仅 A1
5. Define 用户（授权 A2，挂 A1）→ A2∪{A1}
6. Self 无角色未挂机构 → 空（fail-closed）
7. OrgAndChild 用户指定 OrgId=A1 → A1+A2；指定 A（超范围）→ 交集空
8. 指定 OrgId 跨用户范围交集语义
探针 finally 清理（机构/角色/用户/授权行）。
**e2e 不做**（裁决⑧）：`BatchLOrgScopeTest` 走完整 DI 链（`SysUserService`→`SysOrgService`→缓存→真实 PG＋Claims 身份模拟，批 B 先例），HTTP 绑定层（`SysUserController`/model binding）本批零改动，端到端无增量验证价值。

## §5 自行裁决清单（批内自主，批末呈报）

1. **接 `GetUserOrgScopeAsync` 不重造机构范围计算**（参照 `GetUserOrgIdList` 无 DataScope 收敛，本仓批 B 口径强于参照，复用=约定 #11）。
2. **不排除超管账号行**（参照排除因其超管是平台特例账号；本仓 999 超管在租户内是正常管理对象，列表可见合理；无业务诉求不加过滤）。
3. **extOrg 扩展机构/多条件搜索不做**（垂直扩展与非本点范围；`PageUserInput` 只加 `OrgId`）。
4. **visibleOrgs 空集 → 空结果 fail-closed**（未挂机构且无范围授权的用户不应看到任何人，宁缺勿漏）。
5. **指定 OrgId 交集为空 → 空结果**（越权指定机构不泄露数据，与参照同语义）。
6. **过滤在分页查询同一语句内完成**（非先查全量再内存过滤——数据量正确性）。
7. **空集短路在 C# 侧提前返回**：SQL 里写 `list.Count == 0 ? false : ...` 常量三元会被 SqlSugar 翻译成 `SqlFunc.IIF`，而 IIF 的 C# 常量条件分支不受支持（`do not support` 异常，批 L 实证）——空集直接返回空 `PageResult`，不触库。
8. **无 e2e**：单测走完整 DI 链（真实 PG＋Claims 身份模拟，批 B 先例），HTTP 层本批零改动。

## §6 范围边界（不做）

- 2.6 租户授权（职位缺件，独立点）。
- 用户导出/批量操作的机构范围（无此端点）。
- 机构范围的前端树联动（前端职责）。
