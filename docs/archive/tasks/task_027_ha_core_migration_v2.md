# task_id: task_027_ha_core_migration_v2
# subject: task_027_ha_core_migration_v2
# source: huluyeye
# created: 2026-08-28
# format: v3
# priority: P0
# dep_on: 无
# files: src/Business/Entitys/, src/Business/Services/, src/Business/SeedData/, src/Core/Entities/, src/Core/Services/, src/Core/SeedData/, src/Core/Models/, src/Core/Interfaces/, src/Web.Core/, src/Web.Entry/Program.cs
# ⚠️ 已废弃(2026-08-28 葫芦爷爷)——由 task_028_ha_core_webdep_patch 取代。本稿前提有误：main 并未回退迁移(回退提交只撤销修复尝试),无需 v2 重迁移;且同样漏了 Exceptions 迁移、多迁了零引用的 ApiResult

# 任务：框架核心代码迁移 v2（含 Web.Core 依赖前置处理）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**⚠️ P0 架构重构 v2**——v1 迁移（task_023）因未处理 Web.Core 依赖导致编译失败。v2 增加前置步骤。

**代码纪律**：只做文件移动和 namespace/using 替换——禁止修改业务逻辑；每步完成后必须 build 验证。

## 契约（按此实现，分三步执行）

### 第一步：Web.Core 共享类型先移到 Core（消除反向依赖）

以下类型被 Business 的服务引用，必须先移到 Core：

| 源（Web.Core） | 目标（Core） | namespace 变更 |
|---|---|---|
| `src/Web.Core/Models/ApiResult.cs` | `src/Core/Models/ApiResult.cs` | `Huluyeye.Web.Core.Models` → `Huluyeye.Core.Models` |
| `src/Web.Core/Auth/UserTokenState.cs` | `src/Core/Models/UserTokenState.cs` | `Huluyeye.Web.Core.Auth` → `Huluyeye.Core.Models` |
| `src/Web.Core/Auth/IUserTokenStateProvider.cs` | `src/Core/Interfaces/IUserTokenStateProvider.cs` | `Huluyeye.Web.Core.Auth` → `Huluyeye.Core.Interfaces` |

**完成后必须 build 验证通过再继续第二步。**

### 第二步：Business 框架代码迁移到 Core

| 源（Business） | 目标（Core） | 文件数 |
|---|---|---|
| `src/Business/Entitys/Sys*.cs` | `src/Core/Entities/` | 13 实体 |
| `src/Business/Services/{Auth,Dict,File,Log,Menu,Org,Pos,Role,User}/` | `src/Core/Services/` | 9 服务目录 |
| `src/Business/SeedData/Sys*Seed*.cs` | `src/Core/SeedData/` | 9 种子 |

**完成后必须 build 验证通过再继续第三步。**

### 第三步：Namespace/Using 全局重写（批量 sed 全项目 src/ 所有 .cs）

```
namespace Huluyeye.Business.Entitys → namespace Huluyeye.Core.Entities
namespace Huluyeye.Business.Services. → namespace Huluyeye.Core.Services.
namespace Huluyeye.Business.SeedData → namespace Huluyeye.Core.SeedData
using Huluyeye.Business.Entitys → using Huluyeye.Core.Entities
using Huluyeye.Business.Services. → using Huluyeye.Core.Services.
using Huluyeye.Business.SeedData → using Huluyeye.Core.SeedData
```

**完成后必须 build 验证通过。**

### 禁止事项

- 禁止修改任何业务逻辑/方法签名/类定义
- 禁止删除 Huluyeye.Business 项目（保留为业务预留层）
- 每步之间必须 build 验证，失败时停止排查而不是继续

## 交付物

- `src/Core/` 含全部系统实体/服务/种子/共享类型
- `src/Business/` 清空（业务预留）
- 全项目 namespace/using 重写完成

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 **0 警告 0 错误**
- `dotnet test src/Test --no-build` 全绿
- `grep -rc "using Huluyeye.Web.Core" src/Core/` → **0**（Core 不得引用 Web.Core）
- `find src/Business -name "*.cs" | wc -l` → ≤1（业务预留层）
- `find src/Core/Entities -name "Sys*.cs" | wc -l` → ≥13
- psql 验证：重启后 sys_user/sys_role/sys_menu 表正常创建
