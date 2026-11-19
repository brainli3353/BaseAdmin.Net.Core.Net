# task_id: task_024_ha_auto_service
# subject: task_024_ha_auto_service
# source: huluyeye
# created: 2026-08-28
# format: v3
# priority: P0
# dep_on: task_023_ha_core_migration
# files: src/Core/Attributes/, src/Core/Extensions/, src/Web.Core/

# 任务：服务自动发现机制（开发与注册分离——热点文件根除）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §2.10（按钮权限——ServiceAttribute 类似思路）。
**代码纪律**：中文注释；禁止修改 files 外文件；禁止修改任何业务逻辑（纯基建改造）。

## 背景

当前所有服务的 DI 注册都写在 Program.cs 中——导致 Program.cs 成为热点文件，8 本任务书因 files 域重叠被迫串行。本任务实现服务自动发现机制：服务类标注 `[Service]` 特性即可自动注册，新增模块不再需要修改 Program.cs。

## 契约（按此实现）

1. **ServiceAttribute**（src/Core/Attributes/ServiceAttribute.cs）：
   - `[Service(ServiceType = typeof(IMyService), Lifetime = ServiceLifetime.Scoped)]`
   - 属性：ServiceType（可选——省略时自动匹配第一个接口）、Lifetime（Scoped/Singleton/Transient，默认 Scoped）
2. **自动注册扩展**（src/Core/Extensions/ServiceCollectionExtensions.cs）：
   - `AddAutoServices(this IServiceCollection services, params Assembly[] assemblies)` ——反射扫描指定程序集中所有标 `[Service]` 的类，按特性配置注册到 DI
   - 排除抽象类和接口
3. **已有服务迁移**：为全部已有服务类（Auth/Dict/File/Log/Menu/Org/Pos/Role/User 等）添加 `[Service]` 特性标注
4. **Program.cs 更新**：将散落的 `services.AddScoped/AddSingleton/AddTransient` 调用替换为 `services.AddAutoServices(typeof(Xxx).Assembly)` 一次性调用
5. **零破坏**：所有服务的解析行为不变（生命周期/接口映射与迁移前一致）

## 交付物

- `src/Core/Attributes/ServiceAttribute.cs`
- `src/Core/Extensions/ServiceCollectionExtensions.cs`
- `src/Web.Core/` 或 `src/Web.Entry/Program.cs`（AddAutoServices 调用替换散落注册）
- 全部已有服务类的 [Service] 特性标注（多文件修改——在 Core/Services/ 下的各服务文件中添加）

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- `dotnet test src/Test --no-build` 全绿
- psql 验证：重启后全部功能正常（sys_user/sys_role/sys_menu/sys_dict_type 表正常创建）
- `grep -c "AddScoped\|AddSingleton\|AddTransient" src/Web.Entry/Program.cs` 显著减少（散落注册被 AutoServices 替代）
- `grep -c "[Service]" src/Core/Services/*/*.cs` ≥ 9（每个服务文件都有标注）
