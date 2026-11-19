# task_005_ha_jwt 验收报告

**人格：开发（dev-a）** ｜ 评估结论：4 功能点（中×2 + 简×2，共 6 分）→ 直行（双门均未触发，详见 assess）
**提交号：`529e45f`**（分支 `task_task_005_ha_jwt`，worktree `/data/worktrees/admin/task_005_ha_jwt`；本报告随次提交入库）
**基线：`7fd4b0c`**（task_004 终态，含 task_002/003 全量；fast-forward 合入，见披露②）

## 一、交付物清单（同批提交 13 文件）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/Business/Entitys/SysUser.cs` | 新增 | `[SugarTable("sys_user","系统用户")][IncreTable]`；Account/Password/RealName（Length 64/256/64）+ `int Status = 0`；继承 EntityBaseId（雪花主键 + 审计 + 软删，披露③） |
| `src/Core/Security/Pbkdf2Helper.cs` | 新增 | `HashPassword`：16B 随机盐 + PBKDF2-HMAC-SHA256 10 万轮 32B → `"{saltBase64}${hashBase64}"`；`VerifyPassword`：`FixedTimeEquals` 恒定时间比对（防时序侧信道），畸形存储串（无 `$`/段数错/非 Base64）返回 false 不抛 |
| `src/Core/Security/JwtClaimNames.cs` | 新增 | Claim 名单点定义（UserId/Account/RealName）：签发（AuthService）/读取（AuthController）/审计（SqlSugarSetup AOP）三方共用，防字面量漂移 |
| `src/Core/SeedData/IncreSeedAttribute.cs` | 新增 | `[IncreSeed]` 种子标记特性（本任务首建种子机制） |
| `src/Core/SeedData/ISqlSugarSeed.cs` | 新增 | `Task SeedAsync(ISqlSugarClient)`；约定无参构造（Activator） |
| `src/Core/SeedData/SeedSetup.cs` | 新增 | `InitSeeds` 扩展：扫描 [IncreSeed]+ISqlSugarSeed、按类名排序（多任务并存时执行顺序可预期）、逐个执行 |
| `src/Business/SeedData/SysUserSeed.cs` | 新增 | admin/Admin@123 种子；`HashPassword` 种子时现算（不落明文/固定串）；幂等：`AnyAsync(Account=="admin")` 已存在则跳过 |
| `src/Business/Services/AuthService.cs` | 新增 | `LoginAsync` 契约顺序：空参 → 查账号（不存在与密码错误同口径"账号或密码错误"防账号枚举）→ 停用 → 密码校验 → 签发；`GenerateJwtToken`：HS256、8h 过期、Secret/Issuer/Audience 读 `Jwt:*` 配置，缺 Secret 快速失败 |
| `src/Business/Huluyeye.Business.csproj` | 改 | +`Microsoft.AspNetCore.Authentication.JwtBearer 10.0.11`（计划书"NuGet 仅 3 包"口径内，披露④） |
| `src/Web.Entry/Controllers/AuthController.cs` | 新增 | `POST /api/auth/login`（失败 401+error）/ `GET /api/auth/me`（[Authorize]，200+用户信息） |
| `src/Web.Entry/Program.cs` | 改 | `AddScoped<AuthService>` + `AddAuthentication().AddJwtBearer`（Issuer/Audience/签名密钥/Lifetime 四项校验）+ `AddAuthorization` + 管道 `UseRouting→UseAuthentication→UseAuthorization` + `InitTables(Business 程序集)` + `await InitSeeds(Business 程序集)`（top-level await） |
| `src/Web.Entry/appsettings.json` | 改 | +`Jwt` 段（Secret 开发占位，生产环境变量注入；Issuer/Audience 定值） |
| `src/Core/SqlSugarSetup.cs` | 改 | ①AOP 审计 Claim 名 → `JwtClaimNames` 单点（UserId/Account，披露⑧）；②补 `ISqlSugarRepository<>` 接口注册（披露⑥） |

## 二、验收标准逐条证据

### 1) `dotnet build src/huluyeye-admin.slnx` → 0 警告 0 错误 ✅（本地实测，自动 build 文件未落盘见披露⑩）

