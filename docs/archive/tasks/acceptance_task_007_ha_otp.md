# task_007_ha_otp M2a 验收报告

**人格：开发（dev-a）** ｜ 评估结论：7 功能点（中×5 + 简×2，双门均未触发）→ 直行（详见 `task_007_ha_otp.md.assess.md`，按纪律不入库）
**提交号：`a0f0189`**（分支 `task_task_007_ha_otp`，worktree `/data/worktrees/admin/task_007_ha_otp`；基线 `babea69`；本报告随次提交入库）

## 一、交付物清单（同批提交 26 文件：16 新增 / 9 修改 / 1 删除）

| 文件 | 动作 | 内容 |
|---|---|---|
| `src/Core/Cache/ICacheService.cs` | 新增 | 缓存抽象：`SetAsync`（含 TTL 重载）/`GetAsync<T>`/`GetOrNullAsync<T>`/`RemoveAsync`/`IncrementAsync`/`KeyTimeToLiveAsync` |
| `src/Core/Cache/RedisCacheService.cs` | 新增 | SE.Redis 3.x 实现：`StringSetAsync(key,val,exp,When.Always)`、`KeyTimeToLiveAsync` 返回 `TimeSpan?`（无 TTL → null → 0）；连接失败按调用抛出（启动预热兜底，见 Program） |
| `src/Core/Cache/MemoryCacheService.cs` | 新增 | 内存回退实现（`Cache:Type=Memory` 或 Redis 未配置时），同语义 |
| `src/Core/Cache/CacheSetup.cs` | 新增 | `AddCache`：按 `Cache:Type` 选型注册单例 |
| `src/Core/Security/CryptogramHelper.cs` | 新增 | AES-256-CBC/PKCS7：`Encrypt`/`Decrypt` = `Base64(随机16B IV ‖ 密文)`；`Cryptogram:Key` 读 Base64 必须 32 字节，**启动 fail-fast**（`InvalidOperationException`） |
| `src/Core/Security/OtpService.cs` | 新增 | `GenerateSetupSecret()`（20B 随机 → Base32）；`Validate(code,secret,lastUsedTimeStep)` → 命中 step 或 null：Otp.NET `Totp(Sha1,6)` + `VerificationWindow(1,1)` + **防重放**（命中 step ≤ `lastUsedTimeStep` → null） |
| `src/Core/Security/JwtClaimNames.cs` | 改 | +`AccountType`/`TokenVersion` 常量（签发/校验/审计单点防漂移） |
| `src/Core/Huluyeye.Core.csproj` | 改 | +`StackExchange.Redis 3.1.31`（本任务唯一新 NuGet）+ Otp.NET 工程引用（YitIdHelper 先例） |
| `src/Business/Entitys/SysUser.cs` | 改 | +`AccountType`（int，默认 777）+`TokenVersion`（int，默认 1）；**Why**：`[IncreTable]` 增量加列以 DefaultValue 回填存量行，老数据免迁移脚本 |
| `src/Business/Entitys/SysUserOtp.cs` | 新增 | `[SugarTable("sys_user_otp")]`：UserId/SecretCipher(256)/LastUsedTimeStep(nullable)/Enabled/BindTime(nullable) |
| `src/Business/Services/Auth/Dto/*.cs` | 新增 | `LoginInput`/`LoginOtpInput`/`LoginOutput` record；ChallengeId 用 string（防 JS 53 位精度丢失） |
| `src/Business/Services/Auth/LoginOtpChallenge.cs` | 新增 | 挑战缓存载荷 record（userId/account/setupSecret 明文/bindRequired/failTimes）——契约：300s 内明文存 Redis（一次性，过期即焚） |
| `src/Business/Services/Auth/AuthService.cs` | 新增 | 两阶段核心：一阶段（锁定检查→密码→挑战下发）；二阶段（挑战消费→OTP 校验→首次成功绑定→JWT 签发→清错误计数→写版本键→一次性删挑战）；登出（读余活写入黑名单） |
| `src/Business/Services/Auth/UserTokenStateProvider.cs` | 新增 | `IUserTokenStateProvider` 实现（Business 侧）：查 SysUser → `(TokenVersion, Status!=0)` |
| `src/Business/Services/AuthService.cs` | 删除 | 005 扁平旧文件（单阶段登录），由 `Services/Auth/AuthService.cs` 取代 |
| `src/Web.Core/Auth/UserTokenState.cs` | 新增 | `record UserTokenState(int TokenVersion, bool IsDisabled)` |
| `src/Web.Core/Auth/IUserTokenStateProvider.cs` | 新增 | 依赖倒置：Web.Core 不得引用 Business（层序 Core←Web.Core←Business），JWT 校验期 DB 回退由此抽象 |
| `src/Web.Core/Auth/JwtAuthHandler.cs` | 新增 | `OnTokenValidated` 逻辑：①UserId claim 解析 ②黑名单 `sys_token_blacklist:{raw}` ③TokenVersion claim ④缓存版本键（无 → DB 回退 + 回填）⑤版本不一致 → `context.Fail` |
| `src/Web.Core/Huluyeye.Web.Core.csproj` | 改 | +`Microsoft.AspNetCore.Authentication.JwtBearer 10.0.11`（`TokenValidatedContext` 类型所需，版本与 Business 锁一致） |
| `src/Web.Entry/Controllers/AuthController.cs` | 重写 | `POST /api/auth/login`（401+`ApiResult.Fail` / 200+`ApiResult<LoginOutput>.Success`）；`POST /api/auth/login/otp`（同上口径）；`GET /api/auth/me`（[Authorize]，`ApiResult<object>`）；`POST /api/auth/logout`（[Authorize]） |
| `src/Web.Entry/Program.cs` | 改 | `AddCache` + 缓存预热（Set/Remove `__warmup__`，Redis 断连启动即失败不裸奔）+ `AddScoped<AuthService>`/`AddSingleton<CryptogramHelper,OtpService>`/`AddScoped<IUserTokenStateProvider>`/`AddScoped<JwtAuthHandler>` + `JwtBearerEvents.OnTokenValidated` 接线；`ICacheService` 别名消解 SqlSugar 同名类型冲突 |
| `src/Web.Entry/appsettings.json` | 改 | +`ConnectionStrings:Redis`、`Cache:Type=Redis`、`Otp:Issuer`、`Cryptogram:Key`（32B Base64 开发占位，生产环境变量注入，与 `Jwt:Secret` 同口径） |
| `src/huluyeye-admin.slnx` | 改 | +Otp.NET Folder/Project（YitIdHelper 之后） |
| `thirdparty/Otp.NET/Otp.NET.csproj` | 改 | `GeneratePackageOnBuild=False`（**Why**：pack 需 icon.png/README.md/LICENSE.txt 三资源，thirdparty 与仓库根均缺失，默认 true 会破坏 0 警告构建） |

