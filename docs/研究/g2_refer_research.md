# 批 G2 移植调研:2.2 CopyRole / 2.4 菜单授权回读 / 2.5 机构裁剪 Disabled

> 依据件(约定 #13):批 H 实施前对参照仓 AdminNetPro 的对应实现逐点调研,落盘留痕。
> 参照仓:`/data/workspace/AdminNetPro/Admin.NET.Pro.HHBase/Admin.NET/`(只读,#7)。
> 调研日期:2026-09-16｜批 H 范围:81 功能点 2.2(🟡缺 CopyRole)/2.4(🟡缺菜单授权回读)/2.5(🟡缺裁剪+Disabled)。

## 0. 移植落点映射表(速查)

| 参照仓(AdminNetPro) | 本仓落点 | 说明 |
|---|---|---|
| `SysRoleService.CopyRole(CopyRoleInput)` | `SysRoleService.CopyRoleAsync(long)` | 参照另复制 sys_role_table(本仓 2.9 未立项,无此表) |
| `SysRoleMenuService.GetRoleMenuIdList(roleIdList)` | `SysRoleService.GetMenuListAsync(long roleId)` | 参照按角色 Id 集合查;本仓单角色(端点 ?roleId=) |
| `SysOrgService.GetList/GetTree`(非超管子树+Disabled) | `SysOrgService.TreeAsync`(全量树+Disabled 标记) | 参照偏差:本仓不做子树裁剪(见 §7 裁决⑤) |
| `SysOrgService.HandlerOrgTree`(递归标记) | `TreeAsync` 内联 HashSet 判定 | 全量树一次性判定,无递归(树是内存结构,展平判定等价) |
| `UserManager.OrgId/SuperAdmin` | `UserManager.IsSuperAdmin`＋`GetUserOrgScopeAsync` 枢纽 | 本仓范围集经枢纽收敛(含缓存),非参照的实时角色遍历 |

## 1. 参照 CopyRole(逐条)

```csharp
// Admin.NET.Core/Service/Role/SysRoleService.cs L179
public async Task CopyRole(CopyRoleInput input)
{
    var sysRole = await _sysRoleRep.GetByIdAsync(input.Id) ?? throw Oops.Oh(ErrorCodeEnum.D1002); // 源不存在

    var newRoleId = YitIdHelper.NextId();
    sysRole.Id = newRoleId;
    sysRole.Code = $"{sysRole.Code} - copy";
    sysRole.Name = $"{sysRole.Name} - copy";
    sysRole.CreateTime = DateTime.Now;
    sysRole.CreateUserId = null;            // 清审计字段(创建人=操作者由 AOP 重填)
    sysRole.CreateUserName = null;
    sysRole.UpdateTime = null;
    sysRole.UpdateUserId = null;
    sysRole.UpdateUserName = null;
    await _sysRoleRep.InsertAsync(sysRole);   // 整实体复用(含 DataScope/SysFlag/Remark 全字段)

    await _sysRoleOrgService.CopyRoleOrgByRoleId(input.Id, newRoleId);   // 复制数据范围
    await _sysRoleMenuService.CopyRoleMenuByRoleId(input.Id, newRoleId); // 复制菜单
    await _sysRoleApiService.CopyRoleApiByRoleId(input.Id, newRoleId);   // 复制接口黑名单
    await _sysRoleTableService.CopyRolTableByRoleId(input.Id, newRoleId);// 复制表字段权限(本仓无)
}
```

要点:
- 新 Id 雪花;Code/Name 追加 " - copy"(字面量);审计字段清零后由 AOP 重填。
- **参照未处理新 Code 碰撞**——连续复制同一源会撞 " - copy"。本仓 Add 有 Code 唯一校验 → 本批补碰撞拒绝(裁决②)。
- 四张关联表逐表复制;本仓只有三张(sys_role_menu/sys_role_api/sys_role_org)。

## 2. 参照 GetRoleMenuIdList(逐条)

```csharp
// Admin.NET.Core/Service/Role/SysRoleMenuService.cs L26
public async Task<List<long>> GetRoleMenuIdList(List<long> roleIdList)
{
    return await _sysRoleRepo.AsQueryable()
        .Where(u => roleIdList.Contains(u.RoleId))
        .Select(u => u.MenuId).ToListAsync();
}
```

要点:返回 **Id 集合**(非菜单实体)——前端拿菜单树(已有端点)＋勾选态(Id 集合)自行渲染。本仓端点 `GET /api/sysRole/menu?roleId=`。

## 3. 参照机构裁剪(逐条)

```csharp
// Admin.NET.Core/Service/Org/SysOrgService.cs L39 GetList
var userOrgIdList = await GetUserOrgIdList();            // 当前用户机构 Id 集(按最大数据范围策略)
// 带条件筛选时返回列表:WhereIF(userOrgIdList.Count > 0, u => userOrgIdList.Contains(u.Id))

// L57 GetTree
if (_userManager.SuperAdmin)
{
    orgTree = await iSugarQueryable.ToTreeAsync(...);    // 超管全量
}
else
{
    input.Id = input.Id == 0 ? _userManager.OrgId : input.Id;
    orgTree = await iSugarQueryable.ToTreeAsync(..., userOrgIdList...); // 从所属机构子树起
    HandlerOrgTree(orgTree, userOrgIdList);              // 子树内范围外节点 Disabled=true
}

// L86 HandlerOrgTree
private static void HandlerOrgTree(List<SysOrg> orgTree, List<long> userOrgIdList)
{
    foreach (var org in orgTree)
    {
        org.Disabled = !userOrgIdList.Contains(org.Id);  // 设置禁用/不可选择
        if (org.Children != null) HandlerOrgTree(org.Children, userOrgIdList);
    }
}
```

要点:
- **参照范围集来源**:`GetUserOrgIdList` 按角色 DataScope 策略遍历(All/DeptAndChild/Dept/Self/Define 各自展开机构集,取最大范围)——本仓该逻辑已收口于 `SysOrgService.GetUserOrgScopeAsync(userId)`(task_013 枢纽,含 7 天缓存＋变更即失效)→ **复用**(裁决见 #11)。
- 参照非超管树 = **所属机构子树**(input.Id 限定起点)＋子树内 Disabled 标记(防越权改/建)。
- 本仓 Tree 为全量树无入参 → 本批取「全量树＋范围外 Disabled」(裁决⑤)。

## 4. 本仓适配点(与参照差异)

| 点 | 参照 | 本仓 | 原因 |
|---|---|---|---|
| 错误出口 | `Oops.Oh(ErrorCodeEnum.D1002)` | `AppException("角色不存在")` | 本仓 #10 口径(批 G 同) |
| 事务 | 服务内 `_sysRoleRep` 逐表(无显式事务) | 端点 `[UnitOfWork]` | 本仓事务机制批 D 定案(端点级) |
| 菜单回读入参 | `List<long> roleIdList`(批量) | `long roleId`(单角色) | 端点 ?roleId= 单值;批量无消费方(最小必要) |
| Disabled 载体 | `SysOrg.Disabled`(实体字段,直接改实体) | `OrgOutput.Disabled`(DTO 字段) | 本仓 Tree 返回 DTO 投影(实体不加展示态字段) |
| 范围集 | 实时遍历角色 DataScope | `GetUserOrgScopeAsync`(缓存枢纽) | 复用优先(#11);口径与数据权限过滤完全一致 |
| 端点权限 | 动态 API 无独立权限码 | `[MenuPerm("sysRole/copy")]`/`[MenuPerm("sysRole/menu")]` | 批 A 2.10 全覆盖口径 |

## 5. 菜单/权限(种子)

| Id | Pid | Name | Title | Type |
|---|---|---|---|---|
| 10126 | 10003 | sysRole/copy | 角色复制 | 3(按钮) |
| 10127 | 10003 | sysRole/menu | 角色菜单授权查询 | 3(按钮) |

接批 G 种子段尾(10125 后);Pid=10003(sysRole 菜单行,按钮段 10020-10026/10090/10091 之后,OrderNo 10/11)。

## 6. 验证口径

- 单测 `BatchHRoleOrgTest` 6 用例(真实 PG):菜单回读与全量替换对偶 / CopyRole 三表全量复制 / CopyRole 碰撞+源不存在拒绝 / Tree 超管全 false / Tree 非超管范围外 true(经 GetUserOrgScopeAsync 枢纽断言 Org=3 → 仅 OrgA)/ Tree 匿名全 false。
- 端到端 10/10(轻量,REST only):登录两阶段 → add 临时角色 → grantMenu → menu 回读=[10003,10020] → copy(新 Id+副本菜单=源集) → 二次 copy 拒绝(碰撞文案) → tree 超管全 disabled=false → 清理。
- 批 H 无新增推送/实时机制 → 不做 WS 全量 e2e(裁决⑧)。

## 7. 本批自行裁决清单(呈报葫芦爷爷)

| # | 裁决 | 依据 |
|---|---|---|
| ① | CopyRole 不复制 sys_role_table(表级字段权限) | 本仓 2.9 未立项,无此表(最小必要 #8) |
| ② | CopyRole 新 Code 碰撞拒绝(AppException「已存在,请修改后再试」) | 参照未处理;本仓 Add 有 Code 唯一校验,保持同口径(连续复制同一源必撞 " - copy") |
| ③ | SysFlag/Remark/DataScope 全字段复制(含内置标志) | 参照整实体快照口径;内置角色复制出内置副本 |
| ④ | 菜单回读返回 Id 集合(List<long>),非菜单实体 | 参照同口径;前端用菜单树+勾选态渲染 |
| ⑤ | 2.5 取「全量树＋范围外 Disabled=true」,不做参照的子树裁剪(非超管仅返回所属机构子树) | 本仓 Tree 全量树无入参;改子树语义会破坏前端既有渲染(结构变更);Disabled 标记已实现「防越权挂靠/创建」核心语义;子树裁剪留前端按需(展示层问题) |
| ⑥ | 2.5 Page 端点不裁剪(保持全量管理视图) | 参照 GetList 仅在带筛选条件时裁剪;本仓 Page 定位管理视图,Disabled 语义由 Tree 承担 |
| ⑦ | 匿名/无 Claims(测试、内部调用)→ 不标记 Disabled(全 false) | 与超管同口径;无身份则无范围可裁(测试 DbScope 可直测) |
| ⑧ | e2e 取轻量(10 步 REST only),不做 WS 全量 | 批 H 无新增推送/实时机制;6/6 单测已覆盖全部行为口径(最小必要 #8) |