```
  Yitter.IdGenerator -> ...\thirdparty\YitIdHelper\bin\Debug\netstandard2.0\Yitter.IdGenerator.dll
  Huluyeye.Core -> ...\src\Core\bin\Debug\net10.0\Huluyeye.Core.dll
  Huluyeye.Web.Core -> ...\src\Web.Core\bin\Debug\net10.0\Huluyeye.Web.Core.dll
  Huluyeye.Business -> ...\src\Business\bin\Debug\net10.0\Huluyeye.Business.dll
  Huluyeye.Test -> ...\src\Test\bin\Debug\net10.0\Huluyeye.Test.dll
  Huluyeye.Web.Entry -> ...\src\Web.Entry\bin\Debug\net10.0\Huluyeye.Web.Entry.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

（与提交 `529e45f` 同代码，提交前最后一次全量 build；提交后系统自动 build 结果文件 `/data/tasks/build/task_005_ha_jwt.txt` 未落盘，同 task_004 现象。）

### 2) `dotnet run --project src/Web.Entry` 后 4 条 curl 实测 ✅（完整输出）

启动日志（首启，CodeFirst 建 sys_user 表 + 种子执行）：

```
info: Huluyeye.Web.Entry[0]
      初始化表完成(扫描 [SugarTable] 实体,CodeFirst 增量建表)
info: Huluyeye.Web.Entry[0]
      种子执行完成(扫描 [IncreSeed] 种子类)
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5132
```

```
===== 1) 错误密码 → 期望 401 =====
HTTP_STATUS=401
--- 响应体:
{"error":"账号或密码错误"}

===== 2) 正确密码 → 期望 200 + token =====
HTTP_STATUS=200
--- 响应体:
{"token":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJVc2VySWQiOiI4NDE5MTU4NDQ0MTU1NTciLCJBY2NvdW50IjoiYWRtaW4iLCJSZWFsTmFtZSI6Iuezu-e7n-euoeeQhuWRmCIsIm5iZiI6MTc4NzcxMTEzMSwiZXhwIjoxNzg3NzM5OTMxLCJpc3MiOiJodWx1eWV5ZS1hZG1pbiIsImF1ZCI6Imh1bHV5ZXllLWFkbWluLWFwaSJ9.MCbQRpFVcXDecqrUf5GVpL7UpX62vX1-SWQhZz0TwJs"}

===== 3) 带 token 访问 /me → 期望 200 + 用户信息 =====
HTTP_STATUS=200
--- 响应体:
{"userId":"841915844415557","account":"admin","realName":"系统管理员"}

