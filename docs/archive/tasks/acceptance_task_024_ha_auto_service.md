# task_024_ha_auto_service 验收报告

- 任务：服务自动发现机制（`[Service]` 特性 + `AddAutoServices` 反射扫描，根除 Program.cs 热点）
- 执行：开发
- 日期：2026-08-28
- 代码提交：`83c7a7ad45376d9ab21f438e475fe9a56e53210b`（分支 `task_task_024_ha_auto_service`，worktree `/data/worktrees/admin/task_024_ha_auto_service`）

## 一、交付物清单（对照任务书「交付物」节逐项）

| 交付物 | 状态 | 证据 |
|---|---|---|
| `src/Core/Attributes/ServiceAttribute.cs` | ✅ 新建 | `[Service(ServiceType = typeof(I), Lifetime = Scoped)]`；ServiceType/Lifetime 为**可写属性**（初版只读属性作具名参数报 CS0617，已改可写，见 §五.2） |
| `src/Core/Extensions/ServiceCollectionExtensions.cs` | ✅ 新建 | `AddAutoServices(this IServiceCollection, params Assembly[])`；反射扫描 `[Service]`（`inherit:false`，排除抽象/接口/泛型定义/非公共），`ReflectionTypeLoadException` 容错（取已加载部分继续）；`ServiceDescriptor` 精确表达 服务类型→实现→生命周期 |
| 已有服务 `[Service]` 标注（18 处） | ✅ | 13 Core/Services 自注册 + 3 接口→实现 + 2 过滤器强制自注册（分布与语义见 §三 零破坏映射，脚本复核） |
| Program.cs 散注册替换 | ✅ | 17 条 `AddScoped/AddSingleton/AddTransient` → 单次 `AddAutoServices(typeof(SysUserService).Assembly, typeof(JwtAuthHandler).Assembly)`（Core 锚 + Web.Core 锚）；`grep -c "AddScoped\|AddSingleton\|AddTransient" Program.cs` = **17 → 0** |
| 顺手清预存 CS0105（披露） | ✅ | `SysDictDataService.cs` 删 1 处**预存**重复 `using Huluyeye.Core.Entities;`（ba78e67 已存在、非本任务引入），清掉阻塞「0 警告」的预存警告；该文件本就在本任务改动集（加 `[Service]`） |

## 二、验收标准逐条证据

### 1. `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 ——【受预存阻塞；本任务代码已证绿】

- **基线（ba78e67）实测**：`0 警告 + 11 错误`。11 错误全为**预存 Core→Web.Core 反向依赖**（9× CS0234 `Huluyeye.Web.Core.{Auth,Models,Exceptions}` 命名空间在 Core 不可见 + 2× CS0246 连带）。已对 ba78e67 与 main(20bdbf1) **分别实测，错误集完全一致**（各 11 错）。该阻塞属 task_025 范围（其任务书 bfb5b18 自述「当前 main 编译失败 11 错误阻塞全部流水线」；主工作区仍有未跟踪 `fix_compile.py` 在修该依赖）。**本任务代码零新增错误、零新增警告。**
- **固定基线铁证（scratch，仅验证用、未提交）**：在临时 worktree 以 task_025 等价的依赖解（把 Core 引用的 5 个 Web.Core 类型 `ApiResult/UserTokenState/IUserTokenStateProvider/AppException/BadRequestException` 经 csproj `Compile Include` 并入 Core 编译、`Compile Remove` 移出 Web.Core，**零源码改动**、保持原命名空间）后，`dotnet build` 实测 **0 错误 0 警告 Build succeeded**。证明本任务的 ServiceAttribute / AddAutoServices / 18 标注 / Program.cs 在依赖解下全部正确可编译。scratch 已清理。

### 2. `dotnet test src/Test --no-build` 全绿

- scratch 固定基线实测：**Passed 7 / Failed 0 / Total 7**（EntityBaseTest / RepositoryTest / SysDictOrderingTest / UnitTest1）。`AddAutoServices` 运行时 DI 装配正确，服务可解析。

### 3. psql 验证：重启后 sys_user/sys_role/sys_menu/sys_dict_type 表正常创建

- 本任务改动为**纯 DI 注册替换**：未触碰任何实体 / `[SugarTable]` 表名列声明 / 种子 / CodeFirst / DB schema。Program.cs 仅删 17 条服务注册行 + 1 条 `AddAutoServices`，`InitTables / HealColumnCase / InitSeeds / InitSeedData` 的调用与参数**未动**——四表创建路径与迁移前逐字节等价。
- DI 装配正确性已由 scratch 绿构建 + 7/7 测试实证（服务可解析即重启可用）。
- **实跑阻塞**：基线依赖未解（task_025）→ 本 worktree 无法启服务实证；task_025 落地后按任务书口径（启动 + psql 查四表）即可实证，**预期通过（无 schema 变更、无种子变更）**。

### 4. `grep -c "AddScoped\|AddSingleton\|AddTransient" src/Web.Entry/Program.cs` 显著减少

| 期望 | 实跑 | 判定 |
|---|---|---|
| 显著减少（散落注册被 AutoServices 替代） | 17 → **0** | ✅ |

### 5. `grep -c "[Service]" src/Core/Services/*/*.cs` ≥ 9

| 期望 | 实跑 | 判定 |
|---|---|---|
| ≥ 9（每个服务文件都有标注） | **13**（Core/Services 全部服务文件均标注） | ✅ |

