# task_id: task_002_ha_sqlsugar
# subject: task_002_ha_sqlsugar
# source: huluyeye
# created: 2026-08-26

# 任务：SqlSugar 单库接入（PostgreSQL）+ CodeFirst 自动建表

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0，src/ 五项目骨架已建好）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第二节「ORM 初始化设计（零 Furion 版）」——按此实现，禁止引入 Furion/Admin.NET 任何包。
**代码纪律**：关键位置必须补中文注释（类/方法头一句话职责、复杂逻辑 WHY）。

## 背景

数据库已建好（仅开发用，生产密钥环境变量注入）：
`Host=172.18.0.3;Port=5432;Database=huluyeye_admin;Username=huluyeye;Password=huluyeye_dev_2026`

## 契约（按此实现）

1. **NuGet**：src/Core 加 `SqlSugarCore`（5.1.4.x 最新）——唯一新包，理由=ORM。

2. **雪花集成**：thirdparty/YitIdHelper（Yitter.IdGenerator.csproj，已存在）加入解决方案 + Core 项目引用它。Core 里封装 `YitIdHelper` 静态类（Init + NextId 转发，工作节点 Id 从配置读，默认 1）。

3. **`src/Core/SqlSugarSetup.cs`** 静态类：
```csharp
public static class SqlSugarSetup
{
    // AddSqlSugar(this IServiceCollection services, IConfiguration config)
    // ①雪花：StaticConfig.CustomSnowFlakeFunc = YitIdHelper.NextId
    // ②SqlSugarScope 初始化：
    //   ConnectionConfig { ConfigId="main", ConnectionString=config["ConnectionStrings:Default"],
    //     DbType=DbType.PostgreSQL, IsAutoCloseConnection=true, InitKeyType=InitKeyType.Attribute }
    // ③注册：services.AddSingleton<ISqlSugarClient>(sqlSugar)
    // ④AOP：OnError 记错误 SQL 日志（ILogger）；OnLogExecuting 打印执行 SQL（开发期）
    // ⑤InitTables(this ISqlSugarClient db) 方法：扫描程序集贴 [SugarTable] 的实体 → db.CodeFirst.InitTables(type)
}
```

4. **`src/Web.Entry/appsettings.json`** 加 ConnectionStrings 段（上面的开发连接串，注释"仅开发用，生产密钥环境变量注入"）。

5. **`src/Web.Entry/Program.cs`**：builder.Services.AddSqlSugar(builder.Configuration)；启动时调用 InitTables。

6. **测试实体** `src/Core/Entities/SysConfig.cs`：贴 `[SugarTable("sys_config", "系统配置")]` + `[IncreTable]`，仅两字段：long Id（雪花主键，`[SugarColumn(IsPrimaryKey=true, IsIdentity=false)]`）、string? Name。用途=验证建表（task_003 完成后改继承 EntityBase）。

## 交付物

- `/data/worktrees/admin/task_002_ha_sqlsugar/src/Core/SqlSugarSetup.cs`
- `/data/worktrees/admin/task_002_ha_sqlsugar/src/Web.Entry/Program.cs`
- `/data/worktrees/admin/task_002_ha_sqlsugar/src/Core/Entities/SysConfig.cs`

（附带修改：Core.csproj 加包与引用、appsettings.json 连接串、解决方案加 YitIdHelper——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_002_ha_sqlsugar && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- `dotnet run --project src/Web.Entry` 启动日志出现「初始化表」且无异常退出
- `docker exec postgres psql -U huluyeye -d huluyeye_admin -c "\dt"` 输出含 sys_config 表
- git commit（提交号写入验收报告），验收报告 `acceptance_task_002_ha_sqlsugar.md` 附以上实测输出
