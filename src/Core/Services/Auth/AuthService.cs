using AdminCore.Core.Attributes;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Auth.Dto;
using AdminCore.Core.Services.Config;
using AdminCore.Core.Services.Log;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Services.User;
using AdminCore.Core;
using AdminCore.Core.Cache;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Option;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace AdminCore.Core.Services.Auth
{
    /// <summary>
    /// 登录统一结果:Success + Error(失败原因)+ Data(一阶段成功)+ Token(二阶段成功)。
    /// 失败以结果对象表达(不抛异常)——控制器据此映射 401,异常通道留给真正的系统故障。
    /// </summary>
    public sealed record LoginResult(bool Success, string? Error, LoginOutput? Data = null, string? Token = null, bool PasswordExpired = false, string? RefreshToken = null, string? ErrorCode = null);

    /// <summary>
    /// 认证服务(task_007 两阶段登录 + 登出):
    /// 一阶段 POST /login——账号密码初验(错误计数 30 分钟窗口,达 5 次锁定)→ 通过则生成一次性挑战(300s),
    ///   未绑定用户附带新生成密钥 + otpauth URI;
    /// 二阶段 POST /login/otp——挑战 + 校验码(窗口 1,1 + 防重放;失败计数达 5 次作废)→ 通过则
    ///   首次成功即绑定密钥(AES 密文入库)+ 签发 JWT(Claim 扩展 AccountType/TokenVersion)
    ///   + 清错误计数 + 缓存 TokenVersion(供 JwtAuthHandler 快路径);
    /// 登出 POST /logout——原始 token 写入黑名单(TTL=剩余有效期),此后被 JwtAuthHandler 拒绝。
    ///
    /// 攻击面加固(task_007 盲审整改第 2 轮):
    /// ① 账号级 OTP 失败锁定(30min/10 次,独立于挑战)——单挑战 5 次作废只挡一个挑战,
    ///   持正确密码者可反复重发挑战绕过;账号级计数收敛总尝试次数;
    /// ② 挑战失败写回按剩余 TTL(不重置满 300s)——否则连续错码可无限续期挑战寿命;
    /// ③ 输入长度限制(DoS 防御)——超长 account 会经错误计数键写 redis 大键,超长 password 放大 PBKDF2 CPU。
    /// </summary>
    [Service]
    public class AuthService
    {
        // 契约常量:JWT 8h;挑战 300s;密码错误窗口 30min/5 次(第 6 次失败才见锁定提示);OTP 失败 5 次作废
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);
        private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(300);
        private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(30);
        private const int MaxPasswordErrors = 5;
        private const int MaxOtpFails = 5;
        // 盲审整改第 2 轮:账号级 OTP 失败锁定 30min/10 次(与密码错误同窗口口径,收敛"重发挑战绕过单挑战作废"的总尝试次数)
        private const int MaxOtpErrors = 10;
        // DoS 防御输入上限:account 对齐 SysUser.Account 列长 64(超列长必不匹配,防超长串经计数键写 redis 大键);
        // password 128 远超现实密码,防超长输入放大 PBKDF2 10 万轮 CPU;code 契约 6 位,>8 必非法
        private const int MaxAccountLength = 64;
        private const int MaxPasswordLength = 128;
        private const int MaxCodeLength = 8;

        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarRepository<SysUserOtp> _otpRepo;
        private readonly ISqlSugarRepository<SysTenant> _tenantRepo;
        private readonly ICacheService _cache;
        private readonly IConfiguration _config;
        // 批 Q 6.8:Jwt/Otp 节经 IOptions 强类型消费(AddProjectOptions 集中绑定;
        // 既有缺省口径保留——RefreshTokenExpireHours <=0 回落 168 仍在消费点,双保险)
        private readonly IOptions<JwtOptions> _jwtOptions;
        private readonly IOptions<OtpOptions> _otpOptions;
        private readonly CryptogramHelper _cryptogram;
        private readonly OtpService _otpService;
        private readonly SysLogLoginService _loginLogService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AuthService> _logger;
        private readonly SysOnlineUserService _onlineUserService;
        private readonly SysUserService _userService;
        // 批 BB:登录 OTP 全局开关(sys_otp_enabled)读入口——scoped 服务注入 scoped AuthService 合法
        private readonly SysConfigService _sysConfigService;

        public AuthService(
            ISqlSugarRepository<SysUser> userRepo,
            ISqlSugarRepository<SysUserOtp> otpRepo,
            ISqlSugarRepository<SysTenant> tenantRepo,
            ICacheService cache,
            IConfiguration config,
            IOptions<JwtOptions> jwtOptions,
            IOptions<OtpOptions> otpOptions,
            CryptogramHelper cryptogram,
            OtpService otpService,
            SysLogLoginService loginLogService,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AuthService> logger,
            SysOnlineUserService onlineUserService,
            SysUserService userService,
            SysConfigService sysConfigService)
        {
            _userRepo = userRepo;
            _otpRepo = otpRepo;
            _tenantRepo = tenantRepo;
            _cache = cache;
            _config = config;
            _jwtOptions = jwtOptions;
            _otpOptions = otpOptions;
            _cryptogram = cryptogram;
            _otpService = otpService;
            _loginLogService = loginLogService;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
            _onlineUserService = onlineUserService;
            _userService = userService;
            _sysConfigService = sysConfigService;
        }

        /// <summary>
        /// 一阶段:账号密码初验 → 挑战。失败路径(均为 401 + 消息):锁定 / 账号或密码错误 / 账号停用。
        /// 账号不存在与密码错误同口径(防攻击者借错误差异枚举有效账号);停用单独提示(账号确存在,泄漏停用状态无额外风险)。
        /// </summary>
        public async Task<LoginResult> LoginAsync(string? account, string? password)
        {
            var accountTrimmed = account?.Trim();
            if (string.IsNullOrWhiteSpace(accountTrimmed) || string.IsNullOrEmpty(password))
            {
                await WriteLoginLogAsync(accountTrimmed, null, false, "账号或密码错误", "login");
                return Fail(ErrorCodes.AuthLoginFailed);
            }

            // DoS 防御:输入长度上限。超长 account 会经错误计数键写入 redis 大键(内存放大),
            // 超长 password 放大 PBKDF2 十万轮 CPU;两者前置拒绝且不进错误计数(超列长串必不匹配真实账号,计数无意义)
            if (accountTrimmed.Length > MaxAccountLength || password.Length > MaxPasswordLength)
            {
                await WriteLoginLogAsync(accountTrimmed, null, false, "账号或密码错误", "login");
                return Fail(ErrorCodes.AuthLoginFailed);
            }

            // a. 错误计数锁定(30 分钟固定窗口):先查后用,计数已达标 → 拒绝;
            //    窗口自首次错误起算、不随后续错误续期(IncrementAsync 语义,防慢速攻击无限推迟锁定)
            var errorKey = ErrorKey(accountTrimmed);
            // GetAsync 对值类型键缺失返回 default(int=0)——恰好即"无失败计数"语义
            var errorCount = await _cache.GetAsync<int>(errorKey);
            if (errorCount >= MaxPasswordErrors)
            {
                await WriteLoginLogAsync(accountTrimmed, null, false, "登录失败次数过多,请 30 分钟后重试", "login");
                return Fail(ErrorCodes.AuthTooManyAttempts);
            }

            // b. 查账号 + 状态 + 密码(恒定时间比对,防时序侧信道,见 Pbkdf2Helper)
            var user = await _userRepo.GetFirstAsync(u => u.Account == accountTrimmed);
            if (user is null || user.Status != 0 || !Pbkdf2Helper.VerifyPassword(password, user.Password))
            {
                // 失败:原子 +1 并写回(首次失败建立 30 分钟窗口)
                await _cache.IncrementAsync(errorKey, ErrorWindow);
                // 批 AQ:日志面向运维→保持中文可读(与迁移前一致);对外响应走稳定错误码
                var accountDisabled = user is not null && user.Status != 0;
                await WriteLoginLogAsync(accountTrimmed, user?.TenantId, false, accountDisabled ? "账号已停用" : "账号或密码错误", "login");
                return Fail(accountDisabled ? ErrorCodes.AuthAccountDisabled : ErrorCodes.AuthLoginFailed);
            }

            // b2. 批 C 1.4:租户状态校验(有效账号的租户异常 → 初验拒绝;快反馈,不进密码错误计数——非密码错误)。
            //     二阶段(发 token 前)为最终门禁,双保险(防两阶段间租户被停用/过期)。
            var tenantError = await VerifyTenantAsync(user);
            if (tenantError is not null)
            {
                await WriteLoginLogAsync(accountTrimmed, user.TenantId, false, tenantError, "login");
                return Fail(tenantError);
            }

            // 批 BB:登录 OTP 全局开关(见 SysConfigService.OtpEnabled)。值仅显式 "false"(忽略大小写/首尾空白)→ 关,
            // 密码通过直接签发令牌(不再出挑战、不再要码);True/缺失/任何无法解析值→开(两阶段强制 OTP),fail-safe
            // 与改前完全一致。关分支复用与 LoginOtpAsync 尾部同一签发段(IssueTokensAsync),登录日志/密码到期口径同二阶段。
            if (!await IsOtpRequiredAsync())
            {
                var (token, rt, pe) = await IssueTokensAsync(user);
                return new LoginResult(true, null, null, token, pe, rt);
            }

            // c. 初验通过 → 判定绑定状态:已绑定且启用 → 取库内密钥(解密);否则 → 取/建 **pending 密钥**(首次校验通过才绑定)
            // 只查"启用"行:存量残留的禁用行(历史重绑遗留)不应把已绑定的用户误判为未绑定
            var otpRow = await _otpRepo.GetFirstAsync(o => o.UserId == user.Id && o.Enabled);
            var bindRequired = otpRow is null;
            string setupSecret;
            if (bindRequired)
            {
                // 批 AX F2:**复用 pending 行**(Enabled=false)的密钥,不再每次一阶段都新生成——
                // 修前每次调用 GenerateSetupSecret() ⇒ 用户扫过的二维码会因"页面又走一次一阶段"作废(实测缺陷);
                // pending 行落库但 Enabled=false:一阶段只认 Enabled=true ⇒ 未验证成功仍显示"需扫码"(不构成"已绑定"),
                // 同时同一用户重复一阶段拿到**同一**密钥(Redis/Memory 档、多实例皆然)。
                var pendingRow = await _otpRepo.GetFirstAsync(o => o.UserId == user.Id); // bindRequired ⇒ 命中的必为禁用行
                var pendingSecret = TryDecryptSecret(pendingRow?.SecretCipher);
                setupSecret = pendingSecret ?? _otpService.GenerateSetupSecret();

                // 落库/刷新 pending(失败只告警:复用是体验优化,不该让一阶段登录失败——挑战内已带密钥,本次仍可绑)
                try
                {
                    if (pendingRow is null)
                    {
                        await _otpRepo.InsertAsync(new SysUserOtp
                        {
                            UserId = user.Id,
                            SecretCipher = _cryptogram.Encrypt(setupSecret),
                            Enabled = false
                        });
                    }
                    else if (!string.Equals(pendingSecret, setupSecret, StringComparison.Ordinal))
                    {
                        pendingRow.SecretCipher = _cryptogram.Encrypt(setupSecret);
                        pendingRow.Enabled = false;
                        await _otpRepo.UpdateAsync(pendingRow);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OTP pending 密钥落库失败(本次登录可继续;仅「重复一阶段复用同一密钥」失效)");
                }
            }
            else
            {
                try
                {
                    // bindRequired=false 时 otpRow 必非空(上方判定保证)
                    setupSecret = _cryptogram.Decrypt(otpRow!.SecretCipher!);
                }
                catch (Exception)
                {
                    // 密文损坏(密钥材料变更/数据损坏)→ 视同未绑定走重绑:新密钥覆盖,旧密钥本已不可用,无损失
                    bindRequired = true;
                    setupSecret = _otpService.GenerateSetupSecret();
                }
            }

            // d. 生成挑战:雪花 Id + 300s TTL + 一次性(二阶段成功即消费删除,防同一挑战反复尝试/转发)
            var challengeId = YitIdHelper.NextId();
            await _cache.SetAsync(
                ChallengeKey(challengeId),
                new LoginOtpChallenge(user.Id, user.Account!, setupSecret, bindRequired, 0),
                ChallengeTtl);

            // e. 响应:未绑定附带 otpauth URI(扫码内容)+ 手动录入密钥
            LoginOutput data = new(challengeId.ToString(), bindRequired, null, null);
            if (bindRequired)
            {
                data = data with { OtpauthUri = BuildOtpauthUri(user.Account!, setupSecret), Secret = setupSecret };
            }

            return new LoginResult(true, null, data, null);
        }

        /// <summary>
        /// 二阶段:挑战 + 校验码换取 JWT。失败路径(均 401):挑战缺失/已消费/已过期 /
        /// 账号级 OTP 失败锁定(30min 内 10 次) / 校验码错误(FailTimes+1,达 5 次作废;按剩余 TTL 写回不续期) /
        /// 重放码 / 账号中途失效。
        /// </summary>
        public async Task<LoginResult> LoginOtpAsync(string? challengeId, string? code)
        {
            if (string.IsNullOrWhiteSpace(challengeId) || !long.TryParse(challengeId, out var cid) || string.IsNullOrWhiteSpace(code))
            {
                await WriteLoginLogAsync(null, null, false, "登录验证已过期,请重新登录", "login");
                return Fail(ErrorCodes.AuthChallengeExpired);
            }

            // DoS 防御:校验码长度上限(契约 6 位,>8 必非法)——前置拒绝,不消耗挑战失败次数与账号级计数
            if (code.Length > MaxCodeLength)
            {
                await WriteLoginLogAsync(null, null, false, "校验码验证失败", "login");
                return Fail(ErrorCodes.AuthOtpVerifyFailed);
            }

            var key = ChallengeKey(cid);
            var challenge = await _cache.GetAsync<LoginOtpChallenge>(key);
            if (challenge is null || challenge.FailTimes >= MaxOtpFails)
            {
                // 过期 / 已消费 / 已作废(5 次失败)——统一按"过期"回复,不泄漏具体状态
                await _cache.RemoveAsync(key);
                await WriteLoginLogAsync(challenge?.Account, null, false, "登录验证已过期,请重新登录", "login");
                return Fail(ErrorCodes.AuthChallengeExpired);
            }

            // 防重放基线:启用记录的最后使用时间步(与一阶段解密取源一致:优先启用行)
            var otpRow = await _otpRepo.GetFirstAsync(o => o.UserId == challenge.UserId && o.Enabled);
            var validate = _otpService.Validate(code, challenge.SetupSecret, otpRow?.LastUsedTimeStep);
            if (!validate.IsValid)
            {
                // 批 AX F1/F3:**重放**(码对但时间步已用过)与**错码**分流——
                // 重放提示"请用最新码"且**不计失败**(用户只是连点/同一步骤内重试,不是爆破尝试);
                // 错码维持既有计数与错误码(安全语义不变:两者都判失败、都不可登录)。
                if (validate.IsReused)
                {
                    await WriteLoginLogAsync(challenge.Account, null, false, "动态码已被使用,请使用当前最新码", "login");
                    return Fail(ErrorCodes.AuthOtpReused);
                }

                // 校验失败(错码):FailTimes+1 写回(单挑战低并发,读改写可接受)。
                // WHY 剩余 TTL 而非 ChallengeTtl:按满 300s 重置会使连续错码把挑战寿命无限续期
                var remaining = await _cache.GetExpireAsync(key);
                if (remaining is null || remaining <= TimeSpan.Zero)
                {
                    // 键已自然过期(并发场景)——与"挑战缺失"同语义:删除并按过期回复
                    await _cache.RemoveAsync(key);
                    await WriteLoginLogAsync(challenge.Account, null, false, "登录验证已过期,请重新登录", "login");
                    return Fail(ErrorCodes.AuthChallengeExpired);
                }

                await _cache.SetAsync(key, challenge with { FailTimes = challenge.FailTimes + 1 }, remaining);
                // 账号级计数 +1(原子自增,首次失败建立 30 分钟窗口,不随后续失败续期)
                await _cache.IncrementAsync(OtpErrorKey(challenge.Account), ErrorWindow);
                await WriteLoginLogAsync(challenge.Account, null, false, "校验码验证失败", "login");
                return Fail(ErrorCodes.AuthOtpVerifyFailed);
            }

            var matchedStep = validate.MatchedStep!.Value;

            // 绑定与登录一体:首次校验通过即持久化绑定。
            // 不变式"一个用户至多一条记录"(实体契约):
            // ① 启用记录存在 → 原地更新(密钥覆盖 + 防重放基线);
            // ② 仅存禁用记录(历史重绑残留)→ 同样原地更新并重新启用,绝不另插新行
            //    (实机缺陷修复:旧逻辑"禁用=重绑=插新行",同一用户重复行不断累积);
            // ③ 无任何记录 → 首次绑定插入。
            var secretCipher = _cryptogram.Encrypt(challenge.SetupSecret);
            if (otpRow is not null)
            {
                otpRow.SecretCipher = secretCipher;
                otpRow.LastUsedTimeStep = matchedStep;
                await _otpRepo.UpdateAsync(otpRow);
            }
            else
            {
                var legacyRow = await _otpRepo.GetFirstAsync(o => o.UserId == challenge.UserId);
                if (legacyRow is not null)
                {
                    legacyRow.SecretCipher = secretCipher;
                    legacyRow.LastUsedTimeStep = matchedStep;
                    if (!legacyRow.Enabled)
                    {
                        // 批 AX F2:由 pending 行(Enabled=false)转正时补绑定时刻——
                        // 一阶段现在会先落 pending 行,首次绑定走本分支,若不补则 BindTime 恒 null(数据质量回归)
                        legacyRow.BindTime = DateTime.Now;
                    }

                    legacyRow.Enabled = true;
                    await _otpRepo.UpdateAsync(legacyRow);
                }
                else
                {
                    await _otpRepo.InsertAsync(new SysUserOtp
                    {
                        UserId = challenge.UserId,
                        SecretCipher = secretCipher,
                        LastUsedTimeStep = matchedStep,
                        Enabled = true,
                        BindTime = DateTime.Now
                    });
                }
            }

            // 绑定落库后再发 token:确认账号仍存在且启用(并发停用/删除的兜底)
            var user = await _userRepo.GetFirstAsync(u => u.Id == challenge.UserId);
            if (user is null || user.Status != 0)
            {
                await _cache.RemoveAsync(key);
                await WriteLoginLogAsync(user?.Account, user?.TenantId, false, "登录验证已过期,请重新登录", "login");
                return Fail(ErrorCodes.AuthChallengeExpired);
            }

            // 批 C 1.4:租户状态最终门禁(发 token 点)——防一/二阶段之间租户被停用/过期
            var tenantError = await VerifyTenantAsync(user);
            if (tenantError is not null)
            {
                await _cache.RemoveAsync(key);
                await WriteLoginLogAsync(user.Account, user.TenantId, false, tenantError, "login");
                return Fail(tenantError);
            }

            // 批 BB:二阶段成功走与"关"直登同一签发段(IssueTokensAsync)——单登录/清错误计数/TokenVersion/
            // 登录日志/密码到期/refresh token 各项口径与改前逐项一致;挑战键本路径自持(一次性消费删除)
            var (token, rt, pe) = await IssueTokensAsync(user);
            // 挑战一次性:成功换取即消费删除(重发同挑战命中"过期";OTP 重放另有 DB LastUsedTimeStep 双保险)
            await _cache.RemoveAsync(key);
            return new LoginResult(true, null, null, token, pe, rt);
        }

        /// <summary>
        /// 批 BB:读 sys_otp_enabled 判"是否要求 OTP"。值仅显式 "false"(OrdinalIgnoreCase、trim)→ 关;
        /// 其余(null/"true"/任何无法解析值)→ 开。fail-safe:读不到开关绝不静默关闭二次验证。
        /// </summary>
        private async Task<bool> IsOtpRequiredAsync()
        {
            var value = await _sysConfigService.GetConfigValueByCodeAsync(SysConfigService.OtpEnabled);
            return !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 批 BB:二阶段成功 / 关直登共用的令牌签发段(自 LoginOtpAsync 尾部抽出,行为逐项一致)。
        /// 单登录(先踢后发,失败仅告警不阻断)→ GenerateJwtToken → 清密码/OTP 错误计数 →
        /// 缓存 TokenVersion(快路径)→ 登录日志 → 密码到期判定 → refresh token 双 JWT 同签。
        /// 不含用户/租户校验(调用方已做)、不含挑战键删除(各路径自持)。
        /// </summary>
        private async Task<(string Token, string? RefreshToken, bool PasswordExpired)> IssueTokensAsync(SysUser user)
        {
            // 单登录(批 F 1.1 软踢 + 批 CN 服务端强吊销):配置 sys_single_login 启用时——
            //    ① 踢掉该用户既有全部连接(ForceOffline,首方前端即时断开);
            //    ② 递增本用户 TokenVersion 并落库 + 删版本缓存——本次新 token 用递增后版本签发(下方 GenerateJwtToken 读 user.TokenVersion),
            //       随后写新版本回缓存 ⇒ DB/新token/缓存三者一致;该用户既有旧 token(Claim 版本落后)下次请求即被 JwtAuthHandler 拒。
            //    缺省 false = 多端并存(零行为变化)。先踢后发——新连接由客户端拿 token 后建立,不与本调用竞争。失败不阻断登录(约定 #10:记日志)。
            try
            {
                if (await _onlineUserService.SingleLoginAsync(user.Id))
                {
                    user.TokenVersion++;
                    await _userRepo.UpdateAsync(user);
                    await _cache.RemoveAsync($"{CacheConst.KeyUserToken}{user.Id}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "单登录强吊销/踢旧连接失败(不阻断登录): UserId={UserId}", user.Id);
            }

            var token = GenerateJwtToken(user);
            // 清错误计数(成功登录重置锁定窗口)+ 缓存 TokenVersion(JwtAuthHandler 快路径;无 TTL,版本变更时由业务刷新)
            await _cache.RemoveAsync(ErrorKey(user.Account!));
            await _cache.RemoveAsync(OtpErrorKey(user.Account!));
            await _cache.SetAsync($"{CacheConst.KeyUserToken}{user.Id}", user.TokenVersion);
            await WriteLoginLogAsync(user.Account, user.TenantId, true, "登录成功", "login");

            // 批 M 1.11:密码有效期判定挂回登录响应(前端引导改密页;服务端不拒登防锁死,见 g7 §5)
            var passwordExpired = await _userService.IsPasswordExpiredAsync(user.Id);
            // 批 N 6.4:refresh token 双 JWT 同签(参照 SysAuthService 口径,见 g8)
            var refreshToken = GenerateRefreshToken(user);
            return (token, refreshToken, passwordExpired);
        }

        /// <summary>
        /// 登出:原始 token 写入黑名单 sys_token_blacklist:{token}(TTL=剩余有效期),
        /// 此后 JwtAuthHandler 黑名单检查拒绝。token 已过期则不写(写入无意义)。
        /// </summary>
        public async Task LogoutAsync(string? authorizationHeader)
        {
            if (string.IsNullOrWhiteSpace(authorizationHeader))
            {
                return;
            }

            var token = authorizationHeader;
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token["Bearer ".Length..].Trim();
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                // ReadJwtToken 仅解析不验签——黑名单只关心"这个串别再用",签名无效串写不写都无影响
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
                var remaining = jwt.ValidTo - DateTime.UtcNow;
                // 批 D 5.1:登出登录日志(可解析 JWT 即记一条;account/tenant 从 token 自身 claims 取,不依赖请求上下文)
                var logoutAccount = jwt.Claims.FirstOrDefault(c => c.Type == JwtClaimNames.Account)?.Value;
                var logoutTenant = long.TryParse(jwt.Claims.FirstOrDefault(c => c.Type == JwtClaimNames.TenantId)?.Value, out var lt) ? (long?)lt : null;
                await WriteLoginLogAsync(logoutAccount, logoutTenant, true, "登出", "logout");
                if (remaining > TimeSpan.Zero)
                {
                    await _cache.SetAsync($"sys_token_blacklist:{token}", "1", remaining);
                }
            }
            catch (Exception)
            {
                // 非可解析 JWT(篡改/任意串)→ 不拉黑也不抛错(登出是尽力而为的幂等操作)
            }
        }

        /// <summary>
        /// 签发 JWT:Claims = UserId/Account/RealName(存量约定)+ AccountType/TokenVersion(task_007 扩展)
        /// + TenantId(task_008 租户基座:行级过滤/写入自动填的来源;存量行 TenantId 为空时兜底默认租户),
        /// HS256 对称密钥与 Issuer/Audience 从配置 Jwt 段读取。
        /// </summary>
        private string GenerateJwtToken(SysUser user)
        {
            // 批 Q 6.8:Jwt 节经 IOptions<JwtOptions> 强类型消费
            var jwt = _jwtOptions.Value;
            var secret = jwt.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                // 快速失败:缺密钥属部署配置错误,抛异常比签发"永远验不过"的 token 更早暴露问题
                throw new InvalidOperationException("缺少 Jwt:Secret 配置");
            }

            var token = new JwtSecurityToken(
                issuer: jwt.Issuer,
                audience: jwt.Audience,
                claims: new[]
                {
                    new Claim(JwtClaimNames.UserId, user.Id.ToString()),
                    // Account 是本次查询的匹配键(u.Account == account),此处必非空
                    new Claim(JwtClaimNames.Account, user.Account!),
                    new Claim(JwtClaimNames.RealName, user.RealName ?? string.Empty),
                    // task_007 扩展:AccountType(M2 权限体系区分超管)+ TokenVersion(强制失效,JwtAuthHandler 校验)
                    new Claim(JwtClaimNames.AccountType, user.AccountType.ToString()),
                    new Claim(JwtClaimNames.TokenVersion, user.TokenVersion.ToString()),
                    // task_008 租户基座:行级过滤/写入自动填的来源;存量行 TenantId 为空(历史数据无租户列值)
                    // → 兜底默认租户(契约 §5.3"存量数据天然带默认租户值"),登录侧不出现无租户身份
                    new Claim(JwtClaimNames.TenantId, (user.TenantId ?? SqlSugarConst.DefaultTenantId).ToString())
                },
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.Add(TokenLifetime),
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    SecurityAlgorithms.HmacSha256));

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// 签 refresh token(批 N 6.4,参照 SysAuthService 双 JWT 口径):与 access 同签名密钥同 claim 集
        /// (含 TokenVersion——改密/停用强制失效闭环天然覆盖),仅时效更长。
        /// 配置 Jwt:RefreshTokenExpireHours 缺省 168(7 天);&lt;=0 回落 7 天不关闭
        /// (续期是登录契约一部分,关闭会断裂前端契约——见 g8 §5 ①④)。
        /// 校验零新代码:refresh 走既有 JwtAuthHandler 同口径(签名/iss/aud/exp/TokenVersion/黑名单)。
        /// </summary>
        private string GenerateRefreshToken(SysUser user)
        {
            // 批 Q 6.8:Jwt 节经 IOptions<JwtOptions> 强类型消费(PostConfigure 缺省回落 168 已生效)
            var jwt = _jwtOptions.Value;
            var secret = jwt.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException("缺少 Jwt:Secret 配置");
            }

            // <=0 回落 7 天不关闭的消费口径保留(双保险:Option 层 PostConfigure 也落 168)
            var expireHours = jwt.RefreshTokenExpireHours;
            var lifetime = expireHours > 0 ? TimeSpan.FromHours(expireHours) : TimeSpan.FromHours(7);

            var token = new JwtSecurityToken(
                issuer: jwt.Issuer,
                audience: jwt.Audience,
                claims: new[]
                {
                    new Claim(JwtClaimNames.UserId, user.Id.ToString()),
                    new Claim(JwtClaimNames.Account, user.Account!),
                    new Claim(JwtClaimNames.RealName, user.RealName ?? string.Empty),
                    new Claim(JwtClaimNames.AccountType, user.AccountType.ToString()),
                    new Claim(JwtClaimNames.TokenVersion, user.TokenVersion.ToString()),
                    new Claim(JwtClaimNames.TenantId, (user.TenantId ?? SqlSugarConst.DefaultTenantId).ToString())
                },
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.Add(lifetime),
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    SecurityAlgorithms.HmacSha256));

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>组装 otpauth URI(扫码内容):otpauth://totp/{issuer}:{account}?secret=...&issuer=...&digits=6&period=30(契约格式)</summary>
        private string BuildOtpauthUri(string account, string secret)
        {
            // 批 Q 6.8:Otp 节经 IOptions<OtpOptions> 强类型消费
            var issuer = _otpOptions.Value.Issuer;
            if (string.IsNullOrWhiteSpace(issuer))
            {
                // 缺配置段时兜底,与 appsettings 默认值同口径,防 URI 断裂
                issuer = "huluyeye-admin";
            }

            var label = Uri.EscapeDataString($"{issuer}:{account}");
            return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        }

        /// <summary>
        /// 解密 OTP 密钥密文(批 AX F2 pending 复用用):空/损坏一律返回 null ⇒ 调用方回退"重新生成",
        /// 绝不让密文损坏升级成 500(与既有"密文损坏 → 视同未绑定走重绑"同口径)。
        /// </summary>
        private string? TryDecryptSecret(string? secretCipher)
        {
            if (string.IsNullOrWhiteSpace(secretCipher))
            {
                return null;
            }

            try
            {
                return _cryptogram.Decrypt(secretCipher);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 批 C 1.4:租户状态校验——解析用户租户(TenantId 为空 → 默认租户),校验存在/未停用/未过期。
        /// 返回 null=通过;否则返回失败原因(控制器映射 401)。一/二阶段登录均调用(双保险)。
        /// </summary>
        private async Task<string?> VerifyTenantAsync(SysUser user)
        {
            var tenantId = user.TenantId ?? SqlSugarConst.DefaultTenantId;
            var tenant = await _tenantRepo.GetFirstAsync(t => t.Id == tenantId);
            if (tenant is null)
            {
                return ErrorCodes.TenantNotFound;
            }
            if (tenant.Status != 0)
            {
                return ErrorCodes.TenantDisabled;
            }
            if (tenant.ExpirationTime is not null && tenant.ExpirationTime < DateTime.Now)
            {
                return ErrorCodes.TenantExpired;
            }
            return null;
        }

        /// <summary>
        /// 批 D 5.1:登录日志写入——await 直写(登录量小,同步写保证日志与登录动作同请求可见);
        /// 写入失败仅记日志不抛(日志是旁路观察,不得阻断登录主流程)。
        /// TenantId 显式赋值(AOP 仅 null 时填,显式安全):匿名/未知租户 → 默认租户吸收归属不明审计行;
        /// Browser/Os 留空(不引入 UA 解析依赖,预留字段;RemoteAddr 取请求远端地址,无 HttpContext 为 null)。
        /// </summary>
        private async Task WriteLoginLogAsync(string? account, long? tenantId, bool success, string message, string loginType)
        {
            try
            {
                await _loginLogService.LogAsync(new SysLogLogin
                {
                    Account = account,
                    RemoteAddr = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
                    Status = success,
                    Message = message,
                    LoginType = loginType,
                    TenantId = tenantId ?? SqlSugarConst.DefaultTenantId
                });
            }
            catch (Exception ex)
            {
                // 日志写失败不阻断登录主流程(见方法注释)——Warning 留痕可检索
                _logger.LogWarning(ex, "登录日志写入失败(不影响主流程):account={Account}", account);
            }
        }

        // 键统一走 CacheConst(批 A 起:解锁/重置需跨服务清同键,单点防漂移,约定 #11)
        private static string ErrorKey(string account) => $"{CacheConst.KeyPasswordError}{account}";

        private static string OtpErrorKey(string account) => $"{CacheConst.KeyOtpError}{account}";

        private static string ChallengeKey(long challengeId) => $"sys_login_otp_challenge:{challengeId}";

        private static LoginResult Fail(string errorCode) => new(false, null, null, null, ErrorCode: errorCode);
    }
}
