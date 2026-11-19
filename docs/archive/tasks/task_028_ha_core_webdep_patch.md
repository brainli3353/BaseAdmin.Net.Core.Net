# task_id: task_028_ha_core_webdep_patch
# subject: task_028_ha_core_webdep_patch
# source: huluyeye
# created: 2026-08-28
# format: v3
# priority: P0
# dep_on: 无
# files: src/Core/Exceptions/, src/Core/Models/, src/Core/Interfaces/, src/Core/Services/, src/Web.Core/Exceptions/, src/Web.Core/Auth/, src/Web.Core/Middlewares/, src/Web.Entry/Program.cs, src/Web.Entry/Controllers/SysOrgController.cs, src/Web.Entry/Controllers/DemoController.cs

# 任务：[框架] P0 编译修复——消除 Core→Web.Core 反向依赖（023 迁移后 68 错误）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**⚠️ P0 阻塞级**——当前 main（c6e94e7，即 023 迁移态；回退提交 c6e94e7 只撤销了修复尝试、未撤销迁移本身）编译失败：**68 错误 1 警告，全部集中在 Huluyeye.Core 工程**（2026-08-28 葫芦爷爷实测 dotnet build）。所有后续任务的编译门/盲审/验收全部被阻塞。

本任务书取代草案 task_025/task_027（未投递、已废弃）——根因与改动清单以本版实测为准。

## 根因（实测，非推断）

023 迁移把框架服务从 Business 移入 Core，但 Core 服务代码仍引用 **Web.Core 层的类型**。Core 是最底层工程（无任何项目引用），Web.Core 类型不在其引用闭包内 → 编译失败。

**错误构成（68 = 9 + 2 + 57）**：
- **9 处 CS0234**（using 行，根因点）：8 个服务文件 `using Huluyeye.Web.Core.Exceptions;` + `Core/Services/Auth/UserTokenStateProvider.cs(3)` `using Huluyeye.Web.Core.Auth;`
- **2 处 CS0246**：UserTokenStateProvider.cs(11,20) 找不到 IUserTokenStateProvider / UserTokenState
- **57 处 CS0246**（级联，随 using 修复自动消失）：各服务 `throw new AppException/BadRequestException` 使用点（全仓共 60 处 AppException + 8 处 BadRequestException）
- **1 警告 CS0105**：SysDictDataService.cs(5) 与 (1) 重复 `using Huluyeye.Core.Entities`

**涉及的 Web.Core 类型（实测仅此 4 个文件，Core 无其他 Web.Core 引用）**：

| Web.Core 现位置 | 类型 | Core 侧使用 |
|---|---|---|
| src/Web.Core/Exceptions/AppException.cs | AppException | 8 个服务共 60 处 throw |
| src/Web.Core/Exceptions/BadRequestException.cs | BadRequestException | 8 处 throw（SysFileService 等） |
| src/Web.Core/Auth/UserTokenState.cs | UserTokenState | UserTokenStateProvider 返回值 |
| src/Web.Core/Auth/IUserTokenStateProvider.cs | 接口 | UserTokenStateProvider 实现 |

**⚠️ 明确不迁移**：`Web.Core/Models/ApiResult.cs`——实测 Core 服务零引用（草案 025/027 假设"所有服务返回值用 ApiResult"有误），它仅被 Web.Core/Web.Entry 使用，留在原位零改动。

## 契约（按此实现，分三步，每步过构建门再继续）

### 第一步：类型下移（git mv 保历史，4 个文件 Web.Core → Core）

| 源 | 目标 | namespace 变更 |
|---|---|---|
| src/Web.Core/Exceptions/AppException.cs | src/Core/Exceptions/AppException.cs | Huluyeye.Web.Core.Exceptions → Huluyeye.Core.Exceptions |
| src/Web.Core/Exceptions/BadRequestException.cs | src/Core/Exceptions/BadRequestException.cs | 同上 |
| src/Web.Core/Auth/UserTokenState.cs | src/Core/Models/UserTokenState.cs | Huluyeye.Web.Core.Auth → Huluyeye.Core.Models |
| src/Web.Core/Auth/IUserTokenStateProvider.cs | src/Core/Interfaces/IUserTokenStateProvider.cs | Huluyeye.Web.Core.Auth → Huluyeye.Core.Interfaces |

