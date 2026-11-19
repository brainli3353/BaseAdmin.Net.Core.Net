# 验收报告 task_010_ha_config（配置加载与参考源对齐：Business/Configuration 模块化 + 三环境覆盖）

- 分支：`task_task_010_ha_config`
- 代码提交：**7b29db2**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-27
- 实跑环境：开发沙箱容器（worktree 内 `dotnet run --no-build`；沙箱网络不可达 172.18.x 基础设施，披露见「限制与披露」）

## 结论速览

| # | 验收项（任务书 §验收标准） | 结果 | 证据 |
|---|---|---|---|
| 1 | `dotnet build src/huluyeye-admin.slnx` 0 警告 0 错误 | **PASS** | 实跑 0W/0E（15.0s），见「build/回归」节 |
| 2 | `ASPNETCORE_ENVIRONMENT=Development dotnet run` 启动日志显示开发配置（连 dev 库 huluyeye_admin） | **PASS** | 配置自检日志：`环境=Development … 主库=…Database=huluyeye_admin…`，见「三环境实测」节 |
| 3 | `ASPNETCORE_ENVIRONMENT=Staging dotnet run` 启动日志显示测试配置（连 test 库 huluyeye_admin_test） | **PASS** | 配置自检日志：`环境=Staging … 主库=…Database=huluyeye_admin_test…` |
| 4 | `ASPNETCORE_ENVIRONMENT=Production dotnet run` 启动日志显示正式配置（连接 prod 库——不实连，日志确认加载路径） | **PASS** | 配置自检日志：`环境=Production … 主库=…Database=huluyeye_admin_prod（占位）…`；随后 缓存预热完成，仅在连接占位库时失败（不实连，符合口径） |
| 5 | git commit + 验收报告附实测输出 | **PASS** | 7b29db2 + 本报告 |

**5 项验收口径全部命中。** 另加验一项任务书未列但影响验收命令可达性的缺陷修复：launchSettings 环境覆盖（实测会挡三环境验收命令），见「关键设计决策②」。

## 交付范围

1. **`src/Business/Configuration/` 20 文件**（5 模块 × 基础+三环境）：
   - `App.json` + `App.{Development|Staging|Production}.json`（应用名 App:Name 新读取点 + 雪花 WorkerId 系统配置）
   - `Database.json` + 三环境覆盖（ConnectionConfigs 三库 dev/test/prod + 存量回落 ConnectionStrings:Default + DbSettings）
   - `JWT.json` + 三环境覆盖（Issuer/Audience 基础共享；Secret 按环境——dev/staging 占位密钥互不相同，prod 占位+环境变量注入注释）
   - `Cache.json` + 三环境覆盖（Cache:Type + ConnectionStrings:Redis 按环境）
   - `Logging.json` + 三环境覆盖（日志级别）
2. **`src/Web.Entry/Program.cs` 配置加载改造**：
   - `LoadModuleConfiguration`：每模块 `{Module}.json` + `{Module}.{Environment}.json`，AddJsonFile（optional + reloadOnChange），basePath 双路径定位（源树 `../Business/Configuration` 优先 → 发布输出目录 `Configuration/` 回落）
   - 启动配置自检日志（敏感值脱敏）：环境/配置目录/应用名/主库/缓存实现/Redis——位于一切 I/O 之前
   - 裸启动缺省 Development 回落（.NET 8+ 宿主缺省 Production 会直连生产占位配置）
3. **`src/Web.Entry/appsettings.json` 瘦身**：环境相关配置全部迁出，仅留环境无关补充（Otp/Cryptogram/AllowedHosts）；删 `appsettings.Development.json`（Logging 迁入模块）
4. **配套**：`Huluyeye.Web.Entry.csproj` Content 复制项（发布布局）、`launchSettings.json` 去环境覆盖、`CacheSetup.DescribeEffectiveImpl`（自检日志与 AddCache 同判定口径）、测试注释 2 处配置源指向更新

## 配置迁移清单（键名与读取点兼容——代码读取点零改动，纯文件位迁移+加载链）

| 配置键（读取点不变） | 旧位置 | 新位置 |
|---|---|---|
| `ConnectionConfigs`（多库，main 必备/log 可选） | Web.Entry/appsettings.json | Database.json（基础留空）+ Database.{Env}.json（三库） |
| `ConnectionStrings:Default`（存量回落） | 同上 | 同上 |
| `DbSettings:EnableIncreTable` | 同上 | Database.json |
| `Jwt:Secret` / `Jwt:Issuer` / `Jwt:Audience` | 同上 | JWT.json（Issuer/Audience）+ JWT.{Env}.json（Secret） |
| `Cache:Type` | 同上 | Cache.json |
| `ConnectionStrings:Redis` | 同上 | Cache.json（基础留空）+ Cache.{Env}.json |
| `Logging:LogLevel` | 同上 + appsettings.Development.json | Logging.json + Logging.{Env}.json |
| `Yit:WorkerId` | 同上 | App.json + App.{Env}.json |
| `App:Name`（**新增**，启动自检日志读取） | — | App.json |
| `Otp:Issuer` / `Cryptogram:Key` / `AllowedHosts` | 同上 | **保留** Web.Entry/appsettings.json（环境无关补充） |

