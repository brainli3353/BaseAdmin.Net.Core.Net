using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Menu.Dto;
using AdminCore.Core.Attributes;
using AdminCore.Core.Repository;

namespace AdminCore.Core.Services.Menu
{
    /// <summary>
    /// 菜单/按钮管理(契约 §7 菜单域 + 批 A 2.3 登录菜单树):Tree(管理端全量三级树) /
    /// GetLoginMenuTree(登录菜单树:超管全量剔按钮,普通用户按角色裁剪 + 剔按钮) / 新建 / 更新 / 删除(有子节点禁删)。
    /// 按钮( Type=3 )的 Permission 是接口权限键——前端菜单渲染与后端 403 校验共用同一份数据(一处配置两端生效)。
    /// </summary>
    [Service]
    public class SysMenuService
    {
        private readonly ISqlSugarRepository<SysMenu> _menuRepo;
        private readonly ISqlSugarRepository<SysRoleMenu> _roleMenuRepo;
        private readonly ISqlSugarRepository<SysUserRole> _userRoleRepo;
        private readonly IMenuPermService _menuPermService;

        public SysMenuService(
            ISqlSugarRepository<SysMenu> menuRepo,
            ISqlSugarRepository<SysRoleMenu> roleMenuRepo,
            ISqlSugarRepository<SysUserRole> userRoleRepo,
            IMenuPermService menuPermService)
        {
            _menuRepo = menuRepo;
            _roleMenuRepo = roleMenuRepo;
            _userRoleRepo = userRoleRepo;
            _menuPermService = menuPermService;
        }

