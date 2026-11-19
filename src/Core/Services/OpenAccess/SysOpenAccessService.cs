using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.Services.OpenAccess.Dto;
using SqlSugar;
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.OpenAccess
{
    /// <summary>
    /// 开放接口身份服务(批 E 1.6/1.7/6.10):身份 CRUD + 密钥生命周期 + 签名预览 + Handler 取数。
    /// </summary>
    /// <remarks>
    /// 移植参照 AdminNetPro SysOpenAccessService(零 Furion 适配,落点依据 docs/研究/e1_refer_research.md §4/§8):
    /// - 缓存口径(§8-R5):身份行轻量快照走 ICacheService(全异步,无 GetOrAdd → 手写 读-未命中-回源-写),
    ///   TTL 7 天兜底 + 写路径(Update/Delete/ResetSecret)主动失效;绑定用户字段**不进缓存**(实时查用户行,
    ///   用户停用/改密即时生效,免跨服务失效协调);
    /// - 密钥:AccessSecret 服务端生成(OpenAccessSignHelper.CreateSecret,22 字符),仅 Add/ResetSecret 响应
    ///   一次性返回;分页/详情 Out 不回显(参照同口径 + 加固);
    /// - 分页:目标 PageResult + ToPageListAsync(参照 SqlSugarPagedList 替代,§8-R8);
    /// - 权限点:参照 Furion 动态 API 隐式路由,目标手写控制器逐端点 [MenuPerm("sysOpenAccess/xxx")] + 菜单按钮种子。
    /// </remarks>
    [Service]
    public class SysOpenAccessService
    {
        private readonly ISqlSugarRepository<SysOpenAccess> _openAccessRepo;
        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarClient _db;
        private readonly ICacheService _cache;

        public SysOpenAccessService(
            ISqlSugarRepository<SysOpenAccess> openAccessRepo,
            ISqlSugarRepository<SysUser> userRepo,
            ISqlSugarClient db,
            ICacheService cache)
        {
            _openAccessRepo = openAccessRepo;
            _userRepo = userRepo;
            _db = db;
            _cache = cache;
        }

        /// <summary>分页(Admin 面板):AccessKey 包含过滤;LeftJoin 用户/租户映射展示名;不回显密钥。</summary>
        public async Task<PageResult<OpenAccessOutput>> PageAsync(PageOpenAccessInput input)
        {
            RefAsync<int> total = 0;
            var list = await _db.Queryable<SysOpenAccess, SysUser, SysTenant>(
                    (a, u, t) => new JoinQueryInfos(
                        JoinType.Left, a.BindUserId == u.Id,
                        JoinType.Left, a.BindTenantId == t.Id))
                // 批 BV:空白判据对齐全站搜索口径(IsNullOrWhiteSpace,SysConfig/SysMenu/CodeGen 同)——
                // 全空格搜索参数应视为无过滤,而非 Contains(" ") 恒空结果
                .Where((a, u, t) => string.IsNullOrWhiteSpace(input.AccessKey) || a.AccessKey.Contains(input.AccessKey))
                // 批 UX B1d:CreateTime 范围(闭区间口径,与同批 sysFile/既有 log 域一致)
                .WhereIF(input.StartDate.HasValue, (a, u, t) => a.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, (a, u, t) => a.CreateTime <= input.EndDate)
                .OrderByDescending((a, u, t) => a.Id)
                .Select((a, u, t) => new OpenAccessOutput
                {
                    Id = a.Id, AccessKey = a.AccessKey, BindUserId = a.BindUserId,
                    BindUserAccount = u.Account, BindTenantId = a.BindTenantId, BindTenantName = t.Name,
                    CreateTime = a.CreateTime, UpdateTime = a.UpdateTime
                })
                .ToPageListAsync(input, total);
            return new PageResult<OpenAccessOutput>
            {
                Total = total,
                List = list
            };
        }

        /// <summary>
        /// 建号:AccessKey 唯一校验(冲突抛 AppException);AccessSecret 服务端自动生成并随响应一次性返回。
        /// </summary>
        public async Task<(OpenAccessOutput Output, string AccessSecret)> AddAsync(AddOpenAccessInput input)
        {
            ArgumentException.ThrowIfNullOrEmpty(input.AccessKey);
            // AccessKey 唯一性守卫**必须先跑**（2026-10-26 全量实跑纠正，勿再前置绑定校验）：
            // 本片初稿把 ValidateBindTargetAsync 放在这里之前，理由有二，**均已被证伪**——
            // ① "否则非法绑定用例会被 access_key_exists 抢先抛错而测不到" ⇒ 测不到就给它换不重复的 key 即可，
            //    不该为测试可达性改变真实错误码优先级（⑫ 新增用例正是这么写的，两条互不冲突）；
            // ② "可枚举已有 key" ⇒ 本端点受 [MenuPerm]("sysOpenAccess/add") 保护，同权限角色本可 page 列举，无枚举面。
            // 全量如期报红：Expected open_access.access_key_exists / Actual open_access.bind_user_not_found
            // （BatchSGErrCodeCoverageATest 注释即"唯一性守卫先于插入"＝既有契约）。
            // t846 批 2：本守卫必须 IgnoreTenant——它守护的 u_sys_open_access_accesskey **是全局唯一**
            // (按裁定不含 TenantId，见 SchemaIntegrityInitializer)。实体纳管后若沿用带租户过滤的仓储查询，
            // 非超管建号时看不见他租户的同名 key ⇒ 守卫必然漏检，插入撞全局唯一索引，
            // 把稳定的 `open_access.access_key_exists` 降级成 DB 异常（违 CLAUDE.md 约定 10「对外异常可检索」）。
            // ⇒ 守卫的过滤语义须与它所守护的约束语义一致；探测面可忽略（key 服务端随机生成、本端点受
            // [MenuPerm] 保护，非匿名，同上 ② 口径）。
            var exists = await _db.IgnoreTenant<SysOpenAccess>().AnyAsync(u => u.AccessKey == input.AccessKey);
            if (exists)
            {
                throw AppException.With(ErrorCodes.OpenAccessAccessKeyExists, "accessKey", input.AccessKey);
            }

            // S-G D1.4 片 a/b：绑定目标校验（裁定＝先做租户维度＋禁绑超管），仍在插入之前，覆盖不减。
            await ValidateBindTargetAsync(input.BindUserId, input.BindTenantId);

            var secret = OpenAccessSignHelper.CreateSecret();
            var entity = new SysOpenAccess
            {
                AccessKey = input.AccessKey,
                AccessSecret = secret,
                BindUserId = input.BindUserId,
                BindTenantId = input.BindTenantId,
                // t846 批 2:纳管后行归属租户必须与绑定租户同源(与库侧回填 TenantId = BindTenantId 同语义)。
                // 不显式填 ⇒ AOP 只会按"当前操作者 Claim"兜底,超管建跨租户身份时会落成 NULL/错租户 ⇒
                // 非超管此后读不到自己刚建的身份(静默失效)。
                TenantId = input.BindTenantId
            };
            await _openAccessRepo.InsertAsync(entity);

            // 回显仍按既有口径查原行（**不改用校验返回值**：校验刻意在 IgnoreTenant 下查，
            // 好让"跨租户用户"被判为不存在；而这里是管理端回显，口径不该跟着变）
            var user = await _userRepo.GetFirstAsync(u => u.Id == input.BindUserId);
            var tenant = input.BindTenantId;
            var tenantName = await _db.Queryable<SysTenant>().Where(t => t.Id == tenant).Select(t => t.Name).FirstAsync();
            var output = new OpenAccessOutput
            {
                Id = entity.Id, AccessKey = entity.AccessKey, BindUserId = entity.BindUserId,
                BindUserAccount = user?.Account, BindTenantId = tenant, BindTenantName = tenantName,
                CreateTime = entity.CreateTime
            };
            return (output, secret);
        }

        /// <summary>改绑:仅可改绑定用户/租户(AccessKey/AccessSecret 建号后不可改);变更后失效身份缓存。</summary>
        public async Task UpdateAsync(UpdateOpenAccessInput input)
        {
            // 先验身份行存在（NotFound），再校验绑定目标（与 AddAsync 同口径：输入/存在性优先于业务校验）。
            // 原稿在此先校验绑定，理由"绑定非法时不该泄露 id 存在性"——**已被全量证伪**：
            // BatchSGErrCodeCoverageATest.开放接口身份_不存在_三入口同抛OpenAccessNotFound 要求三入口
            // 对不存在的 id 一律抛同一码，先跑绑定校验会让本入口改抛绑定错，破坏"三入口同码"契约；
            // 且本端点已受 [MenuPerm] 保护，NotFound 本身即标准响应，不构成额外泄露面。
            var entity = await _openAccessRepo.GetFirstAsync(u => u.Id == input.Id)
                ?? throw AppException.Of(ErrorCodes.OpenAccessNotFound);
            await ValidateBindTargetAsync(input.BindUserId, input.BindTenantId);
            entity.BindUserId = input.BindUserId;
            entity.BindTenantId = input.BindTenantId;
            await _openAccessRepo.UpdateAsync(entity);
            await _cache.RemoveAsync(CacheConst.KeyOpenAccess + entity.AccessKey);
        }

        /// <summary>删除(软删)+ 失效身份缓存。</summary>
        public async Task DeleteAsync(long id)
        {
            var entity = await _openAccessRepo.GetFirstAsync(u => u.Id == id)
                ?? throw AppException.Of(ErrorCodes.OpenAccessNotFound);
            await _openAccessRepo.DeleteByIdAsync(id);
            await _cache.RemoveAsync(CacheConst.KeyOpenAccess + entity.AccessKey);
        }

        /// <summary>重置密钥:重新生成 AccessSecret(旧密钥即时失效)+ 失效身份缓存;新密钥仅本次返回。</summary>
        public async Task<string> ResetSecretAsync(long id)
        {
            var entity = await _openAccessRepo.GetFirstAsync(u => u.Id == id)
                ?? throw AppException.Of(ErrorCodes.OpenAccessNotFound);
            var secret = OpenAccessSignHelper.CreateSecret();
            entity.AccessSecret = secret;
            await _openAccessRepo.UpdateAsync(entity);
            await _cache.RemoveAsync(CacheConst.KeyOpenAccess + entity.AccessKey);
            return secret;
        }

        /// <summary>
        /// Handler 取数(按 AccessKey,签名 scheme):身份行缓存(7 天 TTL + 写路径失效),
        /// 绑定用户行实时查(不存在/停用 → BindUserValid=false,Handler 拒绝)。
        /// </summary>
        /// <remarks>
        /// t846 批 2:身份查询走 IgnoreTenant&lt;T&gt;()——AccessKey 全局定位是认证路径,
        /// 调用方尚未认证、不知道租户;若被租户过滤,认证会直接把全局唯一的 Key 判成"不存在"(缓存未命中回源同样在此口径内)。
        /// </remarks>
        public async Task<OpenAccessIdentity?> GetIdentityAsync(string accessKey)
        {
            var key = CacheConst.KeyOpenAccess + accessKey;
            var cached = await _cache.GetAsync<SysOpenAccess>(key);
            var entity = cached;
            if (entity is null)
            {
                entity = await _db.IgnoreTenant<SysOpenAccess>().Where(u => u.AccessKey == accessKey).FirstAsync();
                if (entity is null)
                {
                    return null;
                }
                await _cache.SetAsync(key, entity, TimeSpan.FromDays(7));
            }
            return await BuildIdentityAsync(entity);
        }

        /// <summary>
        /// Handler 取数(按 AccessSecret,ApiKey scheme):密钥即 X-API-KEY 值,直查不缓存
        /// (密钥查询为低频兜底路径;另立 secret 索引缓存 + 失效协调的成本不划算,口径见缺口披露)。
        /// </summary>
        /// <remarks>t846 批 2:同 GetIdentityAsync——认证路径,必须 IgnoreTenant(AccessSecret 同样全局定位)。</remarks>
        public async Task<OpenAccessIdentity?> GetIdentityBySecretAsync(string accessSecret)
        {
            var entity = await _db.IgnoreTenant<SysOpenAccess>().Where(u => u.AccessSecret == accessSecret).FirstAsync();
            return entity is null ? null : await BuildIdentityAsync(entity);
        }

        /// <summary>签名预览(管理端调试):参数口径与 Handler 验签一致(共用 OpenAccessSignHelper)。</summary>
        public Task<GenerateSignatureOutput> GenerateSignatureAsync(GenerateSignatureInput input)
        {
            var timestamp = input.Timestamp > 0 ? input.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = string.IsNullOrEmpty(input.Nonce) ? Guid.NewGuid().ToString("N") : input.Nonce;
            var signature = OpenAccessSignHelper.Sign(
                input.AccessSecret, input.Method, input.Url, input.AccessKey, timestamp.ToString(), nonce);
            return Task.FromResult(new GenerateSignatureOutput(timestamp, nonce, signature));
        }

        /// <summary>
        /// 绑定目标校验(S-G D1.4 片 a＋片 b;裁定＝先做租户维度＋禁绑超管)。四条判据全部**实读同源**,非新发明:
        /// ① 存在性 与 ② 启用态 —— 与 `BuildIdentityAsync` 判定 BindUserValid 用的 `user is not null &amp;&amp; user.Status == 0` 同口径;
        ///    写侧若比鉴权侧宽松,产出的就是"建得出来、却永远认证不过"的半成品密钥(运维只能在 401 里猜原因);
        /// ③ 禁绑超管 —— 机器身份不得继承权限穿透凭证(密钥一旦泄露＝全平台失守);
        /// ④ 租户一致 —— 身份的租户归属必须与绑定用户同租户(否则等于用 A 租户的密钥换成 B 租户的身份)。
        /// WHY 用 `IgnoreTenant` 查用户:仓储默认按**当前操作者**租户过滤,跨租户用户会被**伪装成"不存在"**,
        /// 那样 `bind_tenant_mismatch` 永远抛不出来、错误码对调用方撒谎;软删过滤**不清**(软删用户＝不存在,合理)。
        /// </summary>
        private async Task ValidateBindTargetAsync(long bindUserId, long bindTenantId)
        {
            var user = await _db.IgnoreTenant<SysUser>().Where(u => u.Id == bindUserId).FirstAsync()
                ?? throw AppException.Of(ErrorCodes.OpenAccessBindUserNotFound);

            if (user.TenantId != bindTenantId)
            {
                throw AppException.Of(ErrorCodes.OpenAccessBindTenantMismatch);
            }

            if (user.AccountType == UserManager.SuperAdminAccountType)
            {
                throw AppException.Of(ErrorCodes.OpenAccessBindUserSuperAdmin);
            }

            if (user.Status != 0)
            {
                throw AppException.Of(ErrorCodes.OpenAccessBindUserDisabled);
            }
        }

        /// <summary>装配身份快照:身份行(缓存值) + 绑定用户实时行(状态/版本/机构)。</summary>
        private async Task<OpenAccessIdentity> BuildIdentityAsync(SysOpenAccess entity)
        {
            var user = await _userRepo.GetFirstAsync(u => u.Id == entity.BindUserId);
            return new OpenAccessIdentity(
                entity.AccessKey, entity.AccessSecret, entity.BindUserId, entity.BindTenantId,
                user?.Account, user?.RealName, user?.AccountType ?? 0, user?.TokenVersion ?? 0, user?.OrgId,
                user is not null && user.Status == 0);
        }
    }
}