## 二、验收标准逐条证据

### 1) 构建 0 警告 0 错误 ✅（本地全量 build + 提交后 `dotnet run` 二次编译通过）

提交前最后一次全量 build（`dotnet build src/huluyeye-admin.slnx`）：

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`Cryptogram:Key` 占位修复（披露①）发生在该 build 之后、为纯配置变更；提交前 `dotnet run` 两次成功编译并启动（Web.Entry + 全部依赖工程），等价编译验证通过。

### 2) 回归测试 ✅ `dotnet test src/Test/Huluyeye.Test.csproj` → **Passed: 6, Failed: 0**（005/006 既有 6 条全绿，未新增用例——本任务逻辑由实机 curl 覆盖，见下）

### 3) 实机验收（`dotnet run --project src/Web.Entry`，端口 5132，admin/Admin@123，完整输出）

启动日志（增量建表 + 预热）：

```
info: Huluyeye.Web.Entry[0]  缓存预热完成
info: Huluyeye.Web.Entry[0]  初始化表完成(扫描 [SugarTable] 实体,CodeFirst 增量建表)
info: Huluyeye.Web.Entry[0]  种子执行完成(扫描 [IncreSeed] 种子类)
info: Microsoft.Hosting.Lifetime[14]  Now listening on: http://localhost:5132
```

**① 错误密码 ×5 → 401 + ApiResult 失败体**

```
{"code":1,"msg":"账号或密码错误"} [HTTP 401]   （×5）
```

**② 第 6 次 → 锁定提示（错误计数先于密码校验）**

```
{"code":1,"msg":"登录失败次数过多,请 30 分钟后重试"} [HTTP 401]
redis-cli GET sys_password_error_times:admin → 5
redis-cli TTL  sys_password_error_times:admin → 1799   （30min 窗 ✓）
```

