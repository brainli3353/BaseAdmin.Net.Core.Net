# task_id: task_025_ha_fix_core_webref
# subject: task_025_ha_fix_core_webref
# source: huluyeye
# created: 2026-08-28
# format: v3
# priority: P0
# dep_on: 无
# files: src/Core/Services/, src/Core/Models/, src/Core/Entities/, src/Web.Core/Models/, src/Web.Core/Auth/, src/Core/Security/
# ⚠️ 已废弃(2026-08-28 葫芦爷爷)——由 task_028_ha_core_webdep_patch 取代。本稿两处与实测不符：①漏了 Huluyeye.Web.Core.Exceptions(实测 9 处错误中 8 处是它)；②"所有服务返回值用 ApiResult"有误(实测 Core 零引用,ApiResult 不需迁移)

# 任务：[框架] P0 紧急修复——消除 Core→Web.Core 反向依赖（编译当前无法通过）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**⚠️ P0 阻塞级**——当前 main 分支编译失败（11 个错误），所有后续任务的编译门/盲审/验收全部被阻塞。

## 编译错误根因

023 迁移将服务从 Business 移到了 Core，但 Core 服务文件中保留了 `using Huluyeye.Web.Core;`——Core.csproj 不引用 Web.Core 项目 → 类型找不到 → 编译失败。

涉及的 Web.Core 类型：
- `Huluyeye.Web.Core.Models.ApiResult` ← 所有服务返回值使用
- `Huluyeye.Web.Core.Auth.UserTokenState` ← UserTokenStateProvider 使用
- `Huluyeye.Web.Core.Auth.IUserTokenStateProvider` ← UserTokenStateProvider 使用

## 契约（按此实现）

1. **类型迁移**（从 Web.Core → Core，消除反向依赖）：
   - `Web.Core/Models/ApiResult.cs` → `Core/Models/ApiResult.cs`（namespace 改为 `Huluyeye.Core.Models`）
   - `Web.Core/Auth/UserTokenState.cs` → `Core/Models/UserTokenState.cs`（namespace 改为 `Huluyeye.Core.Models`）
   - `Web.Core/Auth/IUserTokenStateProvider.cs` → `Core/Interfaces/IUserTokenStateProvider.cs`（namespace 改为 `Huluyeye.Core.Interfaces`）
2. **Core 服务文件清理**：移除所有 Core/Services/*.cs 文件中的 `using Huluyeye.Web.Core;` 行（类型已在 Core 内，不需要跨项目引用）
3. **Web.Core 引用更新**：Web.Core 中引用上述类型的文件改用 Core 的 namespace
4. **其他 Web.Core 引用**：Core/Services 中如有其他 Web.Core 类型引用，同样处理（类型移到 Core 或改用 Core 内等效类型）
5. **零破坏**：所有服务的接口签名和业务逻辑不变——只做类型位置调整和 namespace 替换

## 交付物

- 迁移后的 `Core/Models/ApiResult.cs`、`Core/Models/UserTokenState.cs`
- 迁移后的 `Core/Interfaces/IUserTokenStateProvider.cs`
- 更新后的全部 `Core/Services/*.cs`（无 Web.Core 引用）
- 更新后的 `Web.Core/` 引用文件

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 **0 警告 0 错误**（这是本任务最关键的验收——编译必须通过）
- `dotnet test src/Test --no-build` 全绿
- `grep -rc "using Huluyeye.Web.Core" src/Core/` → **0**（Core 不得引用 Web.Core）
- psql 验证：重启后 sys_user 表正常创建
