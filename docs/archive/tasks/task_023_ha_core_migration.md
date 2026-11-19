# task_id: task_023_ha_core_migration
# subject: task_023_ha_core_migration
# source: huluyeye
# created: 2026-08-28
# format: v3
# priority: P0
# dep_on: 无
# files: src/Business/Entitys/, src/Business/Services/, src/Business/SeedData/, src/Core/Entities/, src/Core/Services/, src/Core/SeedData/, src/Web.Entry/Program.cs, src/Web.Core/

# 任务：框架核心代码迁移（Business → Core，恢复 Business 为业务预留层）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**⚠️ P0 架构重构**——系统实体/服务/种子数据被错误放置在 Huluyeye.Business 中。按设计意图：
- **Huluyeye.Core** = 框架核心（系统实体/服务/基础设施/种子数据）
- **Huluyeye.Business** = 业务预留层（当前应为空，预留给 [情报] 等项目的业务代码）
本任务将全部框架代码从 Business 迁移到 Core，一次性完成。

**代码纪律**：⚠️ 只做文件移动和 namespace/using 替换——**禁止修改任何业务逻辑**；中文注释保留。

## 契约（按此实现）

### 1. 文件迁移（git mv 保历史）

| 源（Business） | 目标（Core） | 文件数 |
|---|---|---|
| `src/Business/Entitys/Sys*.cs` | `src/Core/Entities/` | 13 个实体 |
| `src/Business/Services/{Auth,Dict,File,Log,Menu,Org,Pos,Role,User}/` | `src/Core/Services/` | 9 个服务目录 |
| `src/Business/SeedData/Sys*Seed*.cs` | `src/Core/SeedData/` | 9 个种子文件 |

### 2. Namespace/Using 全局重写（批量 sed，全项目 src/ 下所有 .cs 文件）

```
namespace Huluyeye.Business.Entitys → namespace Huluyeye.Core.Entities
namespace Huluyeye.Business.Services. → namespace Huluyeye.Core.Services.
namespace Huluyeye.Business.SeedData → namespace Huluyeye.Core.SeedData
using Huluyeye.Business.Entitys → using Huluyeye.Core.Entities
using Huluyeye.Business.Services. → using Huluyeye.Core.Services.
using Huluyeye.Business.SeedData → using Huluyeye.Core.SeedData
```

注意：逐条精确匹配（含子路径如 `Huluyeye.Business.Services.Auth`），不要用宽泛的 `Huluyeye.Business` 全替换。

### 3. csproj 处理

- `Huluyeye.Core.csproj`：SDK glob 模式自动包含新目录 ✓ 无需修改（除非有特殊 PackageReference 需迁移）
- `Huluyeye.Business.csproj`：迁移后为空项目——**保留不删除**（业务预留层）

### 4. 禁止事项

- 禁止修改任何业务逻辑/方法签名/类定义——只做移动和替换
- 禁止删除 Huluyeye.Business 项目引用
- 禁止修改 tests/ 目录下的测试文件路径（测试引用的 namespace 会自动被 sed 重写覆盖）

## 交付物

- 迁移后的 `src/Core/`（含全部系统实体/服务/种子数据）
- 清空后的 `src/Business/`（仅剩 Class1.cs 占位 + csproj）
- 全项目 namespace/using 重写完成

## 验收标准

- `cd $WT && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- `dotnet test src/Test --no-build` 全绿
- psql 验证：重启后 sys_user/sys_role/sys_menu/sys_dict_type 表仍正常创建（CodeFirst 未破坏）
- 文件断言：`find src/Business -name "*.cs" | wc -l` → 1（仅 Class1.cs）
- 文件断言：`find src/Core/Entities -name "Sys*.cs" | wc -l` → ≥13
- 文件断言：`find src/Core/Services -type d | wc -l` → ≥9（Auth/Dict/File/Log/Menu/Org/Pos/Role/User）
- git diff main..HEAD --stat 的文件变更全部在 files 声明范围内
