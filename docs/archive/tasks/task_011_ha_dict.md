# task_id: task_011_ha_dict
# subject: task_011_ha_dict
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P0
# dep_on: task_008_ha_rbac
# files: src/Business/Entitys/SysDictType.cs, src/Business/Entitys/SysDictData.cs, src/Business/Services/Dict/, src/Web.Entry/Controllers/SysDictController.cs, src/Business/SeedData/

# 任务：数据字典模块（类型+值两级模型 + 缓存读取 + 管理 API）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §4.2（数据字典：结构树/代码参考/替换点）+ docs/baseadmin设计精华参考.md 第八节（目录约定）。
**代码纪律**：关键位置必须补中文注释；实体 [IncreTable]、种子 [IncreSeed]；禁止修改框架代码（Core/Web.Core 只做支撑）。
**投递前提**：008 已 accepted（M2 全绿）；与 010（配置）无 files 冲突，可并行派发。

## 背景

框架 M2 已交付（RBAC/审计/增量/多库/租户基座）。数据字典是框架标配服务层（intel 养号状态流转、各业务枚举字典化都依赖）——按参考手册 §4.2 实现。

## 契约（按此实现）

1. **实体**（Business/Entitys/，继承 EntityTenant + [IncreTable]）：
   - `SysDictType`：Name（名称）、Code（编码，唯一）、IsEnum（是否枚举转换字典）、Remark
   - `SysDictData`：DictTypeId（外键）、Code（同类型下唯一）、Value、Label、TagType/StyleSetting/ClassSetting（前端渲染样式）、OrderNo、IsEnum、ExtData、Remark
   - 关系：类型 LEFT JOIN 值（导航属性）

2. **服务**（Business/Services/Dict/，按手册第八节：域目录 + Dto/）：
   - `SysDictTypeService`：Page/Add/Update/Delete（级联删该类型下所有 DictData，[UnitOfWork] 事务）
   - `SysDictDataService`：
     - `GetDataListByIdOrCode(long? typeId, string code)` —— ★缓存读取：键 `sys_dict:{code}`，跨租户 ClearFilter 只取双 Enable，未命中 LEFT JOIN 一次查出缓存
     - Page/Add/Update/Delete/UpdateStatus——**写路径操作前主动 `_cache.Remove("sys_dict:{code}")`**（改完即失效，不靠过期）
   - DTO 就近 Dto/（AddDictTypeInput/PageDictTypeInput/DictTypeOutput/AddDictDataInput 等）

3. **控制器**（Web.Entry/Controllers/SysDictController.cs）：路由 /api/sysDictType/*、/api/sysDictData/*，薄层（绑定→Service→ApiResult）

4. **种子**（Business/SeedData/）：示例字典「账号状态」（intel 用：待验号/观察/养号中/停用/冻结，[IncreSeed]）——状态码与 intel 契约一致

## 交付物

- `/data/worktrees/admin/task_011_ha_dict/src/Business/Entitys/SysDictType.cs`（SysDictData.cs 同批）
- `/data/worktrees/admin/task_011_ha_dict/src/Business/Services/Dict/SysDictDataService.cs`（SysDictTypeService + Dto/ 同批）
- `/data/worktrees/admin/task_011_ha_dict/src/Web.Entry/Controllers/SysDictController.cs`

（附带修改：种子、缓存键常量 CacheConst——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_011_ha_dict && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 接口实测（curl，验收报告附输出）：
  - POST /api/sysDictType/add 新增类型成功；重复 Code → 400
  - POST /api/sysDictData/add 新增值成功；同类型重复 Code → 400
  - GET /api/sysDictData/list?code=account_status → 返回字典值列表（缓存生效：首次 DB、二次命中）
  - PUT /api/sysDictData/update 改值后，list 接口立即返回新值（缓存失效实测）
  - DELETE /api/sysDictType/delete 删类型 → 该类型下 DictData 级联删除（psql 验证）
- 种子幂等：重启两次 `select count(*) from sys_dict_data` 行数不变
- git commit（提交号写入验收报告），验收报告 acceptance_task_011_ha_dict.md 附实测输出