        /// <summary>
        /// 菜单全树(按 OrderNo、Id 排序后内存组树;管理端数据量级小,单次全量查询足够,避免 N+1)。
        /// 租户过滤在此自动生效(基座:当前租户 Claim 过滤 sys_menu;超管豁免——见 PermissionMiddleware)。
        /// </summary>
        public async Task<List<MenuOutput>> TreeAsync()
        {
            // 全量拉取(软删过滤自动);OrderBy 保证同级顺序稳定
            var all = await _menuRepo.AsQueryable()
                .OrderBy(u => u.OrderNo)
                .OrderBy(u => u.Id)
                .ToListAsync();

            var nodes = all.ToDictionary(
                u => u.Id,
                    u => new MenuOutput(u.Id, u.Pid, u.Name, u.Title, u.Type, u.Permission, u.OrderNo, u.Icon, u.Status, u.IsKeepAlive, new List<MenuOutput>()));

            var roots = new List<MenuOutput>();
            var orphans = new List<MenuOutput>(); // 悬挂节点兜底(父行被物理清掉时挂顶级,防组树丢行)

            foreach (var node in nodes.Values)
            {
                if (node.Pid == 0)
                {
                    roots.Add(node);
                }
                else if (nodes.TryGetValue(node.Pid, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    orphans.Add(node);
                }
            }

            roots.AddRange(orphans);
            return roots;
        }

        /// <summary>
        /// 登录菜单树(批 A 2.3:前端导航渲染用,与 TreeAsync 管理端全量树区分):
        /// 超管(AccountType=999)全量;普通用户按角色授权菜单集裁剪;两种口径均剔除按钮(Type=3)——
        /// 按钮只参与接口权限校验,不进前端菜单树。
        /// 批 CH G28:停用(Status=1)节点及其**整棵子树**不进登录树(参照 SysMenuService.GetLoginMenuTree 同语义——
        /// 参照在可查询层过滤 Status=Enable,父行出集后子行成孤儿被 ToTreeAsync 丢弃 ⇒ 整子树消失);
        /// 管理端 TreeAsync 不过滤(需看见停用节点才能再启用)。
        /// WHY 普通用户可见集走角色→SysRoleMenu 菜单 Id(非权限键):菜单树要展示目录/菜单节点,
        /// 而按钮权限键仅 Type=3 节点有,维度不同,不能复用 MenuPermService 的按钮权限集。
        /// 挂载一致性:授权通常为整棵子树(含父目录),孤立节点(父未授权)兜底挂顶级,防丢行(同 TreeAsync 口径)——
        /// 该兜底只作用于"父未授权",不作用于"父被停用"(停用子树先行剔除,不触发浮动)。
        /// </summary>
        public async Task<List<MenuOutput>> GetLoginMenuTreeAsync(long userId, int accountType)
        {
            // 全量拉取(与 TreeAsync 同源同排序,保证顺序稳定)
            var all = await _menuRepo.AsQueryable()
                .OrderBy(u => u.OrderNo)
                .OrderBy(u => u.Id)
                .ToListAsync();

            // 批 CH G28:停用节点+整棵子树的 Id 集(先行剔除,早于授权裁剪与孤儿子兜底)
            var hiddenIds = ComputeHiddenSubtreeIds(all);

            // 可见集:超管 = 全部非按钮节点;普通用户 = 角色授权菜单 Id 集
            HashSet<long> allowedIds = accountType == 999
                ? new HashSet<long>(all.Where(u => u.Type != 3).Select(u => u.Id))
                : new HashSet<long>(await GetMenuIdListAsync(userId));

            // 过滤:非按钮(Type!=3)且属可见集且不在停用子树内;投影为输出节点
            var nodes = all
                .Where(u => u.Type != 3 && allowedIds.Contains(u.Id) && !hiddenIds.Contains(u.Id))
                .ToDictionary(
                    u => u.Id,
                u => new MenuOutput(u.Id, u.Pid, u.Name, u.Title, u.Type, u.Permission, u.OrderNo, u.Icon, u.Status, u.IsKeepAlive, new List<MenuOutput>()));

            var roots = new List<MenuOutput>();
            var orphans = new List<MenuOutput>();
            foreach (var node in nodes.Values)
            {
                if (node.Pid == 0)
                {
                    roots.Add(node);
                }
                else if (nodes.TryGetValue(node.Pid, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    orphans.Add(node);
                }
            }

            roots.AddRange(orphans);
            return roots;
        }

        /// <summary>
        /// 用户可见菜单 Id 集(批 A 2.3:角色 → SysRoleMenu 菜单 Id,含目录/菜单/按钮;供登录菜单树裁剪)。
        /// WHY 不另设缓存:菜单树量级小(数百节点),两段链路查询开销可忽略;变更失效已由
        /// KeyUserApiPerm 权限缓存承担,不引入平行缓存键(约定 #11 复用优先)。
        /// </summary>
        public async Task<List<long>> GetMenuIdListAsync(long userId)
        {
            var roleIds = await _userRoleRepo.AsQueryable()
                .Where(u => u.UserId == userId)
                .Select(u => u.RoleId)
                .ToListAsync();

            if (roleIds.Count == 0)
            {
                return new List<long>();
            }

            return await _roleMenuRepo.AsQueryable()
                .Where(u => roleIds.Contains(u.RoleId))
                .Select(u => u.MenuId)
                .Distinct()
                .ToListAsync();
        }

        /// <summary>
        /// 设置菜单状态(批 CH G28):0 启用/1 停用,部分更新只动 Status 列。
        /// 停用生效面=登录菜单树(该节点+整子树消失);管理端全量树与按钮权限集(API 403 校验)不受影响——
        /// 与参照同口径(参照仅登录树可查询层过滤 Status),停用按钮≠回收 API 权限。
        /// </summary>
        public async Task SetStatusAsync(long id, int status)
        {
            if (status is not (0 or 1))
            {
                throw AppException.Of(ErrorCodes.MenuStatusInvalid, code: 400);
            }

            var menu = await _menuRepo.GetFirstAsync(u => u.Id == id);
            if (menu is null)
            {
                throw AppException.Of(ErrorCodes.MenuNotFound);
            }

            menu.Status = status;
            await _menuRepo.UpdateAsync(menu);
        }

        /// <summary>
        /// 计算停用(Status=1)节点及其全部后代的 Id 集(批 CH G28;登录树剔除用)。
        /// 迭代 BFS(菜单树量级小,防深链递归);只认 Status==1 为停用(SetStatus 已限 0/1 白名单)。
        /// </summary>
        private static HashSet<long> ComputeHiddenSubtreeIds(List<SysMenu> all)
        {
            var childrenByPid = all
                .Where(u => u.Pid != 0)
                .GroupBy(u => u.Pid)
                .ToDictionary(g => g.Key, g => g.Select(u => u.Id).ToList());

            var hidden = new HashSet<long>();
            var stack = all.Where(u => u.Status == 1).Select(u => u.Id).ToList();
            for (var i = 0; i < stack.Count; i++)
            {
                var id = stack[i];
                if (!hidden.Add(id))
                {
                    continue;
                }

                if (childrenByPid.TryGetValue(id, out var children))
                {
                    stack.AddRange(children);
                }
            }

            return hidden;
        }

        /// <summary>
        /// 新建菜单:类型白名单 1/2/3;按钮必填 Permission;Pid 非 0 时父节点必须存在;
        /// Name 全局唯一／父节点禁按钮／同级判重(批 BI 复审,与参照 D4009/D4010/D4000 同口径)。
        /// </summary>
        public async Task<long> AddAsync(AddMenuInput input)
        {
            ValidateTypeAndPermission(input.Type, input.Permission);
            await ValidateNewNodeAsync(0, input.Pid, input.Type, input.Name, input.Title, input.Permission);

            var menu = new SysMenu
            {
                Pid = input.Pid,
                Name = input.Name,
                Title = input.Title,
                Type = input.Type,
                Permission = input.Permission,
                OrderNo = input.OrderNo ?? 0,
                Icon = input.Icon,
                // 0=不缓存;其余(含缺省 null)一律缓存——与加列前"叶子菜单一律缓存"行为一致
                IsKeepAlive = input.IsKeepAlive == 0 ? 0 : 1
            };

            await _menuRepo.InsertAsync(menu);
            return menu.Id;
        }

        /// <summary>
        /// 更新菜单:校验同 Add;另禁自环(Pid 不得改挂为自身——树成环则组树死循环)。
        /// 注:挂到"自己的后代"(跨级成环)参照同样不检查(同口径留痕,批 BI)。
        /// </summary>
        public async Task UpdateAsync(UpdateMenuInput input)
        {
            var menu = await _menuRepo.GetFirstAsync(u => u.Id == input.Id);
            if (menu is null)
            {
                throw AppException.Of(ErrorCodes.MenuNotFound);
            }

            if (input.Pid == input.Id)
            {
                throw AppException.Of(ErrorCodes.MenuParentSelf);
            }

            ValidateTypeAndPermission(input.Type, input.Permission);
            await ValidateNewNodeAsync(input.Id, input.Pid, input.Type, input.Name, input.Title, input.Permission);

            menu.Pid = input.Pid;
            menu.Name = input.Name;
            menu.Title = input.Title;
            menu.Type = input.Type;
            menu.Permission = input.Permission;
            menu.OrderNo = input.OrderNo ?? menu.OrderNo;
            menu.Icon = input.Icon;
            // 部分更新:null=不改;仅接受 0/1(非 0 一律归一为 1,避免脏值让前端判定歧义)
            if (input.IsKeepAlive is not null)
            {
                menu.IsKeepAlive = input.IsKeepAlive == 0 ? 0 : 1;
            }
            await _menuRepo.UpdateAsync(menu);

            // 按钮权限标识/类型可能变更 → 持有该菜单的角色下全部用户权限缓存变更即失效(task_022 契约:角色/菜单变更删缓存)
            await _menuPermService.InvalidateMenuAsync(menu.Id);
        }

        /// <summary>
        /// 删除菜单:有子节点禁删(契约 §7;防树断裂——先删子节点)。缓存失效先行(需中间表行尚存才能查到引用角色),
        /// 再软删主记录 + 级联物理清角色授权引用(task_022 契约:菜单变更即失效,残留缓存中的旧键不能留)。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            if (await _menuRepo.IsAnyAsync(u => u.Pid == id))
            {
                throw AppException.Of(ErrorCodes.MenuHasChildren);
            }

            // 失效先行:InvalidateMenuAsync 内部按 MenuId 查角色引用,须在中间表物理删之前执行
            await _menuPermService.InvalidateMenuAsync(id);
            await _menuRepo.DeleteByIdAsync(id);

            // 级联清理角色授权引用(中间表物理删,无审计价值)
            await _roleMenuRepo.DeletePhysicalByAsync(u => u.MenuId == id);
        }

        /// <summary>
        /// 新节点联合校验(Add/Update 共用,批 BI 复审——参照口径 D4009/D4010/D4000):
        /// ① Name 全局唯一——Name 是前端消费的路由标识(menu store:逐级拼 path 段 + Vue Router 路由名),
        ///    重名 = 命名路由重复 + 组件解析歧义;空名跳过(参照同口径);软删过滤自动生效 ⇒ 软删后同名重建允许;
        /// ② 父节点禁按钮——按钮是叶子;子挂按钮下时登录菜单树(剔按钮)会把子静默浮动到顶级
        ///    (管理端嵌套 / 登录端浮动 = 语义断裂);
        /// ③ 同级判重——非按钮 Title+Type 同父重复 / 按钮 Permission 同父重复(权限集语义下重复键冗余)。
        /// selfId=0 表新建(雪花 Id 恒非 0,排除条件无副作用,统一两路)。
        /// </summary>
        private async Task ValidateNewNodeAsync(long selfId, long pid, int type, string? name, string? title, string? permission)
        {
            if (!string.IsNullOrWhiteSpace(name)
                && await _menuRepo.IsAnyAsync(u => u.Id != selfId && u.Name == name))
            {
                throw AppException.Of(ErrorCodes.MenuNameExists);
            }

            if (pid != 0)
            {
                var parent = await _menuRepo.GetFirstAsync(u => u.Id == pid);
                if (parent is null)
                {
                    throw AppException.Of(ErrorCodes.MenuParentNotFound);
                }

                if (parent.Type == 3)
                {
                    throw AppException.Of(ErrorCodes.MenuParentIsButton);
                }
            }

            if (type == 3)
            {
                if (await _menuRepo.IsAnyAsync(u => u.Id != selfId && u.Pid == pid && u.Permission == permission))
                {
                    throw AppException.Of(ErrorCodes.MenuDuplicate);
                }
            }
            else if (await _menuRepo.IsAnyAsync(u => u.Id != selfId && u.Pid == pid && u.Type == type && u.Title == title))
            {
                throw AppException.Of(ErrorCodes.MenuDuplicate);
            }
        }

        /// <summary>类型/权限标识联合校验:Type 白名单;按钮(Type=3)必填 Permission。</summary>
        private static void ValidateTypeAndPermission(int type, string? permission)
        {
            if (type is not (1 or 2 or 3))
            {
                throw AppException.Of(ErrorCodes.MenuTypeInvalid);
            }

            if (type == 3 && string.IsNullOrWhiteSpace(permission))
            {
                throw AppException.Of(ErrorCodes.MenuPermissionRequired);
            }
        }
    }
}