（锁定生效即阻塞后续登录——契约语义。验收流程中 `DEL` 计数键继续，见披露②）

**③ 正确密码 → 一阶段挑战（未绑定态）**

```
{"code":0,"msg":"操作成功","data":{"challengeId":"841984389443653","bindRequired":true,
 "otpauthUri":"otpauth://totp/huluyeye-admin%3Aadmin?secret=KWBDFI...&issuer=huluyeye-admin&digits=6&period=30",
 "secret":"KWBDFIGGHJ764NXFKBI3N7LPJWTCWJEK"}} [HTTP 200]
```

**④ 挑战键 TTL=300（同秒取数，排除耗时干扰）**

```
TTL sys_login_otp_challenge:841986264625221 → 300
GET 同一键 → {"userId":841915844415557,"account":"admin","setupSecret":"VANYBR...","bindRequired":true,"failTimes":0}
```

**⑤ 错误 OTP → 401 + FailTimes 自增**

```
{"code":1,"msg":"校验码验证失败"} [HTTP 401]
GET 挑战键 → "failTimes":1
```

**⑥ 正确 OTP（python 独立实现 RFC6238 取码，窗口居中）→ 200 + JWT**

```
{"code":0,"msg":"操作成功","data":"eyJhbGciOiJIUzI1NiIs...（HS256，claims: UserId/Account/RealName/AccountType=777/TokenVersion=1，nbf→exp 8h）"} [HTTP 200]
```

**⑦ 重发已消费挑战 → 401 过期（一次性语义）**

```
{"code":1,"msg":"登录验证已过期,请重新登录"} [HTTP 401]
```

**⑧ 重新登录（已绑定态）→ bindRequired=false，无 otpauthUri/secret**

```
{"code":0,"msg":"操作成功","data":{"challengeId":"841989092274245","bindRequired":false,"otpauthUri":null,"secret":null}} [HTTP 200]
```

**⑨ 同 30s 窗口同码重放 → 401（LastUsedTimeStep 防重放生效，语义为"验证失败"而非"过期"）**

```
{"code":1,"msg":"校验码验证失败"} [HTTP 401]
```

**⑩ 下一窗口新码 → 200 + 新 token**

```
{"code":0,"msg":"操作成功","data":"eyJ...（TokenVersion=1，exp 顺延 8h）"} [HTTP 200]
```

**⑪ `/me` 带 token → 200 + ApiResult 统一格式（006 迁移）**

```
{"code":0,"msg":"操作成功","data":{"userId":"841915844415557","account":"admin","realName":"系统管理员"}} [HTTP 200]
GET /api/auth/me（无 token）→ [HTTP 401]
```

**⑫ 登出 → 200 + 黑名单键写入（TTL=剩余寿命）**

```
{"code":0,"msg":"操作成功"} [HTTP 200]
EXISTS sys_token_blacklist:{token} → 1
TTL    sys_token_blacklist:{token} → 28569   （≈7.94h，8h 签发已用 ≈6min ✓）
```

**⑬ 旧 token 再访 → 401（黑名单拦截）**

```
GET /api/auth/me -H "Authorization: Bearer {旧token}" → [HTTP 401]
```

**⑭ 用户版本键（登出后残留，供 TokenVersion 校验；无 TTL）**

```
GET sys_user_token:841915844415557 → 1
TTL 同键 → -1
```

### 4) 数据库核查（Npgsql 探针，dev PG 172.19.0.10）✅

**增量加列生效**（应用启动后）：

```
sys_user columns: ... accounttype default=777, tokenversion default=1
admin(id=841915844415557): accountType=777 tokenVersion=1   （存量行默认值回填 ✓）
```

**sys_user_otp 恰 1 行，密文非明文**（明文 secret 为 ③ 的 `VANYBR52FEQI5F2AFLBBPR2VC4ACWMLT`）：

```
userId=841915844415557 cipher=CYhJBon3RsIfN2ekAuU2WsCWovHq0BhALqT9IuSXwnnB9j5R/SEh3d8HR9YSJs1juzszJVjh5tFoG1KpqCKYwA==
lastStep=59590973 enabled=True bindTime=26-08-26 15:18
```

cipher 为 48B Base64（16B IV ‖ 32B 密文，AES-256-CBC 对 32 字符 Base32 明文的长度自洽），与明文逐字符不同。

