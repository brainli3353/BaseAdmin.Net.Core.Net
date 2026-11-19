using AdminCore.Core.Cache;
using AdminCore.Core.Extensions;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Security;
using AdminCore.Core.Services.Org;
using AdminCore.Core.Services.Role.Dto;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Attributes;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.SqlSugar;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.Role
{
    /// <summary>
    /// 角色管理(契约 §7 角色域):分页 / 新建(Code 唯一) / 更新(仅展示字段)
    /// / 删除(内置 SysFlag 禁删 + 级联清理 SysUserRole/SysRoleMenu/SysRoleApi/SysRoleOrg/SysRoleTable(批 BH)
    ///   + 该角色全部用户权限缓存失效 + 字段权限受管声明重算/受管集合缓存失效(批 BH))
    /// / 角色授权(全量替换 + 该角色全部用户权限缓存变更即失效)
    /// / 接口黑名单授权(批 A 2.10:全量替换 SysRoleApi + 该角色全部用户权限缓存变更即失效)
    /// / 数据范围授权(批 B B2:全量替换 SysRoleOrg 自定义机构集 + 越权防护 + 该角色全部用户数据权限缓存变更即失效)
    /// / 复制角色(批 H 2.2:四张关联表全量复制,含 SysRoleTable 字段权限(批 BH 补))。
    /// </summary>
    [Service]
    public class SysRoleService
    {
        private readonly ISqlSugarRepository<SysRole> _roleRepo;
        private readonly ISqlSugarRepository<SysMenu> _menuRepo;
        private readonly ISqlSugarRepository<SysUserRole> _userRoleRepo;
        private readonly ISqlSugarRepository<SysRoleMenu> _roleMenuRepo;
        private readonly ISqlSugarRepository<SysRoleApi> _roleApiRepo;
        private readonly ISqlSugarRepository<SysRoleOrg> _roleOrgRepo;
        private readonly ISqlSugarRepository<SysOrg> _orgRepo;
        private readonly SysOrgService _orgService;
        private readonly SysRoleTableService _roleTableService; // 批 BH:角色删除/复制的 sys_role_table 级联
        private readonly UserManager _userManager; // 批 D 1.14:操作者身份经门面收敛(原 IHttpContextAccessor 直读)
        private readonly IMenuPermService _menuPermService;
        private readonly ICacheService _cache;

        public SysRoleService(
            ISqlSugarRepository<SysRole> roleRepo,
            ISqlSugarRepository<SysMenu> menuRepo,
            ISqlSugarRepository<SysUserRole> userRoleRepo,
            ISqlSugarRepository<SysRoleMenu> roleMenuRepo,
            ISqlSugarRepository<SysRoleApi> roleApiRepo,
            ISqlSugarRepository<SysRoleOrg> roleOrgRepo,
            ISqlSugarRepository<SysOrg> orgRepo,
            SysOrgService orgService,
            SysRoleTableService roleTableService,
            UserManager userManager,
            IMenuPermService menuPermService,
            ICacheService cache)
        {
            _roleRepo = roleRepo;
            _menuRepo = menuRepo;
            _userRoleRepo = userRoleRepo;
            _roleMenuRepo = roleMenuRepo;
            _roleApiRepo = roleApiRepo;
            _roleOrgRepo = roleOrgRepo;
            _orgRepo = orgRepo;
            _orgService = orgService;
            _roleTableService = roleTableService;
            _userManager = userManager;
            _menuPermService = menuPermService;
            _cache = cache;
        }

        /// <summary>分页(PageResult&lt;RoleOutput&gt;;软删行自动过滤;Id 降序)。</summary>
        public async Task<PageResult<RoleOutput>> PageAsync(PageRoleInput input)
        {
            // 批 UX:Keyword 先 Trim(空白视同不过滤),Name/Code 模糊 OR
            var keyword = input.Keyword?.Trim();
            RefAsync<int> total = 0;
            var list = await _roleRepo.AsQueryable()
                .WhereIF(!string.IsNullOrWhiteSpace(keyword), u => u.Name!.Contains(keyword!) || u.Code!.Contains(keyword!))
                .ApplyDynamicSort(input.OrderBy, input.Desc, u => u.Id, OrderByType.Desc)
                .ToPageListAsync(input, total);

            return new PageResult<RoleOutput>
            {
                Total = total,
                List = list.Select(u => new RoleOutput(u.Id, u.Name, u.Code, u.Remark, u.SysFlag, u.DataScope)).ToList()
            };
        }

        /// <summary>新建角色:Name/Code 必填,Code 唯一性服务层校验(建库不配唯一约束,与项目实体口径一致)。</summary>
        public async Task<long> AddAsync(AddRoleInput input)
        {
            var name = input.Name?.Trim();
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            {
                throw AppException.Of(ErrorCodes.RoleNameCodeRequired);
            }

            if (await _roleRepo.IsAnyAsync(u => u.Code == code))
            {
                throw AppException.With(ErrorCodes.RoleCodeExists, "code", code);
            }

            // DataScope 白名单校验(1-5;缺省 null → 实体默认 4 仅本人,task_013)
            if (input.DataScope is not null && (input.DataScope < 1 || input.DataScope > 5))
            {
                throw AppException.Of(ErrorCodes.RoleDataScopeInvalid, args: null, code: 400);
            }

            var role = new SysRole
            {
                Name = name,
                Code = code,
                Remark = input.Remark,
                // DataScope 缺省 null → 实体默认 4(仅本人);提供时按白名单值落库
                DataScope = input.DataScope ?? 4,
                // SysFlag 一律 false:内置角色只能由种子产生,禁止运行期造内置
            };

            await _roleRepo.InsertAsync(role);
            return role.Id;
        }

        /// <summary>
        /// 更新角色:Name/Remark(展示字段)+ DataScope(数据范围,task_013);Code 与 SysFlag 不可经本端点变更。
        /// DataScope 变更 → 该角色下全部用户数据权限缓存即时失效(变更即失效,不靠 7 天兜底)。
        /// </summary>
        public async Task UpdateAsync(UpdateRoleInput input)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == input.Id);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.RoleNameRequired);
            }

            role.Name = name;
            role.Remark = input.Remark;

            // DataScope 白名单校验(1-5,DataScopeEnum 定义域;越界拒绝而非静默兜底,防错误范围值落库)
            if (input.DataScope is not null && (input.DataScope < 1 || input.DataScope > 5))
            {
                throw AppException.Of(ErrorCodes.RoleDataScopeInvalid, args: null, code: 400);
            }

            // DataScope 变更判定:入参非 null 且与现值不同 → 应用 + 标记需失效(null = 不改,与机构部分更新同口径)
            var dataScopeChanged = input.DataScope is not null && input.DataScope != role.DataScope;
            if (dataScopeChanged)
            {
                role.DataScope = input.DataScope!.Value;
            }

            await _roleRepo.UpdateAsync(role);

            if (dataScopeChanged)
            {
                var userIds = await _userRoleRepo.AsQueryable()
                    .Where(u => u.RoleId == input.Id)
                    .Select(u => u.UserId)
                    .ToListAsync();
                foreach (var userId in userIds.Distinct())
                {
                    await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, userId);
                }
            }
        }

        /// <summary>
        /// 删除角色:内置角色(SysFlag)禁删(契约 §8);软删 + 级联清理中间表(SysRoleMenu/SysUserRole/SysRoleTable 物理删,无审计价值)
        /// + 该角色下全部用户权限缓存失效(缓存里的旧授权不能残留——变更即失效)。
        /// 批 BH:补 sys_role_table 级联(参照 DeleteRole 同口径)——此前漏删留孤儿行,
        /// 且"受管表"声明/缓存最长 7 天漂移(面板计数失实、该表对其他用户仍按受管裁剪)。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == id);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            if (role.SysFlag)
            {
                throw AppException.Of(ErrorCodes.RoleBuiltinUndeletable);
            }

            // 缓存失效在软删前取用户清单(软删后角色链路查询口径可能漂移,先固化快照)
            var userIds = await _userRoleRepo.AsQueryable().Where(u => u.RoleId == id).Select(u => u.UserId).ToListAsync();

            // 中间表物理删(仓储 DeletePhysicalByAsync;无审计价值)——含接口黑名单(批 A 2.10)+ 数据范围授权(批 B B2)
            await _roleMenuRepo.DeletePhysicalByAsync(u => u.RoleId == id);
            await _userRoleRepo.DeletePhysicalByAsync(u => u.RoleId == id);
            await _roleApiRepo.DeletePhysicalByAsync(u => u.RoleId == id);
            await _roleOrgRepo.DeletePhysicalByAsync(u => u.RoleId == id);
            // 批 BH:字段权限授权行级联(参照 DeleteRolTableByRoleId;内含受管声明重算 + 受管集合缓存失效)
            await _roleTableService.DeleteByRoleIdAsync(id);
            await _roleRepo.DeleteByIdAsync(id);

            foreach (var userId in userIds)
            {
                await _menuPermService.InvalidateUserAsync(userId);
                // 数据权限缓存同批失效(task_013 契约 5:角色删除 → 该角色下用户最大范围收敛结果变化)
                await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, userId);
            }
        }

        /// <summary>
        /// 角色授权(全量替换):清空该角色现有授权 → 按传入菜单 Id 集重挂(菜单必须存在,防悬挂引用)
        /// → 该角色下全部用户权限缓存变更即失效(契约 §2:不靠 7 天过期兜底)。
        /// </summary>
        public async Task GrantMenuAsync(GrantMenuInput input)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == input.RoleId);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var menuIds = input.MenuIds is { Count: > 0 }
                ? input.MenuIds!.Distinct().ToList()
                : new List<long>();

            if (menuIds.Count > 0)
            {
                var existingMenuIds = new HashSet<long>(
                    await _menuRepo.AsQueryable().Where(u => menuIds.Contains(u.Id)).Select(u => u.Id).ToListAsync());
                var missing = menuIds.Where(id => !existingMenuIds.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    throw AppException.With(ErrorCodes.RoleMenuNotFound, "ids", string.Join(",", missing));
                }
            }

            // 全量替换:先清后挂(中间表物理删,无审计价值)
            await _roleMenuRepo.DeletePhysicalByAsync(u => u.RoleId == input.RoleId);
            foreach (var menuId in menuIds)
            {
                await _roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = input.RoleId, MenuId = menuId });
            }

            await _menuPermService.InvalidateRoleAsync(input.RoleId);
        }

        /// <summary>
        /// 接口黑名单授权(批 A 2.10,全量替换):清空该角色现有黑名单 → 按传入权限标识集重挂
        /// → 该角色下全部用户权限缓存变更即失效(有效权限集 = 授权按钮 − 黑名单,缓存须刷新)。
        /// 黑名单为空集(传空/null)即"解除全部禁用"。
        /// </summary>
        public async Task GrantApiAsync(GrantApiInput input)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == input.RoleId);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var apiList = input.ApiList is { Count: > 0 }
                ? input.ApiList!.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList()
                : new List<string>();

            // 全量替换:先清后挂(中间表物理删,无审计价值)
            await _roleApiRepo.DeletePhysicalByAsync(u => u.RoleId == input.RoleId);
            foreach (var api in apiList)
            {
                await _roleApiRepo.InsertAsync(new SysRoleApi { RoleId = input.RoleId, Permission = api });
            }

            await _menuPermService.InvalidateRoleAsync(input.RoleId);
        }

        /// <summary>角色接口黑名单查询(权限标识列表;软删行自动过滤)。</summary>
        public async Task<List<string>> GetApiListAsync(long roleId)
        {
            return await _roleApiRepo.AsQueryable()
                .Where(u => u.RoleId == roleId && u.Permission != null)
                .Select(u => u.Permission!)
                .Distinct()
                .ToListAsync();
        }

        /// <summary>
        /// 角色菜单授权回读(批 H 2.4):该角色已授权菜单 Id 集合——前端菜单树勾选态的唯一数据源
        /// (与 grantMenu 全量替换对偶:授权写入集 = 回读集)。
        /// </summary>
        public async Task<List<long>> GetMenuListAsync(long roleId)
        {
            return await _roleMenuRepo.AsQueryable()
                .Where(u => u.RoleId == roleId)
                .Select(u => u.MenuId)
                .Distinct()
                .ToListAsync();
        }

        /// <summary>
        /// 复制角色(批 H 2.2;参照 AdminNetPro CopyRole 移植):源角色快照为新行(Code/Name 追加 " - copy",
        /// 新雪花 Id,清审计字段)→ 全量复制四张关联表(sys_role_menu/sys_role_api/sys_role_org/sys_role_table)。
        /// 批 BH:补 sys_role_table 复制(参照 CopyRolTableByRoleId;2.9 已于批 AH 立项,原"未立项"注释过时)——
        /// 此前复制带字段权限的角色会静默丢列白名单(副本对受管表全裁空,权限收窄且无提示)。
        /// 新 Code 撞已存在编码 → AppException(参照未处理;本仓 Add 有 Code 唯一校验,保持同口径)。
        /// </summary>
        public async Task<long> CopyRoleAsync(long id)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == id);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var newCode = role.Code + " - copy";
            var newName = role.Name + " - copy";
            if (await _roleRepo.IsAnyAsync(u => u.Code == newCode))
            {
                throw AppException.With(ErrorCodes.RoleCodeExistsUpdate, "code", newCode);
            }

            var newRole = new SysRole
            {
                Name = newName,
                Code = newCode,
                Remark = role.Remark,
                DataScope = role.DataScope,
                // SysFlag 复制源值:内置角色复制出内置副本(种子口径一致);非内置照抄 false
                SysFlag = role.SysFlag
            };
            await _roleRepo.InsertAsync(newRole);

            // 三张关联表全量复制(新角色无存量行,直接插入;审计字段由 AOP 填)
            var menus = await _roleMenuRepo.AsQueryable().Where(u => u.RoleId == id).ToListAsync();
            foreach (var m in menus)
            {
                await _roleMenuRepo.InsertAsync(new SysRoleMenu { RoleId = newRole.Id, MenuId = m.MenuId });
            }

            var apis = await _roleApiRepo.AsQueryable().Where(u => u.RoleId == id).ToListAsync();
            foreach (var a in apis)
            {
                await _roleApiRepo.InsertAsync(new SysRoleApi { RoleId = newRole.Id, Permission = a.Permission });
            }

            var orgs = await _roleOrgRepo.AsQueryable().Where(u => u.RoleId == id).ToListAsync();
            foreach (var o in orgs)
            {
                await _roleOrgRepo.InsertAsync(new SysRoleOrg { RoleId = newRole.Id, OrgId = o.OrgId });
            }

            // 批 BH:字段权限授权行(第 4 张关联表;参照 CopyRolTableByRoleId 移植)
            await _roleTableService.CopyByRoleIdAsync(id, newRole.Id);

            return newRole.Id;
        }

        /// <summary>
        /// 数据范围授权(批 B B2 Define 自定义范围,全量替换):角色存在校验 → 机构存在校验(防悬挂引用)
        /// → 越权防护(所选机构须在操作者自身数据范围集内;超管 999 / All 范围豁免)
        /// → 先清后挂(中间表物理删)→ 变更即失效该角色下全部用户数据权限缓存(机构集/最大范围收敛结果变化)。
        /// 传空集/null = 清空全部自定义机构(该角色自定义范围收敛为空集 fail-closed)。
        /// </summary>
        public async Task GrantOrgAsync(GrantOrgInput input)
        {
            var role = await _roleRepo.GetFirstAsync(u => u.Id == input.RoleId);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var orgIds = input.OrgIds is { Count: > 0 }
                ? input.OrgIds!.Distinct().ToList()
                : new List<long>();

            // 机构存在校验(防悬挂引用)
            if (orgIds.Count > 0)
            {
                var existingOrgIds = new HashSet<long>(
                    await _orgRepo.AsQueryable().Where(u => orgIds.Contains(u.Id)).Select(u => u.Id).ToListAsync());
                var missing = orgIds.Where(id => !existingOrgIds.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    throw AppException.With(ErrorCodes.RoleOrgNotFound, "ids", string.Join(",", missing));
                }
            }

            // 越权防护(批 B B2):超管(999)豁免;否则所选机构须落在操作者自身数据范围集内
            // (All 范围 = 可见全部机构 → 豁免;Self/无角色 = 空集 → 非空授权 fail-closed 拒绝;操作者身份不可解析 → 拒绝)
            if (orgIds.Count > 0)
            {
                // 操作者身份经 UserManager 门面(批 D 1.14:超管判定/Claim 解析单点收敛)
                long? operatorId = _userManager.UserId;

                if (!_userManager.IsSuperAdmin)
                {
                    if (operatorId is null)
                    {
                        throw AppException.Of(ErrorCodes.RoleOperatorUnresolved, args: null, code: 400);
                    }

                    var (opScope, opOrgs) = await _orgService.GetUserOrgScopeAsync(operatorId.Value);
                    if (opScope != DataScopeEnum.All)
                    {
                        var notAllowed = orgIds.Where(id => !opOrgs.Contains(id)).ToList();
                        if (notAllowed.Count > 0)
                        {
                            throw AppException.Of(ErrorCodes.RoleOrgOutOfScope, args: null, code: 400);
                        }
                    }
                }
            }

            // 全量替换:先清后挂(中间表物理删,无审计价值)
            await _roleOrgRepo.DeletePhysicalByAsync(u => u.RoleId == input.RoleId);
            foreach (var orgId in orgIds)
            {
                await _roleOrgRepo.InsertAsync(new SysRoleOrg { RoleId = input.RoleId, OrgId = orgId });
            }

            // 变更即失效该角色下全部用户数据权限缓存(机构集/最大范围收敛结果变化,不靠 TTL 兜底)
            var userIds = await _userRoleRepo.AsQueryable()
                .Where(u => u.RoleId == input.RoleId)
                .Select(u => u.UserId)
                .ToListAsync();
            foreach (var userId in userIds.Distinct())
            {
                await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, userId);
            }
        }

        /// <summary>角色数据范围授权查询(机构 Id 列表;空集 = 未授自定义机构,软删行自动过滤)。</summary>
        public async Task<List<long>> GetOrgListAsync(long roleId)
        {
            return await _roleOrgRepo.AsQueryable()
                .Where(u => u.RoleId == roleId)
                .Select(u => u.OrgId)
                .Distinct()
                .ToListAsync();
        }
    }
}
