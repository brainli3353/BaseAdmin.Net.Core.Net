# g14 参照调研——2.6 职位管理缺件(批 T)

## §0 目标

功能清单 2.6(🟡):有 SysPos+SysPosService(Page/Add/Update/Delete)。缺:SysUserExtOrg 兼职表/非超管归属校验/Excel 导入导出(垂直后补)。

## §1 参照仓

- `Admin.NET.Core/Service/Pos/SysPosService.cs`(190 行):Page/GetList/Add/Update/Delete/Import/Export/DownloadTemplate。
  - **Add**:Name+Code 组合重复 → D6000;无归属校验(新记录创建者即操作者,参照无检查)。
  - **Update/Delete**:`if (!_userManager.SuperAdmin && sysPos.CreateUserId != _userManager.UserId) throw D6002`
    ——**非超管仅可操作自己创建的职位**(CreateUserId 归属,单点校验)。
  - **Delete 三层关联校验**:主职位用户(SysUser.PosId)+**附属职位用户(SysUserExtOrg.PosId)**+注册方案(SysUserRegWay.PosId)。
  - **Import/Export/Template**:NPOI Excel(参照引入 NPOI 依赖)。
- `Admin.NET.Core/Entity/SysUserExtOrg.cs`:系统用户扩展机构表(UserId+OrgId+PosId,EntityBaseId)——
  **语义=用户「附属机构」(一人多机构,每机构一职位)**,供 SysUserService(用户详情附属机构)/
  SysUserExtOrgService(管理端点)/SysTenantService/SysOrgService(删机构校验)消费。

## §2 本仓盘点

- `SysPosService`(task_020 契约):Page(Name/Code 模糊)/Add(Code 全表唯一)/Update(Code 唯一排除自身)/
  Delete(关联用户校验:SysUser.PosId 指向本职位即拒)——**均无归属校验**。
- **超管机制现成**:`UserManager.IsSuperAdmin => AccountType == 999`+`UserId`(Claim 解析单点,批 D 1.14);
  既有归属/权限校验范式 4 处(SysOrgService 2.5 机构树裁剪/SysRoleService 379 数据范围授予/
  SysNoticeService 63/SysOnlineUserService 45)——`_userManager.IsAuthenticated && UserId is > 0 && !IsSuperAdmin` 判定+AppException 业务码。
- **SysUser 单机构模型**:OrgId 单列+PosId 单列(批 L 2.1 用户 Page 机构范围=角色授权 Define∪所属机构,无多机构消费);
  SysUserExtOrg「附属机构」在本仓**零消费方**(无多机构挂靠/选择器/权限路径)。
- 无 NPOI 依赖(csproj 清点无);Excel 导入导出在 2.6 清单描述中已注「垂直后补」。
- 参照 SysUserRegWay(注册方案)本仓无此表(注册域未建)→ Delete 第三层校验无对象。

## §3 设计(范围裁决)

1. **建:非超管归属校验(2.6 缺件核心)**——`SysPosService.UpdateAsync/DeleteAsync` 加
   `if (_userManager.IsAuthenticated && _userManager.UserId is > 0 && !_userManager.IsSuperAdmin
       && pos.CreateUserId != _userManager.UserId.Value) throw AppException("仅创建者或超管可操作此职位", 400)`
   (参照 D6002 语义,本仓 AppException 业务码口径;判定范式与 SysOrgService 2.5/SysRoleService 数据范围授予同口径;
   匿名/测试无 Claims 时 UserId null → 放行,与既有 4 处范式一致)。Add 不加(参照同口径:新记录创建者即操作者)。
   注:SysPos 继承 EntityBase → CreateUserId 审计字段自动填充(Aop.DataExecuting 既有机制)→ 归属判定有数据基座。
2. **不建:SysUserExtOrg 附属机构表**(#8 零消费方——本仓单机构模型,权限路径经角色授权,无多机构挂靠需求;
   同 Excel「垂直后补」口径;后续业务需一人多机构时建表+消费路径同批落地)。
3. **不建:Excel 导入导出**(#8,清单已注垂直后补;NPOI 新依赖零当前消费)。
4. **不建:SysUserRegWay 注册方案删除校验**(参照有、本仓注册域未建,无对象)。

## §4 验证口径

`BatchTPosOwnershipTest`(真实 PG,探针职位 finally 清理;Claims 身份模拟=批 B 先例):
1. **超管操作他人职位**:超管 Claims → Update/Delete 他人创建职位成功。
2. **创建者操作自己的职位**:非超管 Claims(UserId=创建者)→ Update 成功。
3. **非超管越权拒绝**:非超管 Claims(UserId≠创建者)→ Update 抛 AppException(400);Delete 同。
4. **匿名放行**:无 Claims(测试基线)→ Update 成功(与既有 4 处范式一致,无身份不拦)。
5. **既有契约回归**:Delete 关联用户拒(既有)/Code 唯一(既有)不回归。
容器模板:in-memory config + AddSqlSugar/AddCache/AddProjectOptions/AddAutoServices + Claims 模拟 + IsPostgresReachable 降级。

## §5 参照偏差声明

1. **Oops.Oh(ErrorCodeEnum)→ AppException(message, code)**(本仓统一出口)。
2. **UserManager.SuperAdmin→IsSuperAdmin**(本仓门面命名,批 D 1.14 单点)。
3. **Add 不校验归属**(参照同口径)。
4. **Delete 三层→一层**(ExtOrg/RegWay 本仓无表;主职位关联校验既有)。
5. **ExtOrg 不建/Excel 不建**(§3 #8 裁决,参照功能保留在清单缺口描述中注明「后补」)。

## §6 不做(范围边界)

- SysUserExtOrg 附属机构(零消费方,#8 后补);
- Excel 导入导出/模板下载(NPOI 新依赖,垂直后补);
- SysUserRegWay 注册方案(注册域未建);
- GetList(参照的列表端点——本仓 Page 已覆盖列表语义,#11 复用优先)。
