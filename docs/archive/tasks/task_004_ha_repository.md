# task_id: task_004_ha_repository
# subject: task_004_ha_repository
# source: huluyeye
# created: 2026-08-26
# format: v3
# dep_on: task_003_ha_entity
# priority: P0
# files: src/Core/Repository/SqlSugarRepository.cs, src/Core/Repository/ISqlSugarRepository.cs, src/Core/Models/PageResult.cs, src/Core/SqlSugarSetup.cs, src/Test/RepositoryTest.cs

# 任务：仓储 SqlSugarRepository + 分页 PageResult

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第三节「仓储设计」——按此实现，禁止引入 Furion。
**代码纪律**：关键位置必须补中文注释。

## 背景

task_002（SqlSugar 接入）+ task_003（实体基类）已完成。本任务实现通用仓储 + 分页。

## 契约（按此实现）

1. **`src/Core/Repository/SqlSugarRepository.cs`**：
```csharp
public class SqlSugarRepository<T> : SimpleClient<T>, ISqlSugarRepository<T> where T : class, new()
{
    // 构造函数注入 ISqlSugarClient（单例），base.Context = client
    // SimpleClient<T> 是 SqlSugar 内置（继承即得 CRUD：InsertAsync/UpdateAsync/DeleteByIdAsync/GetFirstAsync/IsAnyAsync/AsQueryable）

    // 分页（返回统一 PageResult<T>）：
    public async Task<PageResult<T>> ToPagedListAsync(int pageIndex, int pageSize)
    {
        RefAsync<int> total = 0;
        var list = await AsQueryable().ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<T> { Total = total, List = list };
    }
}
```

2. **`src/Core/Repository/ISqlSugarRepository.cs`**：仓储接口（CRUD 方法签名 + ToPagedListAsync）。

3. **`src/Core/Models/PageResult.cs`**：
```csharp
public class PageResult<T> { public long Total { get; set; } public List<T> List { get; set; } }
```

4. **注册**：SqlSugarSetup 里 `services.AddScoped(typeof(SqlSugarRepository<>))`（task_002 若已注册则确认即可）。

5. **单测** `src/Test/RepositoryTest.cs`（xunit，用 SysConfig 实体）：
- CRUD：Insert → GetFirstAsync 查回 → Update → IsAnyAsync → DeleteById（软删）→ 查询为空
- 分页：插入 25 条 → ToPagedListAsync(2, 10) → Total=25、List 10 条

## 交付物

- `/data/worktrees/admin/task_004_ha_repository/src/Core/Repository/SqlSugarRepository.cs`
- `/data/worktrees/admin/task_004_ha_repository/src/Core/Models/PageResult.cs`
- `/data/worktrees/admin/task_004_ha_repository/src/Test/RepositoryTest.cs`

（附带修改：ISqlSugarRepository.cs + SqlSugarSetup 注册——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_004_ha_repository && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- `dotnet test src/Test` 全部通过（CRUD + 分页用例）
- git commit（提交号写入验收报告），验收报告 `acceptance_task_004_ha_repository.md` 附实测输出
