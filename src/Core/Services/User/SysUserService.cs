using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.OnlineUser;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.User.Dto;
using Microsoft.Extensions.Logging;
using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.SqlSugar;
using AdminCore.Core.Utils.DynamicQuery;
using Microsoft.Extensions.Configuration;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.User
{
    /// <summary>
    /// 用户管理(契约 §7 用户域 + 批 A 1.11/2.1 密码安全 + 批 B 数据权限收口 B1):分页(账号模糊,输出含机构/职位归属)
    /// / 新建(PBKDF2 哈希 + 唯一性 + 可选角色 + 可选机构/职位归属)/ 更新(超管账号禁改 + 唯一性 + 状态变更强制下线 + 机构/职位部分更新)
    /// / 删除(超管/本人禁删,软删 + 关联清理)
    /// / 分配角色(全量替换 + 权限缓存变更即失效)/ 改密(校验旧密 + 强度/历史开关 + 强制下线闭环 + 历史入库)
    /// / 重置密码(管理员代操作 + 强制下线闭环 + 清错误计数)/ 解锁登录(清密码/OTP 错误计数)。
    /// </summary>
    [Service]
    public class SysUserService
    {
        // 强度正则缺省(配置项 SysPassword:StrengthExpression 缺省时的兜底;8-20 位,含大小写字母 + 数字 + 特殊字符)
        private const string DefaultStrengthExpression = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,20}$";

        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarRepository<SysRole> _roleRepo;
        private readonly ISqlSugarRepository<SysUserRole> _userRoleRepo;
        private readonly ISqlSugarRepository<SysUserPasswordRecord> _pwdRecordRepo;
        private readonly ISqlSugarRepository<SysUserOtp> _otpRepo;
        private readonly ISqlSugarRepository<SysOrg> _orgRepo;
        private readonly ISqlSugarRepository<SysPos> _posRepo;
        private readonly IMenuPermService _menuPermService;
        private readonly ICacheService _cache;
        private readonly IConfiguration _config;
        private readonly SysOnlineUserService _onlineUserService;
        private readonly SysOrgService _orgService;
        private readonly UserManager _userManager;
        private readonly ILogger<SysUserService> _logger;

        public SysUserService(
            ISqlSugarRepository<SysUser> userRepo,
            ISqlSugarRepository<SysRole> roleRepo,
            ISqlSugarRepository<SysUserRole> userRoleRepo,
            ISqlSugarRepository<SysUserPasswordRecord> pwdRecordRepo,
            ISqlSugarRepository<SysUserOtp> otpRepo,
            ISqlSugarRepository<SysOrg> orgRepo,
            ISqlSugarRepository<SysPos> posRepo,
            IMenuPermService menuPermService,
            ICacheService cache,
            IConfiguration config,
            SysOnlineUserService onlineUserService,
            SysOrgService orgService,
            UserManager userManager,
            ILogger<SysUserService> logger)
        {
            _userRepo = userRepo;
            _roleRepo = roleRepo;
            _userRoleRepo = userRoleRepo;
            _pwdRecordRepo = pwdRecordRepo;
            _otpRepo = otpRepo;
            _orgRepo = orgRepo;
            _posRepo = posRepo;
            _menuPermService = menuPermService;
            _cache = cache;
            _config = config;
            _onlineUserService = onlineUserService;
            _orgService = orgService;
            _userManager = userManager;
            _logger = logger;
        }

        /// <summary>
        /// 分页 + 账号模糊查询 + 机构范围约束(PageResult&lt;UserOutput&gt;;软删行自动过滤)。
        /// 机构范围(批 L 2.1,参照 SysUserService.Page 移植,裁决见 g6 §5):超管/All 范围不过滤;
        /// 否则仅可见"机构范围集 ∪ 所属机构"内的用户(Define 授权不含本人机构时仍可见同机构,同参照);
        /// 指定 input.OrgId 时再与该机构子树(含自身)取交集——越权指定机构交集为空=空结果(fail-closed)。
        /// 范围计算复用 SysOrgService.GetUserOrgScopeAsync(批 B 2.7,含缓存/变更失效)。
        /// 排序按 Id 降序(雪花 ≈ 时间序,分页稳定)。
        /// </summary>
        public async Task<PageResult<UserOutput>> PageAsync(PageUserInput input)
        {
            // 机构范围过滤(与分页同一查询语句内完成,非先查全量再内存过滤)
            List<long>? finalOrgIds = null;   // null = 不过滤(超管/All/未登录)
            if (_userManager.UserId is long currentUserId && !_userManager.IsSuperAdmin)
            {
                var (maxScope, orgIds) = await _orgService.GetUserOrgScopeAsync(currentUserId);
                if (maxScope != DataScopeEnum.All)
                {
                    // 可见机构集 = 范围机构集 ∪ 所属机构(同参照 GetUserOrgIdList 恒含本人机构:
                    // Define 授权不含本人机构时仍可见同机构;Self 无角色未挂机构 → 空集 fail-closed)
                    finalOrgIds = orgIds.Distinct().ToList();
                    if (await _userRepo.GetFirstAsync(u => u.Id == currentUserId) is { OrgId: not null } self
                        && !finalOrgIds.Contains(self.OrgId.Value))
                    {
                        finalOrgIds.Add(self.OrgId.Value);
                    }
                }
            }

            // 指定机构查询:与子树(含自身)取交集(参照 orgList.Where(userOrgIdList.Contains));
            // 超管/All 指定机构 = 子树全量;非超管交集为空 = 空结果(越权指定机构不泄露数据)
            if (input.OrgId is long targetOrgId && targetOrgId > 0)
            {
                var subtree = await _orgService.GetChildIdListWithSelfById(targetOrgId);
                finalOrgIds = finalOrgIds is null
                    ? subtree
                    : finalOrgIds.Where(o => subtree.Contains(o)).ToList();
            }

            // 空集短路:未挂机构且无范围授权的用户看不到任何人(fail-closed;
            // 不在 SQL 里写常量三元——SqlSugar 会翻译成 IIF,其 C# 常量条件分支不受支持,批 L 实证)
            if (finalOrgIds is { Count: 0 })
            {
                return new PageResult<UserOutput> { Total = 0, List = new List<UserOutput>() };
            }

            RefAsync<int> total = 0;
            var query = _userRepo.AsQueryable()
                .Where(u => string.IsNullOrWhiteSpace(input.Account) || u.Account!.Contains(input.Account))
                // 批 UX:状态精确过滤(int? Status,缺省 null=不过滤;0 正常/1 停用)
                .WhereIF(input.Status.HasValue, u => u.Status == input.Status);
            if (finalOrgIds is not null)
            {
                var orgIdList = finalOrgIds;
                query = query.Where(u => u.OrgId.HasValue && orgIdList.Contains(u.OrgId.Value));
            }

            var list = await query
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            // 投影到输出模型:密码哈希等敏感字段不进入响应;orgId/posId 供前端编辑回显(批 B B1)
            return new PageResult<UserOutput>
            {
                Total = total,
                List = list.Select(u => new UserOutput(u.Id, u.Account, u.RealName, u.Status, u.AccountType, u.OrgId, u.PosId)).ToList()
            };
        }

        /// <summary>
        /// 新建用户:账号唯一性校验 → 密码 PBKDF2 哈希(明文永不落库)→ 插入(AOP 自动填主键/审计/租户)
        /// → 可选角色挂接(契约"默认角色"口径:建号时可一并指定,缺省即无角色)。
        /// </summary>
        public async Task<long> AddAsync(AddUserInput input)
        {
            var account = input.Account?.Trim();
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(input.Password))
            {
                throw AppException.Of(ErrorCodes.UserCredentialsRequired);
            }

            if (await _userRepo.IsAnyAsync(u => u.Account == account))
            {
                throw AppException.With(ErrorCodes.UserAccountExists, "account", account);
            }

            // 机构/职位归属校验(批 B B1):提供时须存在(防悬挂引用);缺省即无归属(NULL)
            if (input.OrgId is long orgId && await _orgRepo.GetFirstAsync(u => u.Id == orgId) is null)
            {
                throw AppException.With(ErrorCodes.UserOrgNotFound, "id", orgId);
            }

            if (input.PosId is long posId && await _posRepo.GetFirstAsync(u => u.Id == posId) is null)
            {
                throw AppException.With(ErrorCodes.UserPosNotFound, "id", posId);
            }

            var user = new SysUser
            {
                Account = account,
                Password = Pbkdf2Helper.HashPassword(input.Password),
                RealName = input.RealName,
                Status = input.Status is 0 or 1 ? input.Status.Value : 0,
                OrgId = input.OrgId,
                PosId = input.PosId
            };

            await _userRepo.InsertAsync(user);

            // 可选角色挂接:角色必须存在(防悬挂引用);逐条插入(AOP 逐条填雪花主键)
            if (input.RoleIds is { Count: > 0 })
            {
                var existingRoleIds = new HashSet<long>(
                    await _roleRepo.AsQueryable().Where(u => input.RoleIds!.Contains(u.Id)).Select(u => u.Id).ToListAsync());
                var missing = input.RoleIds.Where(id => !existingRoleIds.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    throw AppException.With(ErrorCodes.UserRoleNotFound, "ids", string.Join(",", missing));
                }

                foreach (var roleId in existingRoleIds)
                {
                    await _userRoleRepo.InsertAsync(new SysUserRole { UserId = user.Id, RoleId = roleId });
                }
            }

            return user.Id;
        }

        /// <summary>
        /// 更新用户:超管账号禁改(防自锁);账号变更做唯一性校验(排除自身);状态白名单 0/1。
        /// </summary>
        public async Task UpdateAsync(UpdateUserInput input)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == input.Id);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            var account = input.Account?.Trim();
            if (string.IsNullOrWhiteSpace(account))
            {
                throw AppException.Of(ErrorCodes.UserAccountRequired);
            }

            // 超管账号禁改:999 账号是权限穿透凭证,改绑等于越权
            if (user.AccountType == 999 && account != user.Account)
            {
                throw AppException.Of(ErrorCodes.UserSuperAdminReadonly);
            }

            if (account != user.Account && await _userRepo.IsAnyAsync(u => u.Account == account && u.Id != input.Id))
            {
                throw AppException.With(ErrorCodes.UserAccountExists, "account", account);
            }

            // 机构/职位归属校验(批 B B1):提供时须存在(防悬挂引用);null=不改(部分更新口径)
            if (input.OrgId is long updOrgId && await _orgRepo.GetFirstAsync(u => u.Id == updOrgId) is null)
            {
                throw AppException.With(ErrorCodes.UserOrgNotFound, "id", updOrgId);
            }

            if (input.PosId is long updPosId && await _posRepo.GetFirstAsync(u => u.Id == updPosId) is null)
            {
                throw AppException.With(ErrorCodes.UserPosNotFound, "id", updPosId);
            }

            // 状态变更判定(0/1 白名单且与现值不同):批 A 1.11 停用/启用共用强制下线入口——
            // 停用须即刻作废已登录 Token(不靠 8h 自然过期);启用虽无在会 Token,共用入口保持口径统一
            var statusChanged = input.Status is 0 or 1 && input.Status.Value != user.Status;

            // 机构归属变更判定(批 B B1):入参非 null 且与现值不同 → 该用户机构范围锚点变化,
            // 数据权限缓存须即时失效(Org/OrgAndChild 范围以用户自身 OrgId 为锚)
            var orgChanged = input.OrgId is not null && input.OrgId != user.OrgId;

            user.Account = account;
            user.RealName = input.RealName;
            user.Status = input.Status is 0 or 1 ? input.Status.Value : user.Status;
            // OrgId/PosId 部分更新(null=不改,与 UpdateOrgInput.Pid 同口径;解除归属本端点不支持,边界与机构 Pid 一致)
            if (input.OrgId is not null)
            {
                user.OrgId = input.OrgId;
            }

            if (input.PosId is not null)
            {
                user.PosId = input.PosId;
            }

            await _userRepo.UpdateAsync(user);

            if (statusChanged)
            {
                await OfflineAndExpireToken(user.Id);
            }

            if (orgChanged)
            {
                await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, user.Id);
            }
        }

        /// <summary>
        /// 删除用户:超管禁删、本人禁删(防自锁);软删(审计保留)+ 关联表物理清理
        /// (user_role 中间表 / OTP 绑定 / 密码历史均无审计价值,物理删防残留——S-G 数据一致性止血)。
        /// </summary>
        /// <param name="id">目标用户 Id。</param>
        /// <param name="currentUserId">当前登录用户 Id(本人禁删判定)。</param>
        public async Task DeleteAsync(long id, long currentUserId)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == id);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            if (user.AccountType == 999)
            {
                throw AppException.Of(ErrorCodes.UserSuperAdminUndeletable);
            }

            if (id == currentUserId)
            {
                throw AppException.Of(ErrorCodes.UserSelfDeleteForbidden);
            }

            // 先清关联再软删主记录:顺序防"角色已删但关联残留"的中间态被权限链路读到
            // 中间表物理删(仓储 DeletePhysicalByAsync;无审计价值)
            await _userRoleRepo.DeletePhysicalByAsync(u => u.UserId == id);
            // OTP 绑定(含 AES 密文)与密码历史同批物理清(S-G 数据一致性止血:与 user_role 同口径防残留,
            // 删号后绑定/历史对已删用户无意义,避免残留行被账号新建雪花 Id 之外的历史比对/绑定查询误读)
            await _otpRepo.DeletePhysicalByAsync(u => u.UserId == id);
            await _pwdRecordRepo.DeletePhysicalByAsync(u => u.UserId == id);
            await _userRepo.DeleteByIdAsync(id);
            // 用户被删 → 其权限缓存一并失效(残留缓存无主,无害但保持口径统一)
            await _menuPermService.InvalidateUserAsync(id);
            // 数据权限缓存同批失效(task_013 契约 5:用户删除 → 其最大范围/机构集缓存无主,保持口径统一)
            await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, id);
        }

        /// <summary>
        /// 分配角色(全量替换):清空该用户现有角色关联 → 按传入 Id 集重挂 → 失效其权限缓存
        /// (角色链路变了,7 天缓存不能兜底——变更即失效)。
        /// </summary>
        public async Task GrantRoleAsync(GrantRoleInput input)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == input.UserId);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            // WHY 同口径超管守卫(对齐 Delete/Update):给 999 账号挂角色会改写其权限链,次级管理员持本端点即可提权
            if (user.AccountType == 999)
            {
                throw AppException.Of(ErrorCodes.UserSuperAdminReadonly);
            }

            var roleIds = input.RoleIds is { Count: > 0 }
                ? input.RoleIds!.Distinct().ToList()
                : new List<long>();

            // 角色必须全部存在(防悬挂引用)
            if (roleIds.Count > 0)
            {
                var existingRoleIds = await _roleRepo.AsQueryable().Where(u => roleIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
                var missing = roleIds.Where(id => !existingRoleIds.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    throw AppException.With(ErrorCodes.UserRoleNotFound, "ids", string.Join(",", missing));
                }
            }

            // 全量替换:先清后挂(中间表物理删,无审计价值)
            await _userRoleRepo.DeletePhysicalByAsync(u => u.UserId == input.UserId);
            foreach (var roleId in roleIds)
            {
                await _userRoleRepo.InsertAsync(new SysUserRole { UserId = input.UserId, RoleId = roleId });
            }

            await _menuPermService.InvalidateUserAsync(input.UserId);
            // 数据权限缓存同批失效(task_013 契约 5:角色集变化 → 该用户最大范围收敛结果变化)
            await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, input.UserId);
        }

        /// <summary>
        /// 回读用户现有角色 Id 集(批 BG,分配角色对话框回显):GrantRoleAsync 是全量替换口径
        /// (空选=清空),无回显则管理员无从得知用户现挂哪些角色——直接保存即静默清空全部授权。
        /// 用户不存在 → UserNotFound(与 GrantRoleAsync 同口径)。
        /// </summary>
        public async Task<List<long>> GetUserRoleIdsAsync(long userId)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            return await _userRoleRepo.AsQueryable()
                .Where(u => u.UserId == userId)
                .OrderBy(u => u.RoleId)
                .Select(u => u.RoleId)
                .ToListAsync();
        }

        /// <summary>
        /// 强制下线闭环(批 A 1.11 三动作闭环①②;③ SignalR 强踢为批 F 预留口):
        /// TokenVersion++(DB)+ 删版本缓存(Redis)→ 旧 Token 的 TokenVersion Claim ≠ 新版本,
        /// JwtAuthHandler 下次请求即拒(快路径缺失 → DB 回查读新版本),无需等 8h 自然过期。
        /// 改密/重置/停用/启用共用本入口(baseadmin 原样口径)。
        /// </summary>
        /// <param name="userId">目标用户 Id。</param>
        public async Task OfflineAndExpireToken(long userId)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            if (user is null)
            {
                return;
            }

            // ① 版本号 +1(DB;刚加载的实体,整对象更新即等价 TokenVersion 列增量写)
            user.TokenVersion++;
            await _userRepo.UpdateAsync(user);

            // ② 删版本缓存(删后 JwtAuthHandler 快路径缺失 → DB 回查读新版本 → 旧 Token 即刻失效)
            await _cache.RemoveAsync($"{CacheConst.KeyUserToken}{userId}");

            // ③ (批 F 落地)SignalR 强踢:推送 forceOffline 使在线连接即时断开,免等下次请求被 TokenVersion 拒。
            //    失败不阻断主流程(约定 #10 不无说明吞异常:记日志;TokenVersion 路径仍保证最终失效,双保险)
            try
            {
                await _onlineUserService.ForceOfflineByUserIdAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR 强踢失败(不阻断;TokenVersion 路径仍保证旧 token 下次请求即拒): UserId={UserId}", userId);
            }
        }

        /// <summary>
        /// 修改本人密码(批 A 1.11):校验旧密码 → 强度(开关)/ 历史重复(开关)策略 →
        /// PBKDF2 哈希覆写新密码 → 历史记录总是入库(MD5)→ 强制下线闭环 + 清密码错误计数。
        /// </summary>
        /// <param name="userId">当前登录用户 Id(从 Token Claim 取,防改他人密码)。</param>
        /// <param name="input">旧密码 + 新密码。</param>
        public async Task UpdatePassword(long userId, UpdatePasswordInput input)
        {
            if (string.IsNullOrWhiteSpace(input.OldPassword) || string.IsNullOrWhiteSpace(input.NewPassword))
            {
                throw AppException.Of(ErrorCodes.UserOldNewPasswordRequired);
            }

            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            // 校验旧密码(恒定时间比对,防时序侧信道)
            if (!Pbkdf2Helper.VerifyPassword(input.OldPassword, user.Password))
            {
                throw AppException.Of(ErrorCodes.UserOldPasswordWrong);
            }

            // 新密码策略:强度(开关)+ 历史重复(开关,比对新密码 MD5 与最近 N 条历史)
            await ValidateNewPasswordAsync(input.NewPassword, user.Id, checkHistory: true);

            // 覆写新密码哈希 + 历史记录总是入库(MD5;开关只挡校验,不挡写入)
            // 批 M 1.11:用户自改密刷新最后改密时间(密码有效期判定基准,IsPasswordExpiredAsync)
            user.Password = Pbkdf2Helper.HashPassword(input.NewPassword);
            user.LastChangePasswordTime = DateTime.Now;
            await _userRepo.UpdateAsync(user);
            await _pwdRecordRepo.InsertAsync(new SysUserPasswordRecord { UserId = user.Id, PasswordMd5 = Md5(input.NewPassword) });

            // 强制下线闭环(改密后旧 Token 即刻作废)+ 清密码错误计数(重置锁定窗口)
            await OfflineAndExpireToken(user.Id);
            if (!string.IsNullOrWhiteSpace(user.Account))
            {
                await _cache.RemoveAsync($"{CacheConst.KeyPasswordError}{user.Account}");
            }
        }

        /// <summary>
        /// 管理员重置他人密码(批 A 1.11):无需旧密码(管理员代操作)→ 强度(开关)策略 →
        /// PBKDF2 哈希覆写 → 历史记录总是入库(MD5)→ 强制下线闭环 + 清错误计数(解锁锁定窗口)。
        /// 不查历史重复(管理员代设全新密码,重复场景弱)。
        /// </summary>
        public async Task ResetPwd(ResetPwdInput input)
        {
            if (string.IsNullOrWhiteSpace(input.NewPassword))
            {
                throw AppException.Of(ErrorCodes.UserNewPasswordRequired);
            }

            var user = await _userRepo.GetFirstAsync(u => u.Id == input.UserId);
            if (user is null)
            {
                throw AppException.Of(ErrorCodes.UserNotFound);
            }

            // WHY 同口径超管守卫(对齐 Delete/Update):重置 999 账号口令等于接管其凭证,次级管理员持本端点即可提权登录
            if (user.AccountType == 999)
            {
                throw AppException.Of(ErrorCodes.UserSuperAdminReadonly);
            }

            // 强度(开关);重置不查历史重复(管理员代设全新密码)
            await ValidateNewPasswordAsync(input.NewPassword, user.Id, checkHistory: false);

            // 批 M 1.11:重置后置 null——用户未自证新密码,启用有效期时必触发到期改密(同参照口径)
            user.Password = Pbkdf2Helper.HashPassword(input.NewPassword);
            user.LastChangePasswordTime = null;
            await _userRepo.UpdateAsync(user);
            await _pwdRecordRepo.InsertAsync(new SysUserPasswordRecord { UserId = user.Id, PasswordMd5 = Md5(input.NewPassword) });

            await OfflineAndExpireToken(user.Id);
            if (!string.IsNullOrWhiteSpace(user.Account))
            {
                await _cache.RemoveAsync($"{CacheConst.KeyPasswordError}{user.Account}");
            }
        }

        /// <summary>
        /// 解锁登录(批 A 2.1):清空账号的密码错误计数 + OTP 失败计数缓存(30min 锁定窗口即刻解除),
        /// 供管理员为被锁定账号人工解锁;不触发 TokenVersion++(解锁不改变已登录会话)。
        /// </summary>
        public async Task UnlockLogin(UnlockLoginInput input)
        {
            var account = input.Account?.Trim();
            if (string.IsNullOrWhiteSpace(account))
            {
                throw AppException.Of(ErrorCodes.UserAccountRequired);
            }

            await _cache.RemoveAsync($"{CacheConst.KeyPasswordError}{account}");
            await _cache.RemoveAsync($"{CacheConst.KeyOtpError}{account}");
        }

        /// <summary>
        /// <summary>
        /// 密码有效期判定(批 M 1.11,参照 VerifyPwdExpirationTime 口径):
        /// 配置 SysPassword:ExpirationDays 缺省 0=关闭 → 恒 false;
        /// 启用时:LastChangePasswordTime 为 null(存量未改过/管理员重置后) → true(到期);
        /// (现在 − 最后改密时间) 天数 &gt; ExpirationDays → true(到期)。
        /// 供登录二阶段挂回 LoginResult.PasswordExpired(前端引导改密页);服务端不拒登——
        /// 拒绝则用户无法登录改密=锁死(参照 LoginUserOutput 同语义"回传引导",见 g7 §5)。
        /// </summary>
        public async Task<bool> IsPasswordExpiredAsync(long userId)
        {
            var expirationDays = _config.GetValue("SysPassword:ExpirationDays", 0);
            if (expirationDays <= 0)
            {
                return false;
            }

            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            if (user is null)
            {
                return false;
            }

            if (user.LastChangePasswordTime is null)
            {
                return true;
            }

            return (DateTime.Now - user.LastChangePasswordTime.Value).Days > expirationDays;
        }

        /// <summary>
        /// 新密码策略校验(批 A 1.11):强度(配置开关 + 正则,默认关)+ 历史重复(配置开关,默认关)。
        /// 强度正则配置化(约定不硬编码:SysPassword:StrengthExpression;配置空时回退内置缺省正则)。
        /// 历史重复:新密码 MD5 落在最近 N 条历史内即拒(防频繁回退旧密码)。
        /// </summary>
        private async Task ValidateNewPasswordAsync(string newPassword, long userId, bool checkHistory)
        {
            // 强度(开关默认关;开启时按正则匹配——配置项为准,配置空回退内置缺省正则)
            var enableStrength = _config.GetValue("SysPassword:EnableStrength", false);
            if (enableStrength)
            {
                var expr = _config["SysPassword:StrengthExpression"];
                if (string.IsNullOrWhiteSpace(expr))
                {
                    expr = DefaultStrengthExpression;
                }

                if (!Regex.IsMatch(newPassword, expr))
                {
                    throw AppException.Of(ErrorCodes.UserPasswordWeak);
                }
            }

            // 历史重复(开关默认关;开启时比对新密码 MD5 与最近 N 条历史)
            if (checkHistory && _config.GetValue("SysPassword:EnableHistory", false))
            {
                var historyCount = _config.GetValue("SysPassword:HistoryCount", 5);
                var recent = await _pwdRecordRepo.AsQueryable()
                    .Where(u => u.UserId == userId)
                    .OrderByDescending(u => u.Id)
                    .Take(historyCount)
                    .Select(u => u.PasswordMd5)
                    .ToListAsync();

                if (recent.Contains(Md5(newPassword)))
                {
                    throw AppException.Of(ErrorCodes.UserPasswordReused);
                }
            }
        }

        /// <summary>密码 MD5(十六进制小写;历史比对统一 MD5,明文永不落库)。</summary>
        private static string Md5(string password)
            => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
    }
}
