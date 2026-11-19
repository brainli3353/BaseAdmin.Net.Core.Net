# 验收报告 task_007_ha_otp_fix(整改第 2/3 轮)

- 分支:`task_task_007_ha_otp_fix`(基于上轮交付 607a892)
- 代码提交:**bc6065e**(本报告提交号见 git log 后继提交)
- 执行人:开发(dev-a),2026-08-26

## 结论速览

| 验收项(任务书) | 上轮结果 | 本轮复测 | 说明 |
|---|---|---|---|
| build 0 警告 0 错误 | PASS | **PASS** | 本 worktree 实跑(9.2s) |
| 单测回归 | 6/6 | **PASS** | `dotnet test` 6/6 |
| 错密码 ×5 计数 / 第 6 次锁定 | PASS | **PASS** | S2/S3 |
| 对密码 → challengeId + bindRequired=true + otpauth URI | PASS | **PASS** | S4 |
| 错 OTP → FailTimes+1,5 次作废 | PASS | **PASS** | S6(含账号级计数联动) |
| 对 OTP → token + 重放拒绝 | PASS | **PASS** | S7/S8/S10 |
| 已绑定再登录 bindRequired=false | PASS | **PASS** | S9 |
| 登出后旧 token 401 | PASS | **PASS** | S13/S14 |
| 挑战键 TTL=300 | PASS | **PASS** | S5 |
| PG sys_user_otp 一条密文记录 | PASS | **PASS** | S16 |
| git 提交 + 验收报告 | PASS | **PASS** | bc6065e + 本报告 |

**本轮盲审攻击项修复(4 项全 PASS)**:

| # | 攻击项 | 修复 | 复测 |
|---|---|---|---|
| ① | OTP 暴力破解放大:持正确密码者可反复重发挑战,绕过单挑战 5 次上限,对 6 位码枚举 | 账号级 OTP 失败锁:30min 固定窗口内总失败 ≤10 次,超限拒绝(与密码错误计数同语义:窗口不续期、成功登录清除) | **PASS**(S6b/S6c:新挑战 + 正确码仍被账号锁拒绝) |
| ② | 挑战失败写回按满 300s 重置 TTL:连续错码可无限续期挑战寿命(上轮披露⑥观察项,与①组合放大) | 失败写回改为剩余 TTL(`ICacheService.GetExpireAsync` 新增,Redis 3.x `KeyTimeToLiveAsync` / Memory 自持登记簿) | **PASS**(S6d:错 1 码后 TTL 300→298,不再重置) |
| ③ | 输入长度无限制 DoS:超长 account 经计数键写 redis 大键;超长 password 放大 PBKDF2 十万轮 CPU | 长度上限前置拒绝:account ≤64(对齐 SysUser.Account 列长)/ password ≤128 / OTP code ≤8(契约 6 位);超限直接失败且不进错误计数 | **PASS**(S17-b/b2/f:10000 字符 10ms 返回 401,无大键落 redis;100 字符 code 401) |
| ④ | 标准攻击 4 条(空参数/超长输入/未授权/非法 JSON)此前未打真实端点 | 无需代码变更,补齐真实端点攻击覆盖 | **PASS**(S17-a/c/d:空参数 401、未授权 401、非法 JSON 400,均非 200/500) |

## 整改内容(相对 607a892,共 4 文件,98 行新增 / 3 行删除)

1. `src/Core/Cache/ICacheService.cs`(+6):接口新增 `Task<TimeSpan?> GetExpireAsync(string key)`——键不存在/无过期返 null,正值为剩余时间;两实现语义须一致(业务按 null=不可续期兜底)。
2. `src/Core/Cache/RedisCacheService.cs`(+6):实现为 `KeyTimeToLiveAsync`(3.x:正值为剩余;键不存在/无过期归一 null,与接口注释一致)。
3. `src/Core/Cache/MemoryCacheService.cs`(+33):实现 `GetExpireAsync`。`IMemoryCache` 无公开 API 回读过期时间,写入时自持 key→绝对过期时刻登记簿;已知边界见披露⑤。
4. `src/Business/Services/Auth/AuthService.cs`(+56/-3):
   - 攻击项①:新增账号级 OTP 失败锁——键 `sys_otp_error_times:{account}`,30min 固定窗口(自首次失败起算,`IncrementAsync` 原子自增不续期),`MaxOtpErrors=10` 超限拒绝;成功登录清除(与密码错误计数同口径)。单挑战 5 次作废(FailTimes)保持不变,任务书验收口径不受影响;
   - 攻击项②:`LoginOtpAsync` 失败路径写回由 `ChallengeTtl` 改为剩余 TTL(`GetExpireAsync`);剩余为 null/≤0(并发下键已自然过期)按"已过期"兜底删键;
   - 攻击项③:新增常量 `MaxAccountLength=64` / `MaxPasswordLength=128` / `MaxCodeLength=8`;`LoginAsync` 空值检查后、`LoginOtpAsync` 挑战有效性检查后分别前置拒绝(超限不消耗错误计数——超列长串必不匹配真实账号,计数无意义)。

