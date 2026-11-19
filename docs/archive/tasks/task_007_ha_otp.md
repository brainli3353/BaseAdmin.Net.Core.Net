# task_id: task_007_ha_otp
# subject: task_007_ha_otp
# source: huluyeye
# created: 2026-08-26
# format: v3
# priority: P0
# dep_on: task_005_ha_jwt
# files: src/Business/Services/, src/Business/Entitys/SysUser.cs, src/Business/Entitys/SysUserOtp.cs, src/Web.Entry/Controllers/AuthController.cs, src/Web.Core/Auth/, src/Core/Cache/, src/Core/Security/, src/Web.Entry/Program.cs, src/Web.Entry/appsettings.json, thirdparty/, src/huluyeye-admin.slnx

# 任务：两阶段登录（账号密码初验 → OTP 校验）+ Redis 缓存接入（M2a）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第四节「JWT 鉴权设计」+ 第六节「OTP 两阶段登录」+ 第八节「代码目录与文件命名约定」——照此实现，禁止引入 Furion/Admin.NET 任何包。注：baseadmin 源码服务器无 clone，设计精华参考已提炼全部所需模式，无需读源码。
**代码纪律**：关键位置必须补中文注释（类/方法头一句话职责、复杂逻辑 WHY）。
**投递前提**：M1 终审通过后投递（task_006 ApiResult 亦在 M1 内，dep_on 单依赖限制下以 005 为硬依赖，006 由投递时机保证）。

## 背景

task_005（JWT 单阶段登录）已完成。本任务升级为**两阶段登录**（对齐 baseadmin 的安全改造）：密码初验只发一次性挑战，OTP 校验通过才签发 Token——密码正确但没输验证码拿不到任何凭据。同时接入 Redis 缓存（挑战/错误计数/黑名单/Token 版本的地基）。第三方 NuGet 包**仅新增 1 个**：StackExchange.Redis（理由=Redis 客户端，其余依赖最小化原则不变）。

## 契约（按此实现）

### 1. Redis 缓存接入（src/Core/Cache/）

- appsettings 加 `ConnectionStrings:Redis`：`172.18.0.2:6379`（开发容器内网，注释"仅开发用，生产密钥环境变量注入"；若容器启用了密码，Password 从服务器环境取，不过会话）
- `ICacheService` 接口：`Set/Get<T>/Remove/ExistKey`（均带可选过期时间）+ `HashSet/HashGet` 泛型 + JSON 序列化
- `RedisCacheService`：StackExchange.Redis 单例连接（`ConnectionMultiplexer`），实现 ICacheService
- `MemoryCacheService`：IMemoryCache 实现同接口（兜底）
- `CacheSetup.AddCache(this IServiceCollection)`：按配置 `Cache:Type`（Redis/Memory）注册对应实现——**Redis 优先、内存兜底，业务代码无感**（对齐 baseadmin 设计精华）

### 2. 密码与密钥工具（src/Core/Security/）

- `Pbkdf2Helper`：task_005 已建，不动
- `CryptogramHelper`：AES 对称加密（`Encrypt/Decrypt`，密钥从配置 `Cryptogram:Key` 读，32 字节 Base64，开发占位 + 注释"仅开发用"）——用于 OTP 密钥密文入库

### 3. OtpService（src/Core/Security/OtpService.cs）

- thirdparty/Otp.NET（源码移植已存在，MIT）加入解决方案 + Core 引用
- `GenerateSetupSecret()`：Base32 编码 20 字节随机密钥（对齐 baseadmin `KeyGeneration.GenerateRandomKey(20)`）
- `Validate(string code, string secret, long? lastUsedTimeStep)`：OtpNet `Totp` 校验，`VerificationWindow(1,1)`（允许前后各 1 个 30 秒窗口兼容时钟偏差）→ 返回匹配的时间步；**同一时间步不可重复使用**（`matchedStep <= lastUsedTimeStep` → 拒绝，防重放）

### 4. 实体扩展（src/Business/Entitys/，继承 task_005 的 SysUser 增量加列）

- `SysUser` 加字段：`AccountType`（int，999=超管 777=普通，默认 777）、`TokenVersion`（int，默认 1）——[IncreTable] 增量建表自动加列，老数据默认值兜底
- 新增 `SysUserOtp`：[SugarTable("sys_user_otp")] + [IncreTable]，字段：UserId、SecretCipher（CryptogramHelper 加密密文）、LastUsedTimeStep（long?）、Enabled（bool）、BindTime（DateTime?）

### 5. 两阶段登录改造（src/Business/Services/Auth/ + src/Web.Entry/Controllers/AuthController.cs）

