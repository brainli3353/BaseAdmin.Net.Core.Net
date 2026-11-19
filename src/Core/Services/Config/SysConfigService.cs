using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Config.Dto;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.Config
{
    /// <summary>
    /// 系统配置服务(批 D 4.1):分页 / 按 Code 读值(缓存) / 新建(Code 唯一) / 部分更新 / 删除(SysFlag 内置禁删)。
    /// 缓存口径:读路径 GetConfigValueByCodeAsync 命中直返、未命中回源回填(TTL 7 天防泄漏兜底);
    /// 写路径 Update/Delete 变更即失效(删键,契约 §2 不靠过期兜底)。
    /// </summary>
    [Service]
    public class SysConfigService
    {
        /// <summary>
        /// 操作日志记录开关配置 Code(批 I 5.1,参照 ConfigConst.SysOpLog 口径):
        /// OpLogFilter 发布前读取,值 "True"/"False";关→不发 op_log 事件;缺失/解析失败 fail-open(记录)。
        /// </summary>
        public const string SysOpLog = "sys_oplog";

        /// <summary>
        /// 升级公告提示开关配置 Code(批 AF 4.11):True/缺省=升级公告(NoticeTypeEnum.Upgrade)参与登录后未读提示;
        /// False=不提示(其余通知类型照常)。缺失/解析失败 **fail-open**(同 SysOpLog 口径)。
        /// </summary>
        public const string SysUpgrade = "sys_upgrade";

        /// <summary>
        /// 登录 OTP(第二道校验)全局开关配置 Code:值 "False"(**仅显式**,忽略大小写/首尾空白)＝关——
        /// 密码通过直接签发令牌,不再出挑战、不再要码;
        /// "True"/缺失/任何无法解析值 ＝开(现状,两阶段强制 OTP)。fail-safe——读不到开关绝不静默关闭二次验证
        /// (与 sys_oplog/sys_upgrade 同口径,此处"开"=强制 OTP)。种子内置 Value="True"、SysFlag=true 禁删可改值,
        /// 管理端系统配置页可直接切换;改 Value 经 7 天缓存＋变更主动失效立即生效。
        /// </summary>
        public const string OtpEnabled = "sys_otp_enabled";

        /// <summary>
        /// 单点登录(单设备踢人 + 服务端强吊销)全局开关配置 Code:值 "true"/"1"(忽略大小写)＝启用——
        /// 新登录踢旧连接 + 递增本用户 TokenVersion 作废旧 token(其他地方登录失效);
        /// 缺失/"false"/任何其它值 ＝ 关(缺省,多端并存零行为变化)。**缺省语义与 sys_otp_enabled 相反**(otp 缺省开、本开关缺省关),
        /// 种子内置 Value="false"、SysFlag=true 禁删可改值,管理端系统配置页可直接切换。
        /// </summary>
        public const string SingleLoginEnabled = "sys_single_login";

        /// <summary>配置值缓存 TTL:防缓存泄漏兜底(失效主机制是写路径删键)。</summary>
        private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromDays(7);

        private readonly ISqlSugarRepository<SysConfig> _configRepo;
        private readonly ICacheService _cache;
        private readonly UserManager _userManager; // 2026-10-27 ㉖:平台级配置写侧收口需超管判定

        public SysConfigService(ISqlSugarRepository<SysConfig> configRepo, ICacheService cache, UserManager userManager)
        {
            _configRepo = configRepo;
            _cache = cache;
            _userManager = userManager;
        }

        /// <summary>
        /// **平台级配置写侧闸**(2026-10-27 ㉖ 用户裁定):裁语原文——「所有系统设置应禁止租户修改;
        /// 租户仅作为我们系统内的一个甲方,系统核心设置由我们控制;租户仅可修改业务区的业务设置和业务内容。」
        /// 判定经 <see cref="UserManager.IsSuperAdmin"/> 单点(Claim AccountType==999),不另立平行判定。
        /// WHY 必须收在服务写侧而非只靠端点:三个写端点仅贴 `[MenuPerm]`,⇒ **任何被授予该菜单的普通角色
        /// (含租户内角色)都能改平台开关**;而 `sys_config` 现 4 行(`sys_oplog`/`sys_upgrade`/
        /// `sys_otp_enabled`/`sys_single_login`)**全是平台行为开关**,其中 `AuthService.cs:79` 注释原文即
        /// **「登录 OTP 全局开关」**——交给租户改＝安全降级(可自行关掉平台强制的 2FA)。
        /// 收口粒度＝**按表**(整表平台域),**不按行做规则引擎**(免矫枉过正);⚠ 若将来业务确需自定义配置,
        /// 正确形态是**另建业务配置表**,不要往 `sys_config` 塞。
        /// </summary>
        private void EnsurePlatformAdmin()
        {
            if (!_userManager.IsSuperAdmin)
            {
                throw AppException.Of(ErrorCodes.Forbidden);
            }
        }

        /// <summary>分页查询(name/code/groupCode 可选模糊;Id 降序稳定)。</summary>
        public async Task<PageResult<SysConfigOutput>> PageAsync(PageConfigInput input)
        {
            RefAsync<int> total = 0;
            var list = await _configRepo.AsQueryable()
                .Where(u => string.IsNullOrWhiteSpace(input.Name) || u.Name!.Contains(input.Name))
                .Where(u => string.IsNullOrWhiteSpace(input.Code) || u.Code.Contains(input.Code))
                .Where(u => string.IsNullOrWhiteSpace(input.GroupCode) || u.GroupCode.Contains(input.GroupCode))
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<SysConfigOutput>
            {
                Total = total,
                List = list
                    .Select(u => new SysConfigOutput(u.Id, u.Name, u.Code, u.Value, u.GroupCode, u.SysFlag, u.Remark, u.CreateTime, u.UpdateTime, u.EnumOptions))
                    .ToList()
            };
        }

        /// <summary>按 Id 查询(软删行自动过滤);不存在返回 null。</summary>
        public async Task<SysConfigOutput?> GetAsync(long id)
        {
            var config = await _configRepo.GetFirstAsync(u => u.Id == id);
            return config is null
                ? null
                : new SysConfigOutput(config.Id, config.Name, config.Code, config.Value, config.GroupCode, config.SysFlag, config.Remark, config.CreateTime, config.UpdateTime, config.EnumOptions);
        }

        /// <summary>
        /// 按 Code 读配置值(缓存口径):命中直返;未命中回源查库 → 回填缓存 → 返回。
        /// 配置不存在返回 null(不缓存 null——配置集小、不存在 Code 的探测缓存收益低且放大失效面)。
        /// </summary>
        public async Task<string?> GetConfigValueByCodeAsync(string code)
        {
            var cacheKey = $"{CacheConst.KeyConfig}{code}";
            var cached = await _cache.GetAsync<string>(cacheKey);
            if (cached is not null)
            {
                return cached;
            }

            var config = await _configRepo.GetFirstAsync(u => u.Code == code);
            if (config is null)
            {
                return null;
            }

            await _cache.SetAsync(cacheKey, config.Value, ConfigCacheTtl);
            return config.Value;
        }

        /// <summary>
        /// 新建配置:code 必填 + 唯一校验 → 插入(AOP 自动填雪花主键/审计)。
        /// </summary>
        public async Task<long> AddAsync(AddConfigInput input)
        {
            EnsurePlatformAdmin(); // ㉖ 平台级配置仅超管可建(见该方法注释)
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.ConfigCodeRequired);
            }

            var existing = await _configRepo.GetFirstAsync(u => u.Code == code);
            if (existing is not null)
            {
                throw AppException.With(ErrorCodes.ConfigCodeExists, "code", code);
            }

            var config = new SysConfig
            {
                Name = input.Name?.Trim(),
                Code = code,
                Value = input.Value ?? string.Empty,
                GroupCode = input.GroupCode?.Trim() ?? string.Empty,
                SysFlag = input.SysFlag,
                Remark = input.Remark,
                EnumOptions = string.IsNullOrWhiteSpace(input.EnumOptions) ? null : input.EnumOptions.Trim()
            };
            await _configRepo.InsertAsync(config);
            return config.Id;
        }

        /// <summary>
        /// 更新配置(部分更新,约定 #9):null = 不改;空串 = 显式清空(清值/清分组合法)。
        /// code 不可改(标识是读取方稳定契约)。更新成功 → 失效该 Code 缓存。
        /// </summary>
        public async Task UpdateAsync(UpdateConfigInput input)
        {
            EnsurePlatformAdmin(); // ㉖ 平台级配置仅超管可改(见 EnsurePlatformAdmin 注释)
            var config = await _configRepo.GetFirstAsync(u => u.Id == input.Id);
            if (config is null)
            {
                throw AppException.Of(ErrorCodes.ConfigNotFound);
            }

            if (input.Name is not null)
            {
                config.Name = input.Name.Trim();
            }

            if (input.Value is not null)
            {
                config.Value = input.Value;
            }

            if (input.GroupCode is not null)
            {
                config.GroupCode = input.GroupCode.Trim();
            }

            if (input.SysFlag is not null)
            {
                config.SysFlag = input.SysFlag.Value;
            }

            if (input.Remark is not null)
            {
                config.Remark = input.Remark;
            }

            if (input.EnumOptions is not null)
            {
                config.EnumOptions = string.IsNullOrWhiteSpace(input.EnumOptions) ? null : input.EnumOptions.Trim();
            }

            await _configRepo.UpdateAsync(config);
            await _cache.RemoveAsync($"{CacheConst.KeyConfig}{config.Code}");
        }

        /// <summary>删除配置:内置项(SysFlag)禁删(防误删框架依赖的开关)→ 软删 + 失效缓存。</summary>
        public async Task DeleteAsync(long id)
        {
            EnsurePlatformAdmin(); // ㉖ 平台级配置仅超管可删(见 EnsurePlatformAdmin 注释)
            var config = await _configRepo.GetFirstAsync(u => u.Id == id);
            if (config is null)
            {
                throw AppException.Of(ErrorCodes.ConfigNotFound);
            }

            if (config.SysFlag)
            {
                throw AppException.Of(ErrorCodes.ConfigBuiltinUndeletable);
            }

            await _configRepo.DeleteByIdAsync(id);
            await _cache.RemoveAsync($"{CacheConst.KeyConfig}{config.Code}");
        }
    }
}