===== 4) 无 token 访问 /me → 期望 401 =====
HTTP_STATUS=401
--- 响应体:（空）
```

token 载荷解码核验：claims = UserId/Account/RealName（与契约一致）；`exp − nbf = 1787739931 − 1787711131 = 28800s = 8h`（契约 8 小时）；`iss = huluyeye-admin`、`aud = huluyeye-admin-api`（与 appsettings Jwt 段一致）。

附加实测（超出契约的回归确认）：
- **二次启动**（重启应用）：建表/种子日志照常、无异常 → 种子幂等实跑通过；重启后登录仍 200 → 数据持久化。
- **存量单测回归**：DI 注册改动后 `dotnet test src/Test` 复跑：

```
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 2 s - Huluyeye.Test.dll (net10.0)
```

### 3) git commit ✅

`529e45f`（代码 13 文件；git add 限定交付物路径，assess 按公约不入库）。

## 三、设计口径与实证

1. **JWT 签发路径**：IdentityModel 8.19.2（JwtBearer 10.0.11 传递依赖）的 `JsonWebTokenHandler` **没有** `WriteToken(SecurityTokenDescriptor)` 重载（XML 文档 API 面实证，只有 `CreateToken(descriptor)` + `WriteToken(SecurityToken)`）→ 采用经典 `JwtSecurityTokenHandler` + `JwtSecurityToken` 构造（`System.IdentityModel.Tokens.Jwt` 同为 JwtBearer 传递依赖，**零新增包**）；签发/校验两端兼容（JwtBearer 管线实测校验通过即证明）。
2. **PBKDF2 API**：任务书口径的 `Rfc2898DeriveBytes` 构造形式在 net10 已标过时（SYSLIB0060，会破坏 0 警告验收）→ 改官方推荐静态方法 `Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, SHA256, 32)`，算法参数与契约完全一致（PBKDF2-HMAC-SHA256/10 万轮/32 字节/16 字节盐）。
3. **登录错误口径**：账号不存在与密码错误返回同一文案（防攻击者借错误差异枚举有效账号）；停用单独提示（账号确存在，泄漏停用状态无额外风险）。
4. **种子机制**：`[IncreSeed]` + `ISqlSugarSeed` + `InitSeeds` 扫描扩展（按类名排序保证多任务并存时顺序可预期）；种子在 `InitTables` 之后执行（表先于数据）；admin 行由 AOP 自动填雪花 Id/CreateTime。
5. **DI 注册**：仓储接口与具体类型双注册（业务服务注入接口，Test 项目按具体类型取，两者实测共存）；`AuthService` Scoped。

## 四、披露（待裁决项）

1. **参考文档缺失**：任务书指定必读 `docs/baseadmin设计精华参考.md` 在仓库与本机均不存在（git log --all + 全盘 find 确认，与 task_004 同样披露）→ 按任务书契约段 + 计划书实现。请葫芦爷爷确认文档落库位置。
2. **依赖合入**：本分支 fast-forward 合入 task_004 终态 `7fd4b0c`（dep_on 产物在分支不在 main）；PM 合并本分支时 task_002/003/004 产物随之入 main，请核对与各任务验收基线一致。
3. **实体属性 `string` → `string?`**：契约非可空 `string` 属性在 nullable enable 下触发 CS8618（与"0 警告"验收标准冲突）→ 按项目实体惯例（EntityBase/SysConfig 全 `string?`）改可空，语义不变（库列本身可空，业务校验在 AuthService/前端层）。
4. **JwtBearer 包落 Business**：契约把 JWT 签发（AuthService）放 `Business/Services`，故 `JwtBearer 10.0.11` 引用落 `Huluyeye.Business.csproj`，Web.Entry 经项目引用传递获得（校验端 `AddJwtBearer` 扩展方法可用），不重复引用；仍在计划书"仅 3 个 NuGet"口径内。
5. **运行实测连接串**：appsettings 的 `172.18.0.3:5432` 本沙箱不可达（task_003/004 同现象），实测用环境变量覆盖 `ConnectionStrings__Default=Host=postgres;...`（凭据与 appsettings 相同，此处脱敏；`postgres` → 172.19.0.10:5432 实测可达）。**提交值未改**。
6. **跨任务修正（任务契约外）**：task_004 的 `SqlSugarSetup` 仓储注册只有具体类型 `SqlSugarRepository<>`，Web 宿主侧注入 `ISqlSugarRepository<>` 接口在 DI 启动验证直接失败（实测 Unhandled exception）→ 本次补 `AddScoped(typeof(ISqlSugarRepository<>), typeof(SqlSugarRepository<>))`（一行，Test 项目用具体类型不受影响，6/6 复跑通过）。此改动属 task_004 交付物文件的缺陷补齐，**待 PM/验收官知悉**。
7. **种子软删边界**：幂等判断 `AnyAsync(Account=="admin")` 走软删全局过滤器——admin 行被软删后重跑种子会再插一行（双行并存）。M1 无用户软删入口，风险不成立；用户管理任务落地时补唯一约束收口。
8. **审计 Claim 定稿**：AOP 审计用户名字段读 `Account`（账号唯一不可变，作审计身份；姓名可重复/可变更不作身份）。task_003 预留的 `UserName` 口径在本任务定稿为 `Account`，历史数据零影响（此前该列恒空）。
9. **assess 文件**：`task_005_ha_jwt.md.assess.md` 在 worktree 根目录、**未入库**（/data/tasks 本会话只读挂载，与 task_004 同处理）。
10. **自动 build 未落盘**：提交后系统自动 build 结果文件 `/data/tasks/build/task_005_ha_jwt.txt` 未生成（`/data/tasks/build/` 持续为空，task_002/003/004 亦无产物）→ 机制疑似未生效，**待 PM 核实**；本地已实测同代码 0/0。

## 五、交付最小清单

- **限域**：新增 `src/Core/Security`、`src/Core/SeedData`、`src/Business/Entitys`、`src/Business/SeedData`、`src/Business/Services` 五目录 9 文件 + 4 处既有文件修改（Business.csproj 包引用 / Program.cs 装配 / appsettings Jwt 段 / SqlSugarSetup 两处）；零改动 Web.Core/Test/thirdparty。
- **引用影响面**：`SqlSugarSetup` 消费方（Web.Entry、Test）仅多一个 Scoped 接口注册与 Claim 名换源（值同源同义，历史审计列恒空零影响）；`AuthService`/`AuthController`/种子均为新增；`appsettings` 只增 `Jwt` 段。
- **回滚方式**：`git revert 529e45f`（单提交 13 文件，无跨提交纠缠；revert 后 sys_user 表与 admin 行保留于库中，无害）。
- **最小验证**：`dotnet build src/huluyeye-admin.slnx`（0/0）+ 启动应用后任务书 4 条 curl（401/200+token/200/401，需 PG 可达）。