## 复测证据(实跑,等价环境:dev PG 172.19.0.10 / dev Redis 172.19.0.2,详见披露①;2026-08-26 17:4x–17:54 CST)

| # | 场景 | 证据(实跑输出) |
|---|---|---|
| S2 | 错密码 ×5 → 401 + 计数递增 | 5 次均 HTTP=401「账号或密码错误」,`sys_password_error_times:admin` 逐次 1..5 |
| S3 | 第 6 次 → 锁定 | HTTP=401「登录失败次数过多,请 30 分钟后重试」 |
| S4 | 清窗口后对密码 → 挑战(首绑态) | HTTP=200,challengeId=842026299379781,bindRequired=true,otpauth URI + 密钥(密钥脱敏) |
| S5 | 挑战键 TTL=300 | `sys_login_otp_challenge:{id}` `TTL` = 300 |
| S6 | 错 OTP ×5 → FailTimes/账号计数递增;第 6 次作废 | 5 次均 HTTP=401「校验码验证失败」,failTimes 1..5 且 `sys_otp_error_times:admin` 1..5;第 6 次 HTTP=401「登录验证已过期,请重新登录」,键 TTL=-2(已删除) |
| S6b | 新挑战再错 ×5 → 账号计数 6..10 | 5 次均 HTTP=401,账号计数逐次 6..10(攻击项①前置验证:单挑战作废不重置账号计数) |
| S6c | 再发新挑战 + 正确码 → 仍被账号锁拒绝 | HTTP=401「校验码失败次数过多,请 30 分钟后重试」(**攻击项①核心证据:正确码也被拒**) |
| S6d | 清账号窗口后:错 1 码前后挑战 TTL 对比 | 错码前 TTL=300 → 错码后 TTL=298(间隔 2s),**不再重置 300**(攻击项②核心证据) |
| S7 | 对 OTP → token | HTTP=200 + JWT(脱敏);成功后密码/OTP 两个错误计数键均清空 |
| S8 | 已消费挑战重发 → 拒绝 | HTTP=401「登录验证已过期,请重新登录」 |
| S9 | 再登录(已绑定) | HTTP=200,bindRequired=false |
| S10 | 同时间步码重放(已绑定)→ 拒绝 | HTTP=401「校验码验证失败」(LastUsedTimeStep 防重放:matched≤lastUsed) |
| S11 | 下一窗口新码 → 200 新 token | HTTP=200 |
| S12 | /me 持 token → 200;无 token → 401 | 200 `{userId:"841915844415557",account:"admin",realName:"系统管理员"}` / 401 |
| S13 | 登出 → 200 + 黑名单 | HTTP=200,`sys_token_blacklist:{token}` TTL=28800(≈剩余 8h) |
| S14 | 登出后旧 token → 401 | HTTP=401(黑名单生效) |
| S15 | 令牌版本键 | `sys_user_token:841915844415557` = 1 |
| S16 | PG sys_user_otp | 1 行;密文 88 字符(AES-256-CBC:IV 16B+密文 32B 的 Base64);明文密钥未出现在密文(cipher_ok);enabled=true |
| S17-a | 空参数打真实端点 POST /api/auth/login `{}` | HTTP=401「账号或密码错误」(非 200/500) |
| S17-b | 超长输入(10000 字符 account+password) | HTTP=401「账号或密码错误」,耗时 10ms(长度门在前,无 PBKDF2/大键);redis 无 `sys_password_error_times:*` 大键 |
| S17-b2 | 超限账号(65 字符,>MaxAccountLength) | HTTP=401(长度门生效) |
| S17-c | 未授权 GET /api/auth/me | HTTP=401 |
| S17-d | 非法 JSON POST /api/auth/login `not-json` | HTTP=400(ASP.NET Core 模型绑定 400,非 200/500) |
| S17-e | 空参数打真实端点 POST /api/auth/login/otp `{}` | HTTP=401「登录验证已过期,请重新登录」 |
| S17-f | 超长 code(100 字符) | HTTP=401「校验码验证失败」(code 长度门,不消耗计数) |

