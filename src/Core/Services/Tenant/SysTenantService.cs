using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.Tenant.Dto;
using AdminCore.Core.SqlSugar;
using Microsoft.Extensions.Configuration;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.Tenant
{
    /// <summary>
    /// 租户管理服务(批 C 多租户业务模块 C1):CRUD + 启停。
    /// - 租户 Id 雪花(AOP 自动填),即 TenantId 标识(= 用户 TenantId Claim / SysUser.TenantId 取值);
    /// - 默认租户(SqlSugarConst.DefaultTenantId)受保护:不可停用、不可删除、不可修改(防平台管理员自锁——
    ///   改过期时间/连接串与停用同类风险,批 BM 补齐修改面);
    /// - 停用/删除时主动失效该租户下全部用户的 token 版本缓存(变更即失效,JwtAuthHandler 缓存缺失回查租户态即拒绝;
    ///   删除路径批 BM 补齐——版本键无 TTL,快路径命中不回查租户态,不失效则旧 token 持续有效);
    /// - 删除前置守卫:开放接口绑定(BindTenantId)存在则拒绝(参照 D1031,批 BM)。
    /// 租户管理是平台职能(超管),[MenuPerm] 门禁承担授权,无操作者自身范围约束(区别于机构数据范围授权)。
    /// </summary>
    [Service]
    public class SysTenantService
    {
        private readonly ISqlSugarRepository<SysTenant> _tenantRepo;
        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarRepository<SysOpenAccess> _openAccessRepo;
        private readonly ICacheService _cache;
        private readonly ISqlSugarClient _db;
        private readonly IConfiguration _config;

        public SysTenantService(ISqlSugarRepository<SysTenant> tenantRepo, ISqlSugarRepository<SysUser> userRepo,
            ISqlSugarRepository<SysOpenAccess> openAccessRepo, ICacheService cache,
            ISqlSugarClient db, IConfiguration config)
        {
            _tenantRepo = tenantRepo;
            _userRepo = userRepo;
            _openAccessRepo = openAccessRepo;
            _cache = cache;
            _db = db;
            _config = config;
        }

        /// <summary>分页查询(name 可选模糊;OrderNo 升序、Id 降序稳定)。</summary>
        public async Task<PageResult<TenantOutput>> PageAsync(PageTenantInput input)
        {
            RefAsync<int> total = 0;
            var list = await _tenantRepo.AsQueryable()
                .Where(u => string.IsNullOrWhiteSpace(input.Name) || u.Name!.Contains(input.Name))
                // 批 UX:Status 按实体列真实类型(int,0=启用 1=停用)精确匹配;实读确认非计算列
                .WhereIF(input.Status.HasValue, u => u.Status == input.Status)
                // 批 UX B1d:CreateTime 范围(闭区间口径,与同批 sysFile/既有 log 域 StartTime/EndTime 一致)
                .WhereIF(input.StartDate.HasValue, u => u.CreateTime >= input.StartDate)
                .WhereIF(input.EndDate.HasValue, u => u.CreateTime <= input.EndDate)
                // 批 CI:表头点击排序(orderBy 空=原默认序 OrderNo asc+Id desc;非空=白名单校验+表达式树排序)
                .ApplyDynamicSort(input.OrderBy, input.Desc, q => q.OrderBy(u => u.OrderNo).OrderByDescending(u => u.Id))
                .ToPageListAsync(input, total);

            return new PageResult<TenantOutput>
            {
                Total = total,
                List = list
                    .Select(u => new TenantOutput(u.Id, u.Name, u.Plan, u.Status, u.ExpirationTime, u.OrderNo, u.Remark, u.CreateTime,
                        u.DbName, !string.IsNullOrWhiteSpace(u.Connection)))
                    .ToList()
            };
        }

        /// <summary>
        /// 新建租户:name 必填校验 → 插入(AOP 自动填雪花主键/审计)→ 返回新租户 Id(= TenantId 标识)。
        /// 连接串(批 AB)非空 = 该租户落独立库(ConfigId=租户 Id,需随后调 initDb 建表/种子)。
        /// </summary>
        public async Task<long> AddAsync(AddTenantInput input)
        {
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.TenantNameRequired);
            }

            var tenant = new SysTenant
            {
                Name = name,
                Plan = input.Plan,
                ExpirationTime = input.ExpirationTime,
                OrderNo = input.OrderNo ?? 100,
                Remark = input.Remark,
                Connection = string.IsNullOrWhiteSpace(input.Connection) ? null : input.Connection!.Trim(),
                DbName = string.IsNullOrWhiteSpace(input.DbName) ? null : input.DbName!.Trim()
            };
            await _tenantRepo.InsertAsync(tenant);
            return tenant.Id;
        }

        /// <summary>
        /// 更新租户(部分更新:仅更新非 null 字段,约定 #9)。status 不在此改(启停走 SetStatusAsync,统一缓存失效挂钩);
        /// expirationTime null=不改(部分更新口径)——本批不支持"清空过期时间置永久"(与批 B UpdateUserInput 同口径,已文档化)。
        /// </summary>
        public async Task UpdateAsync(UpdateTenantInput input)
        {
            // 批 BM:默认租户不可修改(与禁删/禁停同类防自锁不变量)——本仓 Page 默认列出默认租户(无参照 IncludeDefault 隐藏),
            // UI 编辑入口直接可达;放行 ExpirationTime 修改 ⇒ 登录门+JWT 回查全平台拒绝(自锁),放行 Connection ⇒ 平台数据切库。
            if (input.Id == SqlSugarConst.DefaultTenantId)
            {
                throw AppException.Of(ErrorCodes.TenantDefaultUneditable, args: null, code: 400);
            }

            var tenant = await _tenantRepo.GetFirstAsync(u => u.Id == input.Id);
            if (tenant is null)
            {
                throw AppException.Of(ErrorCodes.TenantNotFound);
            }

            if (input.Name is not null)
            {
                tenant.Name = input.Name;
            }
            if (input.Plan is not null)
            {
                tenant.Plan = input.Plan;
            }
            if (input.ExpirationTime is not null)
            {
                tenant.ExpirationTime = input.ExpirationTime;
            }
            if (input.OrderNo is not null)
            {
                tenant.OrderNo = input.OrderNo.Value;
            }
            if (input.Remark is not null)
            {
                tenant.Remark = input.Remark;
            }

            // 连接串/库名(批 AB 库级隔离):部分更新口径(null=不改);变更即失效已注册的租户库连接,
            // 下次访问按新串重建(变更即失效,不靠重启——同批 C 停用租户即时失效 token 的口径)
            if (input.DbName is not null)
            {
                tenant.DbName = input.DbName;
            }

            if (input.Connection is not null)
            {
                var previous = tenant.Connection;
                tenant.Connection = string.IsNullOrWhiteSpace(input.Connection) ? null : input.Connection.Trim();
                if (!string.Equals(previous, tenant.Connection, StringComparison.Ordinal))
                {
                    TenantDbRegistry.Invalidate(tenant.Id);
                }
            }

            await _tenantRepo.UpdateAsync(tenant);
        }

        /// <summary>删除租户(软删)。默认租户不可删除(平台标识)。</summary>
        public async Task DeleteAsync(long id)
        {
            if (id == SqlSugarConst.DefaultTenantId)
            {
                throw AppException.Of(ErrorCodes.TenantDefaultUndeletable, args: null, code: 400);
            }

            // 批 BM:开放接口绑定守卫(参照 D1031 口径)——绑定租户软删后开放接口成悬空绑定,删前拒绝
            if (await _openAccessRepo.AsQueryable().AnyAsync(u => u.BindTenantId == id))
            {
                throw AppException.Of(ErrorCodes.TenantOpenAccessBound, args: null, code: 400);
            }

            var tenant = await _tenantRepo.GetFirstAsync(u => u.Id == id);
            if (tenant is null)
            {
                throw AppException.Of(ErrorCodes.TenantNotFound);
            }

            await _tenantRepo.DeleteByIdAsync(id);

            // 批 BM:删除租户(比停用更强的终态)→ 同样主动失效该租户全员 token 版本缓存。
            // 版本键无 TTL 且 JwtAuthHandler 快路径(缓存命中)只比对 TokenVersion、不回查租户态——
            // 不失效则已删租户用户的旧 token 持续有效(停用路径 SetStatusAsync 已做本失效,两路径同类同口径)。
            var userIds = await _userRepo.AsQueryable()
                .Where(u => u.TenantId == id)
                .Select(u => u.Id)
                .ToListAsync();
            foreach (var userId in userIds.Distinct())
            {
                await _cache.RemoveAsync($"{CacheConst.KeyUserToken}{userId}");
            }

            // 批 AB:删除租户 → 失效其独立库连接注册(防已删租户的连接常驻注册表;库内数据不删,由 DBA 处置)
            TenantDbRegistry.Invalidate(id);
        }

        /// <summary>
        /// 初始化租户独立库(批 AB C4):库存在性判定(可选自动建库)→ 注册/复用租户库连接 →
        /// [Tenant] 实体建表(CodeFirst 增量)→ 租户级种子(主键缺行插入、已有跳过)。幂等可重跑且不覆写租户运行期修改。
        /// </summary>
        /// <param name="id">租户 Id。</param>
        /// <returns>初始化摘要(库标识/建表数/种子数/库名)。</returns>
        /// <remarks>
        /// WHY 三步而非"交给启动流程":启动只建主库/日志库(Platform 侧),租户库属运行期资源
        /// (租户可能上线后新增),故初始化入口在租户管理端点(平台职能,[MenuPerm] 门禁)。
        /// WHY 自动建库默认关闭(<c>Tenant:AutoCreateDatabase</c>,缺省 false):CREATE DATABASE 需实例级权限,
        /// 多数生产库账号无此权限,由 DBA 预建更符合最小权限;开启时权限不足 fail-loud 提示预建。
        /// </remarks>
        public async Task<InitTenantDbOutput> InitDbAsync(long id)
        {
            var tenant = await _tenantRepo.GetFirstAsync(u => u.Id == id);
            if (tenant is null)
            {
                throw AppException.Of(ErrorCodes.TenantNotFound);
            }

            if (id == SqlSugarConst.DefaultTenantId)
            {
                throw AppException.Of(ErrorCodes.TenantDefaultMainDb, args: null, code: 400);
            }

            // ① 连接串解析(按开关解密;空=未配置 → fail-loud,不静默落主库)
            var connectionString = TenantDbRegistry.ResolveConnectionString(tenant);
            var databaseName = TenantDbRegistry.DatabaseNameOf(connectionString);

            // ② 可选自动建库(缺省关闭;幂等:库已存在不动作)
            if (_config.GetValue("Tenant:AutoCreateDatabase", false) && !string.IsNullOrWhiteSpace(databaseName))
            {
                await TenantDbRegistry.EnsureDatabaseAsync(MainClient(), databaseName!);
            }

            // ③ 注册/复用租户库连接(懒加载,幂等;required=true:初始化场景必须真具备独立库)
            var configId = await TenantDbRegistry.EnsureConnectionAsync(id, required: true);

            // ④ 建表 + 租户级种子(程序集取自启动登记簿:Core+Business,见 SqlSugarSetup.RegisteredAssemblies)
            var tableCount = _db.InitTenantTables(id);
            var seedCount = _db.InitTenantSeedData(id);

            return new InitTenantDbOutput(id, configId, tableCount, seedCount, databaseName);
        }

        /// <summary>取主库连接(建库判定/执行经主库;注册表缺失属装配缺陷,快速失败)。</summary>
        private ISqlSugarClient MainClient()
            => SqlSugarSetup.ConnectionClients.TryGetValue(SqlSugarConst.MainConfigId, out var main)
                ? main
                : throw new InvalidOperationException("主库连接未注册:无法执行租户库初始化");

        /// <summary>
        /// 租户启停(0=启用 1=停用)。默认租户不可停用(防平台管理员自锁)。
        /// 停用 → 主动失效该租户下全部用户的 token 版本缓存:其下次请求 JwtAuthHandler 缓存缺失 → 回查租户态 → 拒绝(变更即失效,不靠 TTL 兜底)。
        /// </summary>
        public async Task SetStatusAsync(SetTenantStatusInput input)
        {
            var tenant = await _tenantRepo.GetFirstAsync(u => u.Id == input.Id);
            if (tenant is null)
            {
                throw AppException.Of(ErrorCodes.TenantNotFound);
            }

            if (input.Id == SqlSugarConst.DefaultTenantId && input.Status != 0)
            {
                throw AppException.Of(ErrorCodes.TenantDefaultUndisableable, args: null, code: 400);
            }

            tenant.Status = input.Status;
            await _tenantRepo.UpdateAsync(tenant);

            // 停用 → 失效该租户全员 token 版本缓存(触发 JwtAuthHandler 回查)
            if (input.Status != 0)
            {
                var userIds = await _userRepo.AsQueryable()
                    .Where(u => u.TenantId == input.Id)
                    .Select(u => u.Id)
                    .ToListAsync();
                foreach (var userId in userIds.Distinct())
                {
                    await _cache.RemoveAsync($"{CacheConst.KeyUserToken}{userId}");
                }
            }
        }
    }
}