**构建门①：`dotnet build src/Core/Huluyeye.Core.csproj` 0 错误**（此时 Core 服务 using 尚未重写会报错——先完成第二步的 Core 部分再过此门；顺序：第一步+第二步 Core 部分一起过门①）

### 第二步：using 重写（精确按文件清单，禁止全项目宽泛 sed）

**Core（9 文件，反向依赖清零）**：
- 8 个服务文件：`using Huluyeye.Web.Core.Exceptions;` → `using Huluyeye.Core.Exceptions;`
  （SysDictDataService / SysDictTypeService / SysFileService / SysMenuService / SysOrgService / SysPosService / SysRoleService / SysUserService）
- Core/Services/Auth/UserTokenStateProvider.cs：`using Huluyeye.Web.Core.Auth;` → `using Huluyeye.Core.Interfaces;` + `using Huluyeye.Core.Models;`

**构建门①**：Core 工程 build 0 警告 0 错误后继续。

**Web.Core（2 文件，Web.Core→Core 引用方向合法；PermissionMiddleware/OpLogFilter 实测零引用不动）**：
- Auth/JwtAuthHandler.cs(18,20)：使用 IUserTokenStateProvider——补 `using Huluyeye.Core.Interfaces;`
- Middlewares/ExceptionMiddleware.cs：`using Huluyeye.Web.Core.Exceptions;` → `using Huluyeye.Core.Exceptions;`

**Web.Entry（3 文件）**：
- Program.cs(17)：`using Huluyeye.Web.Core.Auth;` → `using Huluyeye.Core.Interfaces;`；确认 L60 `AddScoped<IUserTokenStateProvider, UserTokenStateProvider>()` 两类型均可见（实现类在 Huluyeye.Core.Services.Auth，如缺 using 补上）
- Controllers/SysOrgController.cs、DemoController.cs：`using Huluyeye.Web.Core.Exceptions;` → `using Huluyeye.Core.Exceptions;`

### 第三步：警告清零 + 横向排查

- SysDictDataService.cs：删除 (5) 行重复的 `using Huluyeye.Core.Entities;`（与 (1) 重复，CS0105）
- **横向排查（防同类残留）**：`grep -rn "Huluyeye.Web.Core" src/Core --include=*.cs` 必须 0 命中；如发现本任务书未列的 Web.Core 类型引用，同样下移处理并在验收报告披露

**构建门②（终门）**：`dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 → `dotnet test src/Test --no-build` 全绿

## 禁止事项

- 禁止迁移 ApiResult（未受影响，动了=违反最小改动）
- 禁止修改业务逻辑/异常语义/方法签名/类定义——只做文件移动、namespace 替换、using 增删
- 禁止给 Core.csproj 添加对 Web.Core 的 ProjectReference（方向倒置=架构破坏）
- 禁止删除 Huluyeye.Business 项目/引用（业务预留层）
- 禁止改注释（Program.cs L55 等过期表述属已知残留，留文档同步任务）

## 交付物

- src/Core/Exceptions/（AppException.cs + BadRequestException.cs）、src/Core/Models/UserTokenState.cs、src/Core/Interfaces/IUserTokenStateProvider.cs
- 清理后的 src/Web.Core/（Exceptions 目录移除；Auth 目录无 UserTokenState*/IUserTokenStateProvider* 文件）
- 全部 using 重写完成 + CS0105 修复

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` → **0 警告 0 错误**（最关键硬门）
- `dotnet test src/Test --no-build` → 全绿
- `grep -rn "Huluyeye.Web.Core" src/Core --include=*.cs | wc -l` → **0**（Core 零反向依赖）
- `test ! -d src/Web.Core/Exceptions` 通过，且 `ls src/Core/Exceptions` → 2 文件
- `find src/Web.Core/Auth -name 'UserTokenState*' -o -name 'IUserTokenStateProvider*' | wc -l` → 0
- `git diff main..HEAD --stat` 变更全部落在 files 声明范围内
- psql 验证：重启后 sys_user/sys_dict_type 表正常创建（本补丁不触实体/种子，CodeFirst 应无影响）