读取点核对（grep 全量）：SqlSugarSetup（ConnectionConfigs/ConnectionStrings:Default/Yit/DbSettings）、Program.cs + AuthService（Jwt 三键/Otp:Issuer）、CacheSetup（Cache:Type/ConnectionStrings:Redis）、CryptogramHelper（Cryptogram:Key）——键名全部未变，无需同步改读取点。

## build / 回归

- 实跑（dev 沙箱，任务书验收命令原样）：`cd /data/worktrees/admin/task_010_ha_config && dotnet build src/huluyeye-admin.slnx`
  ```
  Build succeeded.
      0 Warning(s)
      0 Error(s)
  Time Elapsed 00:00:15.02
  ```
- build 自动验证：提交 7b29db2 后系统约定自动跑 build 写 `/data/tasks/build/task_010_ha_config.txt`；写报告时（提交后 ~12 分钟）该文件尚未生成，本报告以 dev 实跑同口径命令输出为准（与任务_008 披露③同口径）。若自动验证文件后续生成且结果不一致，以文件为准并打回。
- `dotnet test`：本任务未改测试代码逻辑（仅 2 处注释指向更新），测试项目编译通过（含于 build 0/0）；DB 用例依赖 PG 可达性，沙箱内不可达（既有降级机制），回归以验收环境为准。

## 三环境实测（启动证据）

命令基线：`cd src/Web.Entry && ASPNETCORE_ENVIRONMENT=<Env> dotnet run`（--no-build，已 build 通过；验收脚本跑完整 `dotnet run` 等价）。

### ① Development（任务书验收项 2）

```
配置自检: 环境=Development 配置目录=/data/worktrees/admin/task_010_ha_config/src/Web.Entry/../Business/Configuration 应用=huluyeye-admin 主库=Host=172.18.0.3;Port=5432;Database=huluyeye_admin;Username=huluyeye;password=*** 缓存=Redis Redis=172.18.0.2:6379
```

- 主库 = **dev 库 huluyeye_admin**（验收口径命中）；配置目录 = 源树 Business/Configuration（basePath 定位正确）；密码脱敏为 `***`（安全红线）；缓存 Redis 地址生效。

### ② Staging（任务书验收项 3）

```
配置自检: 环境=Staging 配置目录=/data/worktrees/admin/task_010_ha_config/src/Web.Entry/../Business/Configuration 应用=huluyeye-admin 主库=Host=172.18.0.3;Port=5432;Database=huluyeye_admin_test;Username=huluyeye;password=*** 缓存=Redis Redis=172.18.0.2:6379
```

- 主库 = **test 库 huluyeye_admin_test**（验收口径命中，docs/环境管理.md：验收/盲审连 test 库不污染 dev 库）。

### ③ Production（任务书验收项 4）

```
配置自检: 环境=Production 配置目录=/data/worktrees/admin/task_010_ha_config/src/Web.Entry/../Business/Configuration 应用=huluyeye-admin 主库=Host=CHANGE_ME;Port=5432;Database=huluyeye_admin_prod;Username=CHANGE_ME;password=*** 缓存=Redis 已配置但连接串缺失 → 回退 Memory(需注入 ConnectionStrings:Redis) Redis=(未配置)
      缓存预热完成
fail: SqlSugar[0]
      SqlSugar.SqlSugarException: 中文提示 :  连接数据库过程中发生错误，检查服务器是否正常连接字符串是否正确，错误信息：Resource temporarily unavailableDbType="PostgreSQL";ConfigId="main".
```

- 主库 = **prod 库 huluyeye_admin_prod 占位**（加载路径确认，验收口径命中）；**不实连**：占位 Host=CHANGE_ME 不可达，启动恰停在连接占位库处——且此前已完成缓存预热（生产 Redis 占位为空 → Memory 回退，日志显式可见，防「以为是 Redis 实际全在内存」的静默降级）。
- 生产密钥不落库（红线）：prod 三敏感值（DB 密码/JWT Secret/Redis）均为占位 + 环境变量注入键名注释（`ConnectionConfigs__0__ConnectionString` / `Jwt__Secret` / `ConnectionStrings__Redis`），环境变量在配置链优先级最高。