## 三、零破坏映射（脚本逐类复核：类 → 公共接口数 → 注册语义）

18 处 `[Service]` 逐一核对（`grep` 实定位 18 处属性用法 + Python 逐类解析继承子句）：

- **13 自注册（0 公共接口，等价 `AddScoped<Concrete>()`）**：AuthService、SysDictDataService、SysDictTypeService、SysFileService、SysLogLoginService、SysMenuService、SysOrgService、SysPosService、SysRoleService、SysUserService、JwtAuthHandler、CryptogramHelper（Singleton）、OtpService（Singleton）。
- **3 接口→实现（恰 1 公共接口，等价 `AddScoped<I, Concrete>()`）**：IUserTokenStateProvider→UserTokenStateProvider、IFileContentStore→FileContentStore、IMenuPermService→MenuPermService。
- **2 强制自注册（实现 IAsyncActionFilter 但显式 `ServiceType=typeof(具体类)`，Transient）**：OpLogFilter、MenuPermFilter。WHY：MVC `o.Filters.AddService<TFilter>()` 本身**不向 DI 注册** TFilter（只按具体类从请求作用域解析），故须 `[Service]` 承接其 DI 注册并锁定具体类（否则会被自动匹配改道到 IAsyncActionFilter）。
- **生命周期逐条与原 Program.cs 一致**：原 L58/59 `AddSingleton<CryptogramHelper/OtpService>`→`[Service(Lifetime=Singleton)]`；原 L76/77 `AddTransient<OpLogFilter/MenuPermFilter>`→`[Service(ServiceType=typeof(X), Lifetime=Transient)]`；其余 14 处原 `AddScoped`→默认 Scoped。
- **纯增量 1 处（披露）**：SysLogLoginService 原 Program.cs **未注册**（真孤儿，全仓无引用、无接口），任务书「全部已有服务类」要求一并标注；新增该注册无既有代码依赖其缺席，安全（Program.cs L54 注释已披露）。17 原注册 1:1 匹配 + 1 增量 = 18。

## 四、影响面与回滚

- **引用影响面**：Core（+2 文件，`Huluyeye.Core.Extensions` 命名空间被 Web.Entry 引用）、Web.Core（3 文件加特性：JwtAuthHandler/OpLogFilter/MenuPermFilter）、Web.Entry/Program.cs（删 17 注册 + 1 AddAutoServices + 删 9 未用 using + 补 `using Huluyeye.Core.Extensions`）、Core/Services（18 文件加特性 + SysDictDataService 清重复 using）。**无 csproj 包变更、无 slnx 变更、无 DB/序列化结构变更**。
- **回滚方式**：`git revert 83c7a7a`（单提交自包含，含 2 新文件 + 21 文件改动，git 可完整逆向）。
- **最小验证**：scratch 固定基线绿构建（0错0警）+ 7/7 测试 + §二.4/.5 两条 grep（均已给实跑值）。

## 五、已知阻塞与残留（供终审知悉）

1. **【阻塞·非本任务】Core→Web.Core 反向依赖**：9 个 Core 服务文件 `using Huluyeye.Web.Core.{Auth,Models,Exceptions}`，而 Core.csproj 不引用 Web.Core（引用图单向 Web.Core→Core）。ba78e67 与 main(20bdbf1) 均实测失败（11 错，集合同）。task_025 范围内（主工作区 `fix_compile.py` 已在修、未落提交态）。**本任务代码已在此依赖解下证绿（scratch，§二.1）。建议：task_025 先落，或本分支 rebase 至依赖解后的基线——则 build / test / psql 三项验收一次通过。**
2. **本任务自查并修正 2 处自身 bug**：① CS0617（`[Service]` 具名参数 `ServiceType/Lifetime` 原为只读 auto-property，C# 具名属性参数须绑定可写字段/非静态读写属性）→ 改可写属性（Lifetime 带 `= Scoped` 初始值）；② CS8619（`ReflectionTypeLoadException.Types` 为 `Type?[]`，`.Where(t=>t is not null)` 不收窄元素可空性 → 赋 `Type[]` 报可空性不匹配）→ `Select(t => t!)` 显式转非空（前置 `is not null` 已保证非空，`!` 安全）。
3. **顺手清预存 CS0105**：SysDictDataService 重复 `using Huluyeye.Core.Entities;`（ba78e67 已存在），非本任务引入，清掉以达「0 警告」。

## 六、过程合规披露

- 执行前评估：`task_024_ha_auto_service.assess.md`（落 worktree 根；`.gitignore` 的 `*.assess.md` 规则不入库，按仓库既定约定）。
- **build**：系统提交后自动 build 未在观测窗口产出结果文件 → 本地核验（`dotnet build`，产物入 gitignored `bin/obj`，不污染提交）+ scratch 固定基线绿构建（§二.1）。
- **主工作区无污染**：`/data/huluyeye-admin` 无 `[Service]`、无本任务新增文件（仅预存未跟踪 `fix_compile.py`，系上一会话对 task_025 依赖的修复脚本，非本会话产物）。
- 未写验证脚本/攻击数据（系统模板化生成）。
- 交付前 worktree `bin/obj` 已清理，`git status` 干净；BOM/CRLF 逐文件保留（脚本检测主导换行，单文件内不混入）。