> 目录按 baseadmin设计精华参考.md 第八节（业务域分目录）：AuthService 归 **Services/Auth/** 域，DTO 就近 **Services/Auth/Dto/**。若 task_005 已交付平铺版 `Services/AuthService.cs`，本任务移入 Auth/ 域目录（该文件已在 # files 锁定范围内，同批调整）。

**阶段一 `POST /api/auth/login`（账号密码初验，改造 task_005 的 LoginAsync）**：
- a. 密码错误次数 Redis 滑动窗口：键 `sys_password_error_times:{account}`，30 分钟 TTL，计数超 5 次拒绝（错误码带锁定提示）
- b. 查用户（Account 匹配）+ 状态校验（停用拒绝）+ PBKDF2 校验（复用）
- c. 通过 → 生成 challengeId（雪花）+ Redis 存挑战 `sys_login_otp_challenge:{challengeId}`（300 秒过期，含 UserId/SetupSecret/FailTimes）
- d. 响应（ApiResult 统一格式，task_006）：`challengeId` + `bindRequired`（SysUserOtp 是否已绑定）+ 首次未绑定附 `otpauth://totp/{issuer}:{account}?secret={s}&issuer={issuer}&digits=6&period=30` 与手动密钥（issuer 从配置读，缺省 `huluyeye-admin`）
- **二维码前端生成**（不引后端 NuGet 包：依赖最小化原则；前端登录页用 js 库按 otpauth URI 渲染）

**阶段二 `POST /api/auth/login/otp`（OTP 校验兑换 Token）**：
- a. 按 challengeId 取挑战（不存在/过期 → "登录验证已过期，请重新登录"）；FailTimes>=5 → 作废挑战
- b. OtpService.Validate + 防重放；失败 → FailTimes+1 写回挑战 → 拒绝
- c. 首次未绑定 → 校验通过即持久化绑定（SysUserOtp：UserId/SecretCipher/LastUsedTimeStep/Enabled=true/BindTime=Now）——绑定与登录合一；已绑定 → 更新 LastUsedTimeStep
- d. 通过 → 签发 JWT（Claims 加 `AccountType`、`TokenVersion`）+ 登录成功清密码错误计数 + 缓存 `sys_user_token:{userId}` = TokenVersion

### 6. JwtAuthHandler 增强（src/Web.Core/Auth/，基于 task_005）

- **Token 黑名单校验**：请求头 Token 在 `sys_token_blacklist:{token}` 缓存 → 拒绝
- **TokenVersion 校验**：Token 内 `TokenVersion` Claim ≠ 缓存 `sys_user_token:{userId}` → 拒绝；缓存缺失查库兜底（账号停用也在此拦截）
- **登出接口 `POST /api/auth/logout`**：当前 Token 写黑名单（TTL=Token 有效期）

### 7. 种子（src/Business/SeedData/）

- SysUserOtp 无种子；admin 种子用户（task_005 已有）不动；登录链路依赖的默认值走实体默认

## 交付物

- `/data/worktrees/admin/task_007_ha_otp/src/Business/Services/Auth/AuthService.cs`（含 Dto/：LoginInput/LoginOtpInput/LoginOutput 同批）
- `/data/worktrees/admin/task_007_ha_otp/src/Core/Cache/CacheSetup.cs`（含 ICacheService/RedisCacheService/MemoryCacheService 同目录同批）
- `/data/worktrees/admin/task_007_ha_otp/src/Business/Entitys/SysUserOtp.cs`（SysUser 增量加列同文件同批）

（附带修改：OtpService/CryptogramHelper 入 Core、Otp.NET 入解决方案、appsettings 加 Redis/Cryptogram 段、Program 注册缓存、AuthController/JwtAuthHandler 改造——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_007_ha_otp && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- 登录实测（验收报告附 curl 输出与步骤）：
  - 错密码 → 401 且错误计数递增；连错 6 次 → 锁定提示
  - 对密码 → 200 返回 challengeId + bindRequired=true + otpauth URI
  - 错误 OTP → 401 且挑战 FailTimes 递增；5 次 → 挑战作废（提示重新登录）
  - 正确 OTP → 返回 token；**同一验证码重复提交 → 拒绝（防重放）**
  - 再次登录（已绑定）→ bindRequired=false，新 Token 正常
  - 登出后旧 Token 调 `/api/auth/me` → 401
- `docker exec redis redis-cli --scan --pattern 'sys_login_otp_challenge:*'` 登录后存在挑战键且 TTL=300
- `docker exec postgres psql -U huluyeye -d huluyeye_admin -c "select * from sys_user_otp;"` 首次绑定后有一条记录（SecretCipher 为密文非明文）
- git commit（提交号写入验收报告），验收报告 `acceptance_task_007_ha_otp.md` 附以上实测输出
