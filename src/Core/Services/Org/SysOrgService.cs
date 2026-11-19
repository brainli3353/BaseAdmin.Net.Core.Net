using AdminCore.Core.Attributes;
using AdminCore.Core.Extensions;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Enums;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.Org.Dto;
using AdminCore.Core.Models;
using AdminCore.Core.Repository;
using AdminCore.Core.Security;
using AdminCore.Core.SqlSugar;
using AdminCore.Core.Utils.DynamicQuery;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.Org
{
    /// <summary>
    /// 机构管理(task_019 契约):Page(分页,Name/Code 模糊) / Tree(Pid 内存组树,根 Pid=0)
    /// / Add / Update(部分更新:可空字段不改;改挂载 → 受影响用户数据权限缓存失效) / Delete(有子节点或关联用户拒删 → 400)。
    /// 机构是数据权限枢纽(task_013):sys_user.OrgId 关联 + GetUserOrgScopeAsync 多角色最大范围收敛(缓存 7 天,变更即失效),
    /// 删除前校验关联用户防数据权限孤儿。
    /// </summary>
    [Service]
    public class SysOrgService
    {
        /// <summary>数据范围缓存 TTL(对齐 KeyUserApiPerm 口径:7 天;变更即失效为主机制,TTL 仅作泄漏兜底)。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        private readonly ISqlSugarRepository<SysOrg> _orgRepo;
        private readonly ISqlSugarRepository<SysUser> _userRepo;
        private readonly ISqlSugarRepository<SysRole> _roleRepo;
        private readonly ISqlSugarRepository<SysUserRole> _userRoleRepo;
        private readonly ISqlSugarRepository<SysRoleOrg> _roleOrgRepo;
        private readonly ICacheService _cache;
        private readonly UserManager _userManager; // 批 H 2.5:Tree 裁剪需要当前操作者身份(超管/数据范围)

        public SysOrgService(
            ISqlSugarRepository<SysOrg> orgRepo,
            ISqlSugarRepository<SysUser> userRepo,
            ISqlSugarRepository<SysRole> roleRepo,
            ISqlSugarRepository<SysUserRole> userRoleRepo,
            ISqlSugarRepository<SysRoleOrg> roleOrgRepo,
            ICacheService cache,
            UserManager userManager)
        {
            _orgRepo = orgRepo;
            _userRepo = userRepo;
            _roleRepo = roleRepo;
            _userRoleRepo = userRoleRepo;
            _roleOrgRepo = roleOrgRepo;
            _cache = cache;
            _userManager = userManager;
        }

        /// <summary>
        /// 分页查询(名称/编码模糊,缺省不过滤;Sort、Id 升序,同级顺序稳定)。
        /// 租户过滤在此自动生效(基座:当前租户 Claim 过滤 sys_org;超管豁免——见 PermissionMiddleware)。
        /// </summary>
        public async Task<PageResult<OrgOutput>> PageAsync(PageOrgInput input)
        {
            RefAsync<int> total = 0;
            var list = await _orgRepo.AsQueryable()
                .Where(u => string.IsNullOrWhiteSpace(input.Name) || u.Name!.Contains(input.Name))
                .Where(u => string.IsNullOrWhiteSpace(input.Code) || u.Code!.Contains(input.Code))
                // 批 UX:状态精确过滤(int? Status,缺省 null=不过滤;0 启用/1 禁用)
                .WhereIF(input.Status.HasValue, u => u.Status == input.Status)
                // 批 CI:表头点击排序(orderBy 空=原默认序 Sort asc+Id asc;非空=白名单校验+表达式树排序)
                .ApplyDynamicSort(input.OrderBy, input.Desc, q => q.OrderBy(u => u.Sort).OrderBy(u => u.Id))
                .ToPageListAsync(input, total);
            return new PageResult<OrgOutput>
            {
                Total = total,
                List = list.Select(u => new OrgOutput(u.Id, u.Pid, u.Name, u.Code, u.Sort, u.Remark, u.Status, new List<OrgOutput>())).ToList()
            };
        }

        /// <summary>
        /// 机构全树(Sort、Id 排序后内存组树;根节点 Pid=0;管理端数据量级小,单次全量查询足够,避免 N+1)。
        /// 悬挂节点(父行被物理清掉)兜底挂顶级,防组树丢行(与 SysMenuService.TreeAsync 同口径)。
        /// </summary>
        public async Task<List<OrgOutput>> TreeAsync()
        {
            var all = await _orgRepo.AsQueryable()
                .OrderBy(u => u.Sort)
                .OrderBy(u => u.Id)
                .ToListAsync();

            // 批 H 2.5:非超管机构树裁剪——范围外节点 Disabled=true(前端选择器禁用,防越权挂靠/创建);
            // 超管/全量范围/匿名(测试无 Claims)恒 false。范围经 GetUserOrgScopeAsync 枢纽(缓存命中路径)。
            var disabledSet = new HashSet<long>();
            if (_userManager.IsAuthenticated && _userManager.UserId is > 0 && !_userManager.IsSuperAdmin)
            {
                var (maxScope, orgIds) = await GetUserOrgScopeAsync(_userManager.UserId.Value);
                if (maxScope != DataScopeEnum.All)
                {
                    disabledSet = all.Where(u => !orgIds.Contains(u.Id)).Select(u => u.Id).ToHashSet();
                }
            }

            var nodes = all.ToDictionary(
                u => u.Id,
                u => new OrgOutput(u.Id, u.Pid, u.Name, u.Code, u.Sort, u.Remark, u.Status, new List<OrgOutput>(), disabledSet.Contains(u.Id)));

            var roots = new List<OrgOutput>();
            var orphans = new List<OrgOutput>(); // 悬挂节点兜底

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
        /// 新建机构:Name 必填;Code 提供时校验全表唯一(重复 → 400);Pid 非 0 时父节点必须存在;
        /// 同父同名拒绝(参照 D2002 同口径,批 BJ——Code 已由全表唯一覆盖,严于参照 per-Pid 口径)。
        /// </summary>
        public async Task<long> AddAsync(AddOrgInput input)
        {
            var name = input.Name?.Trim();
            var code = input.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.OrgNameRequired);
            }

            // 批 CG(G29):操作者范围校验——顶级创建仅超管(参照 D2009 同口径);父机构须在范围内(参照 D2003,空集 fail-closed)
            var scope = await GetOperatorOrgScopeAsync();
            if (scope is not null)
            {
                if (input.Pid == 0)
                {
                    throw AppException.Of(ErrorCodes.OrgTopForbidden);
                }

                if (!scope.Contains(input.Pid))
                {
                    throw AppException.Of(ErrorCodes.OrgParentOutOfScope);
                }
            }

            // Code 可空:缺省不参与唯一校验;提供时全表唯一(与字典模块 Code 唯一同口径)
            if (!string.IsNullOrWhiteSpace(code) && await _orgRepo.IsAnyAsync(u => u.Code == code))
            {
                throw AppException.With(ErrorCodes.OrgCodeExists, "code", code, 400);
            }

            if (input.Pid != 0 && await _orgRepo.GetFirstAsync(u => u.Id == input.Pid) is null)
            {
                throw AppException.Of(ErrorCodes.OrgParentNotFound);
            }

            // 同父同名判重(参照 D2002 同口径,批 BJ;机构显示名重复=管理端歧义)
            if (await _orgRepo.IsAnyAsync(u => u.Pid == input.Pid && u.Name == name))
            {
                throw AppException.Of(ErrorCodes.OrgDuplicate);
            }

            var org = new SysOrg
            {
                Pid = input.Pid,
                Name = name,
                Code = string.IsNullOrWhiteSpace(code) ? null : code,
                Sort = input.Sort,
                Remark = input.Remark,
                Status = input.Status
            };

            await _orgRepo.InsertAsync(org);
            return org.Id;
        }

        /// <summary>
        /// 更新机构(部分更新):可空字段 null=不改;改名不改挂载、改挂载不动名称。
        /// 校验:禁自环(Pid 不得改挂为自身)＋禁跨级环(改挂自身后代——参照 D2001 分支 2,批 BJ:
        /// 成环则 GetChildIdListWithSelfById BFS 死循环挂死、组树丢节点);同父同名判重(参照 D2002,按生效值);
        /// Code 提供时唯一(排除自身);Pid 非 0 时父节点必须存在。
        /// </summary>
        public async Task UpdateAsync(UpdateOrgInput input)
        {
            var org = await _orgRepo.GetFirstAsync(u => u.Id == input.Id);
            if (org is null)
            {
                throw AppException.Of(ErrorCodes.OrgNotFound);
            }

            // 批 CG(G29):操作者范围校验——目标须在范围内(参照 D2003,空集 fail-closed);改挂顶级仅超管(参照 D2010)
            var scope = await GetOperatorOrgScopeAsync();
            if (scope is not null)
            {
                if (!scope.Contains(org.Id))
                {
                    throw AppException.Of(ErrorCodes.OrgOutOfScope);
                }

                if (input.Pid is 0)
                {
                    throw AppException.Of(ErrorCodes.OrgTopMoveForbidden);
                }
            }

            var name = input.Name?.Trim();
            // Name 显式提供但全空白 → 拒(null=不改,不报错)
            if (input.Name is not null && string.IsNullOrWhiteSpace(name))
            {
                throw AppException.Of(ErrorCodes.OrgNameRequired);
            }

            var code = input.Code?.Trim();
            // Code 唯一校验排除自身(改回原编码不算冲突)
            if (!string.IsNullOrWhiteSpace(code)
                && await _orgRepo.AsQueryable().AnyAsync(u => u.Code == code && u.Id != input.Id))
            {
                throw AppException.With(ErrorCodes.OrgCodeExists, "code", code, 400);
            }

            // 同父同名判重(参照 D2002 同口径,批 BJ):部分更新语义下按**生效值**(null=保持现值)判,
            // 排除自身(改名回原名/仅改排序不算冲突)
            var effectiveName = string.IsNullOrWhiteSpace(name) ? org.Name : name;
            var effectivePid = input.Pid ?? org.Pid;
            if (effectiveName is not null
                && await _orgRepo.AsQueryable().AnyAsync(u => u.Pid == effectivePid && u.Name == effectiveName && u.Id != input.Id))
            {
                throw AppException.Of(ErrorCodes.OrgDuplicate);
            }

            if (input.Pid is not null)
            {
                if (input.Pid == input.Id)
                {
                    throw AppException.Of(ErrorCodes.OrgParentSelf);
                }

                // 跨级环检查(参照 D2001 分支 2,批 BJ):改挂到"自己的子/孙/…"成环 ⇒
                // GetChildIdListWithSelfById BFS 死循环(挂死)+TreeAsync 组树丢整支(环无根)
                if (input.Pid != 0
                    && (await GetChildIdListWithSelfById(input.Id)).Contains(input.Pid.Value))
                {
                    throw AppException.Of(ErrorCodes.OrgParentDescendant);
                }

                if (input.Pid != 0 && await _orgRepo.GetFirstAsync(u => u.Id == input.Pid) is null)
                {
                    throw AppException.Of(ErrorCodes.OrgParentNotFound);
                }

                // 批 CG(G29):新父须在范围内(跨范围挂载裁决——G29 证据面明示"改挂顶级/跨范围挂载";
                // 严于参照 Update 分支,但 UI 父级选择器已按同一范围裁剪,两侧口径一致)
                if (scope is not null && input.Pid != 0 && !scope.Contains(input.Pid.Value))
                {
                    throw AppException.Of(ErrorCodes.OrgParentOutOfScope);
                }
            }

            // 改挂载判定须取旧值:org.Pid 在下述字段应用后被覆盖,提前捕获(否则 input.Pid != org.Pid 恒假,失效钩子永不触发)
            var oldPid = org.Pid;

            // 仅应用非 null 字段(null=不改,见 UpdateOrgInput WHY)
            if (!string.IsNullOrWhiteSpace(name))
            {
                org.Name = name;
            }

            if (input.Code is not null)
            {
                org.Code = string.IsNullOrWhiteSpace(code) ? null : code;
            }

            if (input.Pid is not null)
            {
                org.Pid = input.Pid.Value;
            }

            if (input.Sort is not null)
            {
                org.Sort = input.Sort.Value;
            }

            if (input.Remark is not null)
            {
                org.Remark = input.Remark;
            }

            if (input.Status is not null)
            {
                org.Status = input.Status.Value;
            }

            await _orgRepo.UpdateAsync(org);

            // 数据权限失效(task_013):改挂载(Pid 变更)→ 新旧锚点子树内用户的可见机构集变化 → 变更即失效。
            // 受影响集 = OrgId 落在 旧父机构子树 ∪ 新父机构子树 的用户(顶层 Pid=0 无公共父 → 以机构自身为锚,宁多勿漏);
            // 被迁移机构自身子树内用户不受影响(其范围集 = 自己机构的子树,不随父级迁移变化)
            if (input.Pid is not null && input.Pid != oldPid)
            {
                var oldAnchorIds = oldPid == 0
                    ? new List<long> { org.Id }
                    : await GetChildIdListWithSelfById(oldPid);
                var newAnchorIds = input.Pid == 0
                    ? new List<long> { org.Id }
                    : await GetChildIdListWithSelfById(input.Pid.Value);

                // OrgId 可空:未挂机构用户的范围集不随机构树迁移变化 → 排除(null 不参与 Contains)
                var affectedUserIds = await _userRepo.AsQueryable()
                    .Where(u => u.OrgId != null && (oldAnchorIds.Contains(u.OrgId.Value) || newAnchorIds.Contains(u.OrgId.Value)))
                    .Select(u => u.Id)
                    .ToListAsync();

                foreach (var affectedUserId in affectedUserIds.Distinct())
                {
                    await SqlSugarFilter.DeleteUserOrgCacheAsync(_cache, affectedUserId);
                }
            }
        }

        /// <summary>
        /// 删除机构:有子节点或关联用户(sys_user.OrgId 指向本机构)拒删 → 400。
        /// WHY 关联用户校验:机构是数据权限枢纽(013 ORM 拦截读 sys_user.OrgId),
        /// 删机构前须确保无用户挂靠,否则用户数据权限集合出现孤儿机构引用。
        /// 软删口径(DeleteByIdAsync → IsDelete=true,物理行保留,审计可追溯)。
        /// </summary>
        public async Task DeleteAsync(long id)
        {
            var org = await _orgRepo.GetFirstAsync(u => u.Id == id);
            if (org is null)
            {
                throw AppException.Of(ErrorCodes.OrgNotFound);
            }

            // 批 CG(G29):操作者范围校验——目标须在范围内(参照 D2003,空集 fail-closed)
            var scope = await GetOperatorOrgScopeAsync();
            if (scope is not null && !scope.Contains(org.Id))
            {
                throw AppException.Of(ErrorCodes.OrgOutOfScope);
            }

            // 有子节点拒删(防树断裂——先删子节点)
            if (await _orgRepo.IsAnyAsync(u => u.Pid == id))
            {
                throw AppException.Of(ErrorCodes.OrgHasChildren, args: null, code: 400);
            }

            // 关联用户拒删(数据权限枢纽防护)
            if (await _userRepo.IsAnyAsync(u => u.OrgId == id))
            {
                throw AppException.Of(ErrorCodes.OrgHasUsers, args: null, code: 400);
            }

            await _orgRepo.DeleteByIdAsync(id);
        }

        /// <summary>
        /// 用户机构数据范围(task_013 契约 3,GetUserOrgIdList 超集——契约只要求机构集,此处连同收敛后的最大范围一并返回,
        /// 供 PermissionMiddleware → SqlSugarFilter 一行装配):
        /// 多角色最大范围收敛(枚举值小 = 范围大,Min)→ (最大范围, 机构集)。
        /// 机构集语义:全部(1) → 空集(不过滤);仅本人(4) → 空集([OwnerUser] 路径);
        /// 本部门(3) → [用户.OrgId];本部门及以下(2) → 用户.OrgId 子树(含自身);
        /// 自定义(5) → 该角色 SysRoleOrg 授权机构集(批 B B2;多角色 Define 并集,无授权行 = 空集 fail-closed)。
        /// 无角色用户 → (仅本人, 空集)。
        /// 缓存:KeyRoleMaxDataScope:{userId} + KeyUserOrg:{userId}(7 天,空集亦负缓存,同 KeyUserApiPerm 口径);
        /// 变更即失效(SqlSugarFilter.DeleteUserOrgCacheAsync,两键同批)。
        /// </summary>
        public async Task<(DataScopeEnum MaxScope, List<long> OrgIds)> GetUserOrgScopeAsync(long userId)
        {
            var scopeKey = CacheConst.KeyRoleMaxDataScope + userId;
            var orgKey = CacheConst.KeyUserOrg + userId;

            // 缓存命中(两键同批读:任一缺失即全量重算并同批写回,防范围与机构集错配)。
            // WHY 用 GetAsync<long?>:ICacheService.GetAsync<T> 的 T? 对无约束 T 仅作注解——T=long 时
            // 缺失键返回 default(long)=0 无法判存在;T=long? 时缺失返 null,与写侧 (long) 序列化值兼容。
            var cachedScope = await _cache.GetAsync<long?>(scopeKey);
            if (cachedScope is not null)
            {
                var cachedOrgs = await _cache.GetAsync<List<long>>(orgKey) ?? new List<long>();
                return ((DataScopeEnum)cachedScope.Value, cachedOrgs);
            }

            // 收敛计算:用户(所属机构)+ 全部角色 → 最大范围(Min)+ 机构集并集
            var user = await _userRepo.GetFirstAsync(u => u.Id == userId);
            var roleIds = await _userRoleRepo.AsQueryable()
                .Where(u => u.UserId == userId)
                .Select(u => u.RoleId)
                .ToListAsync();
            var roles = roleIds.Count > 0
                ? await _roleRepo.AsQueryable().Where(u => roleIds.Contains(u.Id)).ToListAsync()
                : new List<SysRole>();

            var maxScope = DataScopeEnum.Self; // 无角色 = 仅本人(最小权限起步)
            var orgIds = new List<long>();
            if (roles.Count > 0)
            {
                maxScope = roles.Min(r => (DataScopeEnum)r.DataScope);
                foreach (var role in roles)
                {
                    switch ((DataScopeEnum)role.DataScope)
                    {
                        case DataScopeEnum.Org:
                            if (user?.OrgId is long orgId)
                            {
                                orgIds.Add(orgId);
                            }

                            break;

                        case DataScopeEnum.OrgAndChild:
                            if (user?.OrgId is long childOrgId)
                            {
                                orgIds.AddRange(await GetChildIdListWithSelfById(childOrgId));
                            }

                            break;

                        // 自定义(5):读该角色的角色-机构授权集(批 B B2,替代原"授权表未建 → 空集 fail-closed")
                        case DataScopeEnum.Define:
                            orgIds.AddRange(await _roleOrgRepo.AsQueryable()
                                .Where(u => u.RoleId == role.Id)
                                .Select(u => u.OrgId)
                                .ToListAsync());

                            break;

                        // All:不贡献机构集(不过滤);Self:[OwnerUser] 路径
                    }
                }
            }

            var distinctOrgIds = orgIds.Distinct().ToList();

            await _cache.SetAsync(scopeKey, (long)maxScope, CacheTtl);
            await _cache.SetAsync(orgKey, distinctOrgIds, CacheTtl);
            return (maxScope, distinctOrgIds);
        }

        /// <summary>
        /// 操作者机构范围集(批 CG,G29 闭合;参照 Admin.NET D2009/D2003/D2010 移植 + G29 裁决补强"跨范围挂载"):
        /// 返回 null = 放行(未认证/超管/All 范围——All 语义不过滤,与 TreeAsync 裁剪豁免同口径);
        /// 非 null = 管理范围集,经 GetUserOrgScopeAsync 枢纽复用(Org→[OrgId] / OrgAndChild→子树 / Define→SysRoleOrg 并集 / Self→空集),
        /// 缓存命中路径(与批 H 2.5 树裁剪同一范围定义,UI 禁用集=API 放行集,两侧口径一致);空集=fail-closed(无范围=API 层无机构操作权)。
        /// </summary>
        private async Task<HashSet<long>?> GetOperatorOrgScopeAsync()
        {
            if (!_userManager.IsAuthenticated || _userManager.UserId is not > 0 || _userManager.IsSuperAdmin)
            {
                return null;
            }

            var (maxScope, orgIds) = await GetUserOrgScopeAsync(_userManager.UserId.Value);
            if (maxScope == DataScopeEnum.All)
            {
                return null;
            }

            return orgIds.ToHashSet();
        }

        /// <summary>
        /// 机构子树 Id 集(含自身):整表载入后 BFS(管理端数据量级小,单次全量查询足够,与 TreeAsync 同口径)。
        /// </summary>
        internal async Task<List<long>> GetChildIdListWithSelfById(long orgId)
        {
            var all = await _orgRepo.AsQueryable().ToListAsync();
            var childrenMap = all
                .Where(u => u.Pid != 0)
                .GroupBy(u => u.Pid)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

            var result = new List<long> { orgId };
            var queue = new Queue<long>();
            queue.Enqueue(orgId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (childrenMap.TryGetValue(current, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        result.Add(kid);
                        queue.Enqueue(kid);
                    }
                }
            }

            return result;
        }
    }
}