回归:`dotnet build` 0 警告 0 错误(9.2s);`dotnet test` 6/6 通过。

## 披露

1. **等价环境复测**:dev-a 沙箱(172.19.x)不可达官方 172.18.x 网络;复测经环境变量注入 172.19.0.10(PG)/172.19.0.2(Redis)实跑,口径同上轮披露②。TOTP 码用 python3 标准实现生成,并经 Otp.Net(`Totp.VerifyTotp`,与线上 `OtpService.Validate` 同参数 SHA1/30s/6位/窗口(1,1))对照验证为有效码后才用于套件。
2. **admin OTP 行**:复测前删除 dev PG 旧绑定行(重验首绑路径);复测后 admin 已重新绑定(2026-08-26 17:53:52 CST),新行保留,处理同上轮披露③(密钥脱敏,不入本报告)。
3. **盲审脚本路径**:脚本硬编码构建路径 `/data/worktrees/admin/task_007_ha_otp`。本整改交付在 live worktree,复审前需由调度/流水线将 `task_task_007_ha_otp_fix` 分支同步至该路径(或改脚本路径)——同前两轮披露性质,属流水线事项,非 dev 职责内动作。
4. **窗口清除手法**:S4/S6d 手动 `DEL` 错误计数键模拟窗口过期(场景续跑手法,等价于自然 30min 窗口过期;生产行为不变)。
5. **Memory 实现已知边界(兜底路径)**:`IMemoryCache` 无公开 API 回读过期时间,`GetExpireAsync` 采用写入时自持的绝对过期登记簿;容量淘汰/进程内过期不经过 `RemoveAsync`,登记簿条目可能短暂滞留——剩余时间算得 ≤0 时业务按"已过期"兜底,不产生误放行。生产 `Cache:Type=Redis`,Memory 实现仅单实例兜底场景,无生产风险。
6. **已绑定用户登录响应不含 secret(防泄漏)**:S10/S11/S15 的 TOTP 码改为从 redis 挑战键直读 `setupSecret` 生成(复测取证手法,非代码变更)。
7. **新增键类型**:`sys_otp_error_times:{account}`(30min 窗口,自然过期消失,无历史数据/无迁移);`MaxOtpErrors=10` 为防御性新增口径,与任务书"单挑战 5 次作废"验收不冲突(S6 保持原语义)。
8. **首轮套件脚本自身缺陷(已修复,不影响交付代码)**:复测脚本首轮漏了"每次新登录重新提取挑战密钥"(已绑定后登录响应 secret=null),导致 S7/S10/S11/S15 误用 S4 密钥;修正后三轮全过,以上证据以第三轮输出为准。
9. **复测复跑(派发后,2026-08-26 18:05–18:12 CST)**:本轮交付(e82a2b2)后按任务书"对照验收标准复测"在 live worktree 复跑:build 0 警告 0 错误 + 单测 6/6 + S2–S17 全套件(执行前同②手法删 admin 绑定行重验首绑路径,18:09:53 CST 重绑完成、新行保留,redis 清零残留=0);24 项证据与 17:4x 轮次全一致无漂移。本轮日志留存 /tmp/retest007/retest_rerun.log(17:54 的 retest.log 原样保留)。

## 交付最小清单

- **限域**:4 文件(`ICacheService`/`RedisCacheService`/`MemoryCacheService`/`AuthService`),98 行新增 / 3 行删除,无其他文件变更;
- **引用影响面**:`ICacheService` 为共享缓存接口,仅增方法(向后兼容,无签名变更),Redis/Memory 两实现同步落地;新增 1 个缓存键类型,无既有键结构/序列化变更;
- **回滚方式**:`git revert bc6065e`;
- **最小验证**:build 0/0 + `dotnet test` 6/6 + 本报告 S2–S17 复测套件(脚本与输出留存于 dev-a `/tmp/retest007/`,临时文件不进项目)。