### ④ 加验：裸 `dotnet run`（未设 ASPNETCORE_ENVIRONMENT）

```
配置自检: 环境=Development … 主库=…Database=huluyeye_admin…（回落生效，开发工作流不变）
```

## 关键设计决策（含实证探针）

1. **覆盖方向与数组合并实证**：临时探针项目（.NET 10.0）实测配置链「基础文件先、环境覆盖后」：同键标量 → 后加载源胜；数组节按索引合并且同索引后加载源胜；仅基础存在的键保留。→ 基础文件可安全持共享值/留空占位（如 Database.json `ConnectionConfigs:[]`、Cache.json `Redis:""`），环境文件定实际值。
2. **launchSettings 陷阱（缺陷修复）**：实测 launchSettings `environmentVariables` **覆盖 shell 环境变量**（`ASPNETCORE_ENVIRONMENT=Staging dotnet run` 实测仍跑 Development）→ 验收命令会被挡。修复：launchSettings 两 profile 移除 `ASPNETCORE_ENVIRONMENT`；Program.cs 顶部补「未显式指定 → 缺省 Development」回落（.NET 8+ 宿主缺省 Production，裸启动会直连生产占位配置）。IDE 启动行为等效（回落同为 Development）。
3. **basePath 双路径**：dev（`dotnet run`，content root=Web.Entry 项目目录）优先源树 `../Business/Configuration`（与 Business 为兄弟目录，源树编辑 + reloadOnChange 即时生效）；发布布局回落 `AppContext.BaseDirectory/Configuration`（csproj Content 复制，20/20 文件已验证落入输出目录）；两者皆无 → 启动即抛 InvalidOperationException（快速失败，不静默缺配置）。
4. **脱敏**：连接串 `Password=` / `password=` / URI `:user:pass@` 段 → `***`，host/库名保留（排查可见）；三环境实测日志均验证。
5. **JWT 占位密钥按环境区分**：dev/staging 密钥不同 → 跨环境 token 互失效，环境切换可观测（盲审可直接验证）；prod 占位 `CHANGE_ME__injected_via_Jwt__Secret`（≥32 字节防空键异常）+ 注入注释。
6. **Cache 基础留空语义**：ConnectionStrings:Redis 缺失 → CacheSetup 回退 Memory（既有机制），生产占位即此路径，自检日志显式声明回退（`DescribeEffectiveImpl` 与 AddCache 同一判定口径）。

## 三查（改代码后影响面）

- **前端/接口契约**：无 API/契约变化（配置键名零改动，仅文件位迁移 + 加载链）。
- **共享路径（基类/枚举/读取点）**：SqlSugarSetup/CacheSetup/AuthService/CryptogramHelper/Program.cs 读取点全部未变（键名兼容，任务书约束「配置键名与现有读取点兼容」取兼容侧，未同步改读取点）；Test 项目自建内存配置（连接串硬编码常量，不受文件迁移影响），仅 2 处注释的配置源指向更新。
- **历史数据**：无 DB schema/序列化变化；存量库数据零影响。

## 回滚方式

`git revert 7b29db2`（单提交，干净回滚）：appsettings.json 恢复原键（读取点始终兼容旧布局）、Business/Configuration 移除、Program.cs 加载块/自检/回落移除、launchSettings/csproj 复原。无数据迁移、无残留依赖。

## 限制与披露

1. **沙箱网络隔离**：本会话沙箱容器不可达 172.18.0.3（PG）/172.18.0.2（Redis）（TCP 探测多次重试均失败，无本地替代实例），故三环境实测的**完整启动路径**（CodeFirst 建表/种子/Kestrel 监听）未能在本会话跑完；配置自检日志位于一切 I/O 之前，恰是任务书验收口径的观测点（「启动日志显示开发配置（如连接 dev 库 huluyeye_admin）」）。docs/环境管理.md 确认 dev/test 库 2026-08-26 已建可达，验收环境重跑同命令即得完整启动（Production 按设计停在自检日志后的占位库连接处）。
2. **build 自动验证文件**：见「build/回归」节说明（写报告时未生成，以 dev 实跑同口径命令为准）。
3. **prod 连接占位**：部署机未定（docs/环境管理.md），Database.Production.json 的 Host/Username/Password 均为 CHANGE_ME 占位，库名按 dev/test 命名规律取 `huluyeye_admin_prod`；上线前按文件内注释经环境变量注入。
4. **test 库凭证**：Database.Staging.json 复用 dev 实例的 huluyeye 账号（同 PG 实例 172.18.0.3，docs/环境管理.md 未另设测试账号）；如测试库后续独立账号，仅改该文件。