### 5) TOTP 实现互证 ✅（python 标准库独立实现 RFC6238 ↔ Otp.NET，6 个任意时间戳全过）

```
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|59|287082           => OK (step=1, expect=1)
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|1111111109|081804   => OK (step=37037036, expect=37037036)
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|1111111111|050471   => OK (step=37037037, expect=37037037)
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|1750000000|678608   => OK (step=58333333, expect=58333333)
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|1750000045|010921   => OK (step=58333334, expect=58333334)
GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ|9999999999|948864   => OK (step=333333333, expect=333333333)
```

（Otp.NET 为应用运行时引擎，python 为独立实现；任意时间戳互证排除"恰好当前时间窗口"巧合）

## 三、披露

1. **appsettings `Cryptogram:Key` 占位作废**：首版占位 26 字节（应为 32），一阶段正确密码路径首次触发 `Decrypt` 时启动 fail-fast 拦截（500"系统繁忙"）——快速失败按设计生效；已重新生成 32 字节 Base64 占位更新 appsettings 并重启复验（开发占位口径同 `Jwt:Secret`，生产环境变量注入）。
2. **锁定阻塞验收流**：5 次错误后锁定（计数 5≥5）使正确密码也被拒——契约语义正确；为继续验收 `DEL sys_password_error_times:admin`（锁定行为证据已留存于上 ①②）。
3. **`Otp.NET.csproj` 编码为 LF+BOM**：与仓库 CRLF 惯例不一致（源码文件自身编码，非本任务引入）；仅追加 `GeneratePackageOnBuild=False` 一行，未做重编码（避免源码 diff 噪音）。
4. **172.19.x 环境变量覆盖**：入库 appsettings 保持 172.18.x（005 先例：连接串以环境变量注入环境差异）；实机以 `ConnectionStrings__Default`/`ConnectionStrings__Redis` 指向 172.19.0.10/172.19.0.2（dev PG 已含 admin 种子；沙箱无 docker，以既有 dev 容器等价替代）。
5. **`ICacheService.GetAsync<T>` 值类型语义**：缺键 → `default`（int → 0），不可 `is null`/`?? 0`（编译期 CS0037/CS1503 拦截，本仓库编译门禁已覆盖）；错误计数缺键=0、版本键 `<=0` 触发 DB 回退均依此语义。
6. **`/me` 迁移 `ApiResult<object>`**（006 统一响应契约）；JWT 中间件级失败（claim 缺失/黑名单/版本不一致）走 `context.Fail` → 标准 401 challenge（空体），`ApiResult` 仅约束控制器处理端点响应体——与认证管道既有 401 口径一致。
7. **实机残留**：dev PG `sys_user_otp` 行（admin 已绑定 OTP）为验收真实产物，**予以保留**（后续 admin 登录将需 TOTP 码，secret 见 ③ 或从密文恢复需 `Cryptogram:Key`）；Redis `sys_*` 测试键已全部清理，`huluyeye:*` 既有键未触碰。
8. **自动 build**：提交后 `/data/tasks/build/task_007_ha_otp.txt` 落盘检查（写报告时点）未见文件，同 005/004 现象；本条以本地全量 build + `dotnet run` 编译通过为证。

## 四、交付最小清单

- **限域**：认证登录域（两阶段 OTP）+ 缓存抽象 + 令牌生命周期治理；不触碰 RBAC（M3）、前端页面、既有 6 条单测。
- **引用影响面**：`Program.cs` 启动管线（AddCache/预热/JwtBearer events）；`SysUser` 实体（+2 列，`[IncreTable]` 增量，存量行默认值回填）；删除 `Business/Services/AuthService.cs`（005 单阶段登录唯一引用方 AuthController 已重写）；Otp.NET 入 slnx（多目标编译入 0 警告门禁）。
- **回滚方式**：`git revert a0f0189` 单提交即回（26 文件同批）；DB 残留无害（`sys_user_otp` 1 行 + `sys_user` 2 个新列带默认值，回滚后为死数据/死列，不阻塞旧版本运行）；Redis 无持久状态。
- **最小验证**：①`dotnet build` 0/0；②`dotnet test` 6/6；③curl 四步——错码 401→正确码 200→重发 401→登出后旧 token 401；④`redis-cli TTL sys_login_otp_challenge:*`=300。
