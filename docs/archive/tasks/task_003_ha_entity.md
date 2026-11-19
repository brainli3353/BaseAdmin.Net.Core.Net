# task_id: task_003_ha_entity
# subject: task_003_ha_entity
# source: huluyeye
# created: 2026-08-26
# format: v3
# dep_on: task_002_ha_sqlsugar
# priority: P0
# files: src/Core/Entities/EntityBaseId.cs, src/Core/Entities/EntityBase.cs, src/Core/Entities/IEntityFilter.cs, src/Core/SqlSugarSetup.cs, src/Test/EntityBaseTest.cs

# 任务：实体基类（雪花主键/审计字段/软删）+ AOP 审计自动填充

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第一节「实体基类分层设计」+ 第二节 2.1「AOP 三段」——按此实现，禁止引入 Furion。
**代码纪律**：关键位置必须补中文注释。

## 背景

task_002 已完成 SqlSugar 接入 + YitIdHelper 雪花集成 + SysConfig 测试实体。本任务实现实体基类体系 + 框架自动填充（业务代码只管业务字段）。

## 契约（按此实现）

1. **`src/Core/Entities/EntityBaseId.cs`**：
```csharp
public abstract class EntityBaseId
{
    [SugarColumn(ColumnName = "Id", ColumnDescription = "主键Id", IsPrimaryKey = true, IsIdentity = false)]
    public virtual long Id { get; set; }
}
```

2. **`src/Core/Entities/IEntityFilter.cs`**（含 IDeletedFilter）：
```csharp
public interface IDeletedFilter { bool IsDelete { get; set; } }
```

3. **`src/Core/Entities/EntityBase.cs`**：继承 EntityBaseId + IDeletedFilter，字段照抄参考文档第一节（CreateTime/UpdateTime/CreateUserId/CreateUserName/UpdateUserId/UpdateUserName/IsDelete/DeleteTime，SugarColumn 特性与 IsOnlyIgnoreUpdate 照抄）。

4. **改造 SysConfig**（task_002 的测试实体）：改继承 EntityBase（保留 Name 字段）。

5. **`src/Core/SqlSugarSetup.cs` 加 AOP 审计自动填充**（DataExecuting，照抄参考文档 2.1）：
- InsertByObject：主键为空 → YitIdHelper.NextId()；CreateTime 为空 → DateTime.Now；CreateUserId/CreateUserName 从当前用户 Claim 填（用 IHttpContextAccessor，无用户则跳过）
- UpdateByObject：UpdateTime → DateTime.Now；IsDelete=true 时 DeleteTime → DateTime.Now；UpdateUserId/UpdateUserName 同 Insert
- 当前用户 Claim 类型：定义 `long? CurrentUserId` 辅助（IHttpContextAccessor.HttpContext.User.FindFirst("UserId")，无则 null）——M1 阶段登录还未接，此分支返回 null 即可，逻辑写全

6. **软删过滤器**：db.QueryFilter.AddTableFilter<IDeletedFilter>(u => u.IsDelete == false)

7. **单测** `src/Test/EntityBaseTest.cs`（xunit）：
- 雪花唯一：并发 1 万次 NextId 无重复（Parallel.For + HashSet 计数）
- 审计填充：插入 SysConfig → CreateTime 非空、Id > 0
- 软删：DeleteById 后查询（AsQueryable）查不到该记录（被过滤器过滤）、数据库里 IsDelete=true

## 交付物

- `/data/worktrees/admin/task_003_ha_entity/src/Core/Entities/EntityBase.cs`
- `/data/worktrees/admin/task_003_ha_entity/src/Core/SqlSugarSetup.cs`（加 AOP 审计 + 软删过滤器）
- `/data/worktrees/admin/task_003_ha_entity/src/Test/EntityBaseTest.cs`

（附带修改：EntityBaseId.cs/IEntityFilter.cs/SysConfig 改继承——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_003_ha_entity && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- `dotnet test src/Test` 全部通过（含雪花并发唯一/审计填充/软删过滤三用例）
- `docker exec postgres psql -U huluyeye -d huluyeye_admin -c "\d sys_config"` 列含 Id/CreateTime/CreateUserId/IsDelete 等基类字段
- git commit（提交号写入验收报告），验收报告 `acceptance_task_003_ha_entity.md` 附实测输出
