using AdminCore.Core.Entities;
using AdminCore.Core;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.Menu
{
    /// <summary>
    /// 菜单按钮权限集服务(task_022 契约 2 + 批 A 2.10 黑名单):用户 → SysUserRole → SysRoleMenu → SysMenu(Type=3 按钮)
    /// Permission 集合解析,再扣减角色接口黑名单(SysRoleApi)得有效权限集 + Redis 缓存
    /// (键 KeyUserApiPerm:{userId},TTL 7 天)+ 角色/菜单/黑名单变更精准失效。
    /// </summary>
    /// <remarks>
    /// 008 UserApiPermissionProvider(路由白名单模型)的接替者:查询链路相同,消费方由
    /// PermissionMiddleware 的路由比对改为 MenuPermFilter 的 [MenuPerm] 标注比对,缓存键按契约换用 KeyUserApiPerm 前缀。
    /// 负缓存:无角色/无授权用户同样落空集缓存——避免这类请求反复走链路查询。
    /// 有效集口径(批 A 2.10):缓存落库即"角色授权按钮权限 − 角色黑名单"的有效集;黑名单变更经
    /// SysRoleService.GrantApiAsync 调 InvalidateRoleAsync 失效,不靠 TTL 兜底。
    /// 仓储查询自动带软删过滤与租户过滤(基座:当前租户 Claim 由 PermissionMiddleware 每请求设定),本类不重复处理。
    /// </remarks>
    [Service]
    public sealed class MenuPermService : IMenuPermService
    {
        /// <summary>权限缓存 TTL(对齐 008 口径:7 天;变更精准失效为主机制,TTL 仅作泄漏兜底)。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        private readonly ISqlSugarRepository<SysUserRole> _userRoleRepo;
        private readonly ISqlSugarRepository<SysRoleMenu> _roleMenuRepo;
        private readonly ISqlSugarRepository<SysMenu> _menuRepo;
        private readonly ISqlSugarRepository<SysRoleApi> _roleApiRepo;
        private readonly ICacheService _cache;

        public MenuPermService(
            ISqlSugarRepository<SysUserRole> userRoleRepo,
            ISqlSugarRepository<SysRoleMenu> roleMenuRepo,
            ISqlSugarRepository<SysMenu> menuRepo,
            ISqlSugarRepository<SysRoleApi> roleApiRepo,
            ICacheService cache)
        {
            _userRoleRepo = userRoleRepo;
            _roleMenuRepo = roleMenuRepo;
            _menuRepo = menuRepo;
            _roleApiRepo = roleApiRepo;
            _cache = cache;
        }

        /// <summary>
        /// 解析按钮权限集:缓存命中直接返回;未命中走 角色→授权→按钮 链路查询并写缓存(含空集负缓存)。
        /// </summary>
        public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(long userId)
        {
            var cacheKey = CacheConst.KeyUserApiPerm + userId;
            var cached = await _cache.GetAsync<List<string>>(cacheKey);
            if (cached is not null)
            {
                return cached;
            }

            // 链路查询:用户角色 → 角色授权菜单 → 按钮(Type=3)权限标识(软删行由仓储自动过滤)
            var roleIds = await _userRoleRepo.AsQueryable()
                .Where(u => u.UserId == userId)
                .Select(u => u.RoleId)
                .ToListAsync();

            List<string> permissions = new();
            if (roleIds.Count > 0)
            {
                var menuIds = await _roleMenuRepo.AsQueryable()
                    .Where(u => roleIds.Contains(u.RoleId))
                    .Select(u => u.MenuId)
                    .ToListAsync();

                // Permission 列可空 → 投影类型 string?,过滤空值后收敛为 List<string>
                if (menuIds.Count > 0)
                {
                    // WHY u.Id:SysMenu 主键即 Id(关联表 SysRoleMenu.MenuId 指向它)
                    var rows = await _menuRepo.AsQueryable()
                        .Where(u => menuIds.Contains(u.Id) && u.Type == 3 && u.Permission != null)
                        .Select(u => u.Permission)
                        .ToListAsync();

                    permissions = rows
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p!)
                        .Distinct()
                        .ToList();

                    // 批 A 2.10:扣减角色接口黑名单——有效权限集 = 授权按钮权限 − 黑名单(忽略大小写,与 Filter 比对口径一致)
                    var blacklistRows = await _roleApiRepo.AsQueryable()
                        .Where(u => roleIds.Contains(u.RoleId) && u.Permission != null)
                        .Select(u => u.Permission)
                        .ToListAsync();

                    var blacklist = blacklistRows
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p!)
                        .ToList();

                    if (blacklist.Count > 0)
                    {
                        permissions = permissions.Except(blacklist, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            }

            await _cache.SetAsync(cacheKey, permissions, CacheTtl);
            return permissions;
        }

        /// <summary>失效单用户权限缓存(用户角色变更/用户删除后调用——变更即失效,不靠 TTL 兜底)。</summary>
        public Task InvalidateUserAsync(long userId)
        {
            return _cache.RemoveAsync(CacheConst.KeyUserApiPerm + userId);
        }

        /// <summary>
        /// 失效某角色下全部用户权限缓存(角色授权变更/角色删除后调用——契约:变更即失效)。
        /// </summary>
        public async Task InvalidateRoleAsync(long roleId)
        {
            var userIds = await _userRoleRepo.AsQueryable()
                .Where(u => u.RoleId == roleId)
                .Select(u => u.UserId)
                .ToListAsync();

            foreach (var userId in userIds.Distinct())
            {
                await _cache.RemoveAsync(CacheConst.KeyUserApiPerm + userId);
            }
        }

        /// <summary>
        /// 失效持有某菜单的全部用户权限缓存(菜单权限标识变更/菜单删除后调用:
        /// 菜单 → 引用该菜单的角色 → 角色下用户,两段扩散覆盖全部受影响用户)。
        /// </summary>
        public async Task InvalidateMenuAsync(long menuId)
        {
            var roleIds = await _roleMenuRepo.AsQueryable()
                .Where(u => u.MenuId == menuId)
                .Select(u => u.RoleId)
                .ToListAsync();

            foreach (var roleId in roleIds.Distinct())
            {
                await InvalidateRoleAsync(roleId);
            }
        }
    }
}
