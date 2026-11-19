using System.Collections.Concurrent;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Entities;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Repository;
using AdminCore.Core.Services.RoleTable.Dto;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

namespace AdminCore.Core.Services.RoleTable
{
    /// <summary>
    /// 角色字段权限服务(批 AH 2.9):授权(全量替换)/回读/授权树 + **列白名单缓存**(供响应出口强制点消费)。
    /// </summary>
    /// <remarks>
    /// **表级开关(批 AK/G13)**:受管表由 <see cref="SysTableFieldScope"/> 显式声明(不再"任一角色有授权行即
    /// 全局受管"——那会跨角色跨租户误伤);授权即自动入册(Enabled=true),可手动关闭;未声明/关闭的表零影响。
    /// 强制点分工(本批真做的核心):
    /// 本服务只负责**存储与读取白名单**;"按白名单裁剪响应"由 `Web.Core/Filters/ColumnScopeFilter` 在响应出口执行
    /// (超管直通 / 无授权行的表不动 / DTO→实体→表名保守解析)。二者通过本服务的
    /// <see cref="GetAllowedColumnsAsync"/> 与 <see cref="GetManagedTablesAsync"/> 两个读口衔接。
    /// 审计字段口径:授权树(用于勾选)**排除**基类审计字段(Id/Create*/Update*/Delete*/IsDelete/TenantId)——
    /// 这些列不由字段权限治理(否则"没勾 Id"会让列表主键消失,前端整页不可用);裁剪侧同理跳过它们。
    /// 缓存:角色白名单 <c>KeyRoleTable</c> TTL 7 天,授权变更即失效(与 KeyUserApiPerm 同口径)。
    /// </remarks>
    [Service]
    public class SysRoleTableService
    {
        /// <summary>受管表集合缓存键(与角色键同前缀;供 GrantAsync 失效与 GetManagedTablesAsync 读写)。</summary>
        private const string ManagedTablesKey = CacheConst.KeyRoleTable + "__managed";

        /// <summary>单角色白名单缓存键(键维度=角色:GrantAsync 变更即失效,读路径逐角色回填)。</summary>
        private static string RoleCacheKey(long roleId) => $"{CacheConst.KeyRoleTable}{roleId}";

        /// <summary>基类审计字段(授权树排除 + 裁剪跳过;大小写不敏感)。</summary>
        private static readonly HashSet<string> AuditColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "createtime", "createuserid", "createusername", "createorgid",
            "updatetime", "updateuserid", "updateusername", "updateorgid",
            "deletetime", "isdelete", "tenantid"
        };

        /// <summary>实体名 → 类型 缓存(DTO 反查实体用;见 ResolveEntity)。</summary>
        private static readonly ConcurrentDictionary<string, Type?> EntityNameCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ISqlSugarRepository<SysRoleTable> _roleTableRep;
        private readonly ISqlSugarRepository<SysRole> _roleRep;
        private readonly ISqlSugarRepository<SysUserRole> _userRoleRep;
        private readonly ISqlSugarRepository<SysTableFieldScope> _scopeRep;
        private readonly ICacheService _cache;

        public SysRoleTableService(ISqlSugarRepository<SysRoleTable> roleTableRep, ISqlSugarRepository<SysRole> roleRep,
            ISqlSugarRepository<SysUserRole> userRoleRep, ISqlSugarRepository<SysTableFieldScope> scopeRep,
            ICacheService cache)
        {
            _roleTableRep = roleTableRep;
            _roleRep = roleRep;
            _userRoleRep = userRoleRep;
            _scopeRep = scopeRep;
            _cache = cache;
        }

        /// <summary>
        /// 授权(全量替换):校验角色存在 → 清洗 items("表:列"格式 + 去重 + 去掉审计列)→ 删旧插新。
        /// 空集=清空该角色全部列授权(语义与 grantMenu/grantOrg 一致)。
        /// </summary>
        public async Task GrantAsync(RoleTableInput input)
        {
            var role = await _roleRep.GetFirstAsync(r => r.Id == input.RoleId);
            if (role is null)
            {
                throw AppException.Of(ErrorCodes.RoleNotFound);
            }

            var rows = new List<SysRoleTable>();
            foreach (var raw in input.Items ?? new List<string>())
            {
                var item = raw?.Trim();
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }

                var parts = item.Split(':', 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    throw AppException.With(ErrorCodes.FieldScopeGrantItemInvalid, "item", item, 400);
                }

                var table = parts[0].Trim();
                var column = parts[1].Trim();
                if (AuditColumns.Contains(column))
                {
                    continue; // 审计列不参与字段权限(见类注释口径)
                }

                rows.Add(new SysRoleTable { RoleId = input.RoleId, TableName = table, ColumnName = column });
            }

            // 去重(唯一索引兜底):同角色同表同列只留一行
            var distinct = rows
                .GroupBy(r => $"{r.TableName}\u0000{r.ColumnName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // 批 AK:重算需要旧表集(授权全量替换后,若某表全库已无任何授权行 ⇒ 撤销其受管声明,对偶语义)
            var oldTables = (await _roleTableRep.AsQueryable()
                    .Where(r => r.RoleId == input.RoleId)
                    .Select(r => r.TableName).ToListAsync())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            await _roleTableRep.DeletePhysicalByAsync(r => r.RoleId == input.RoleId);

            // 逐条插入:本仓仓储契约(ISqlSugarRepository)不暴露 Context/批量入口,按约定 #11 不绕契约
            // (批 AA 同教训——参照的 Storageable/批量 API 在本仓契约外);授权项规模为"表的列数"级,逐条可接受
            foreach (var row in distinct)
            {
                await _roleTableRep.InsertAsync(row);
            }

            // 批 AK:授权与声明**对偶**——授权全量替换后按"全库是否存在该表授权行"重算开关:
            // 存在 ⇒ 入册(配置即受管);全库已无任何授权行 ⇒ 撤销声明(清空授权=恢复不受管,而非"受管但全裁空")。
            await ReconcileScopesAsync(distinct.Select(r => r.TableName).Concat(oldTables)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());

            // 批内自抓修正(缺陷①):除角色键外**必须一并失效受管表集合键**——否则"新增受管表"最长 7 天不被
            // 强制点识别(受管集合缓存 TTL 7 天),期间该表**漏裁**(授权了等于没生效)。
            await _cache.RemoveAsync(ManagedTablesKey);
            await _cache.RemoveAsync(RoleCacheKey(input.RoleId));
        }

        /// <summary>回读:该角色的授权项集合("表名:列名",按表名/列名排序稳定)。</summary>
        public async Task<RoleTableOutput> GetRoleTableAsync(long roleId)
        {
            var rows = await _roleTableRep.AsQueryable()
                .Where(r => r.RoleId == roleId)
                .OrderBy(r => r.TableName)
                .OrderBy(r => r.ColumnName)
                .ToListAsync();

            return new RoleTableOutput(roleId, rows.Select(r => $"{r.TableName}:{r.ColumnName}").ToList());
        }

        /// <summary>
        /// 角色复制级联(批 BH):全量复制源角色的字段权限授权行(参照 CopyRole 第 4 张关联表
        /// <c>sys_role_table</c> 的复制口径;参照 <c>CopyRolTableByRoleId</c> 移植)。
        /// 新角色 Id 全新 ⇒ 无既有缓存;源角色行不动 ⇒ 全局"受管"集合不变 ⇒ 无需失效缓存。
        /// </summary>
        public async Task CopyByRoleIdAsync(long sourceRoleId, long newRoleId)
        {
            var rows = await _roleTableRep.AsQueryable()
                .Where(r => r.RoleId == sourceRoleId)
                .ToListAsync();
            foreach (var row in rows)
            {
                await _roleTableRep.InsertAsync(new SysRoleTable
                {
                    RoleId = newRoleId,
                    TableName = row.TableName,
                    ColumnName = row.ColumnName
                });
            }
        }

        /// <summary>
        /// 角色删除级联(批 BH):物理删该角色的字段权限授权行(参照 <c>DeleteRolTableByRoleId</c> 移植),
        /// 并按 GrantAsync 的对偶语义收尾:涉及表若全库已无任何授权行 ⇒ 撤销受管声明(ReconcileScopesAsync),
        /// 并失效受管集合缓存(否则删角色后"受管"最长 7 天漂移,面板计数/裁剪面失实)。
        /// </summary>
        public async Task DeleteByRoleIdAsync(long roleId)
        {
            var tables = (await _roleTableRep.AsQueryable()
                    .Where(r => r.RoleId == roleId)
                    .Select(r => r.TableName).ToListAsync())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await _roleTableRep.DeletePhysicalByAsync(r => r.RoleId == roleId);

            if (tables.Count > 0)
            {
                await ReconcileScopesAsync(tables);
                await _cache.RemoveAsync(ManagedTablesKey);
                await _cache.RemoveAsync(RoleCacheKey(roleId)); // 角色已删,清残留键
            }
        }

        /// <summary>
        /// 授权树:反射实体程序集生成"表 → 列"(仅排除 **[IgnoreTable]**(外部/手工维护表)与基类审计字段)。
        /// 数据源=实体元数据(与响应裁剪同一套列名口径,避免"树上能勾、裁剪不认")。
        /// </summary>
        /// <remarks>
        /// **与参照仓的差异(批 AH 裁决,实机 e2e 抓出)**:参照 `HandleTableColumn` 额外排除 `[SysTable]` 实体,
        /// 那会让 `sys_user`/`sys_role` 等**系统表无法配置字段权限**——而字段权限的首要价值恰是治理系统表的敏感列
        /// (如账号/联系方式);故本仓**不排除 [SysTable]**,只排除 `[IgnoreTable]`(只读外部表/手工表,本就不该被裁)。
        /// 审计列仍排除(否则"没勾 Id"会让列表主键消失)。
        /// </remarks>
        public Task<List<TableColumnOutput>> GetAllTableColumnAsync()
        {
            var result = new List<TableColumnOutput>();

            foreach (var type in typeof(SysUser).Assembly.GetTypes()
                         .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
                         .Where(t => t.GetCustomAttribute<SugarTable>(inherit: false) is not null)
                         .Where(t => t.GetCustomAttribute<IgnoreTableAttribute>() is null)
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var table = type.GetCustomAttribute<SugarTable>(inherit: false)!;
                var node = new TableColumnOutput
                {
                    TableName = table.TableName ?? type.Name,
                    TableComment = table.TableDescription ?? string.Empty
                };

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.GetIndexParameters().Length == 0)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var column = ResolveColumnName(prop);
                    if (AuditColumns.Contains(column))
                    {
                        continue;
                    }

                    node.Columns.Add(new ColumnItemOutput
                    {
                        ColumnName = column,
                        ColumnComment = prop.GetCustomAttributes(typeof(SugarColumn), true)
                            .OfType<SugarColumn>().FirstOrDefault()?.ColumnDescription ?? string.Empty
                    });
                }

                if (node.Columns.Count > 0)
                {
                    result.Add(node);
                }
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// 取"当前用户全部角色的列白名单并集"(供响应出口强制点):表名 → 允许列集合(大小写不敏感)。
        /// 超管返回 null(=不裁剪);用户无角色返回空字典(=所有**被管理**的表都不允许任何列——
        /// 但"被管理"由 <see cref="GetManagedTablesAsync"/> 决定,未被任何角色授权过的表不受影响)。
        /// </summary>
        public async Task<Dictionary<string, HashSet<string>>?> GetAllowedColumnsAsync(long userId, IReadOnlyList<long> roleIds)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (roleIds.Count == 0)
            {
                return map;
            }

            foreach (var roleId in roleIds.Distinct())
            {
                var cached = await _cache.GetAsync<List<string>>(RoleCacheKey(roleId));
                if (cached is null)
                {
                    // 未命中回源:该角色的"表:列"项 → 回填(键维度=角色,GrantAsync 变更即失效)
                    cached = (await _roleTableRep.AsQueryable()
                            .Where(r => r.RoleId == roleId)
                            .ToListAsync())
                        .Select(r => $"{r.TableName}\u0000{r.ColumnName}")
                        .ToList();
                    await _cache.SetAsync(RoleCacheKey(roleId), cached, TimeSpan.FromDays(7));
                }

                foreach (var item in cached)
                {
                    var parts = item.Split('\u0000');
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    if (!map.TryGetValue(parts[0], out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        map[parts[0]] = set;
                    }

                    set.Add(parts[1]);
                }
            }

            return map;
        }

        /// <summary>
        /// 被字段权限管理的表集合(全局:任何角色只要对该表有授权行,该表就进入"受管"状态——
        /// 用于强制点的"仅对受管表生效"判定,未受管表一律零影响)。缓存 TTL 7 天,授权变更即失效。
        /// </summary>
        public async Task<HashSet<string>> GetManagedTablesAsync()
        {
            var managedKey = ManagedTablesKey;
            var cached = await _cache.GetAsync<HashSet<string>>(managedKey);
            if (cached is not null)
            {
                // 批内自抓修正(缺陷③):经 JSON 往返后 HashSet 的比较器回落为默认 Ordinal,
                // 与写入时的 OrdinalIgnoreCase 不一致 ⇒ 大小写不同的表名判定随缓存冷热翻转(漏裁)。
                // 故读回后**重建**比较器(写入路径保持 OrdinalIgnoreCase,两侧统一)。
                return new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
            }

            // 批 AK:权威读口 = 表级开关表 Enabled=true 的表(不再是"有授权行的表")。
            // **读路径零写副作用**:升级迁移已剥离为显式 MigrateLegacyScopesAsync(Program 启动钩子)——
            // 曾把迁移内嵌此处,导致"开关表暂时为空"即触发重插(本批测试抓出:直连清空后受管判定又见旧行)。
            var tables = (await _scopeRep.AsQueryable().Where(s => s.Enabled).ToListAsync())
                .Select(s => s.TableName)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await _cache.SetAsync(managedKey, tables, TimeSpan.FromDays(7));
            return tables;
        }

        /// <summary>
        /// 升级迁移(批 AK,仅启动钩子调用一次):开关表**完全没有任何行**且历史授权表非空 ⇒ 视为升级库,
        /// 把既有授权表批量入册(Enabled=true)——否则旧授权一夜之间全部失效(惊吓)。
        /// WHY 不在读口内做:读口(强制点高频)不允许写副作用;迁移语义=一次性数据整理,只该由启动路径触发。
        /// 幂等:开关表已有任何行即跳过(无论开了几个/关了几个)。
        /// </summary>
        public async Task MigrateLegacyScopesAsync()
        {
            var hasAnyScope = await _scopeRep.IsAnyAsync(s => true);
            if (hasAnyScope)
            {
                return;
            }

            var legacyTables = (await _roleTableRep.AsQueryable().Select(r => r.TableName).Distinct().ToListAsync())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (legacyTables.Count > 0)
            {
                await ReconcileScopesAsync(legacyTables);
                await _cache.RemoveAsync(ManagedTablesKey);
            }
        }

        /// <summary>
        /// 批量入册(批 AK):对每个表名 upsert 开关行 Enabled=true(幂等;逐条按契约,规模=表数级)。
        /// </summary>
        /// <summary>
        /// 开关对偶重算(批 AK):对涉及表逐一按"全库是否有该表授权行"定开关——
        /// 有 ⇒ 删旧插新置 Enabled=true(授权即受管);无 ⇒ 删开关行(清空授权=撤销声明)。
        /// WHY 删旧插新而非 GetFirst+Update:批 AK 实测该配置下 Update 分支未把 Enabled 落地(开关表仍留旧值);
        /// 删旧插新幂等且状态确定,无静默失败面。WHY 对偶:清空授权若仍受管 ⇒ 受管表+空白名单=全表全裁空,
        /// 比"没配过"更糟(管理员清空期望恢复不受管)——测试 306 保护这层语义。
        /// </summary>
        private async Task ReconcileScopesAsync(List<string> tableNames)
        {
            foreach (var table in tableNames.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var stillGranted = await _roleTableRep.IsAnyAsync(r => r.TableName == table);
                await _scopeRep.DeletePhysicalByAsync(s => s.TableName == table);
                if (stillGranted)
                {
                    await _scopeRep.InsertAsync(new SysTableFieldScope { TableName = table, Enabled = true, Remark = "批 AK:授权自动入册" });
                }
            }
        }

        /// <summary>
        /// 表级开关清单(批 AK):可配置字段权限的全部表(实体元数据,与授权树同一套口径)+ 当前开关状态 + 已授权角色数。
        /// 供前端开关面板;未在开关表中的表按"未受管"显示(前端置灰说明)。
        /// </summary>
        public async Task<List<TableScopeOutput>> GetTableScopesAsync()
        {
            var tree = await GetAllTableColumnAsync();
            var scopes = await _scopeRep.AsQueryable().ToListAsync();
            var scopeByTable = scopes.ToDictionary(s => s.TableName, StringComparer.OrdinalIgnoreCase);
            // 已授权角色数 = 该表在授权表中的 distinct 角色数(按 表名×角色Id 分组;首版误按表名词条计数恒为 1——批 AK 测试抓出)
            var roleCounts = (await _roleTableRep.AsQueryable()
                    .Select(r => new { r.TableName, r.RoleId }).ToListAsync())
                .Where(x => !string.IsNullOrWhiteSpace(x.TableName))
                .GroupBy(x => x.TableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.RoleId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);

            return tree.Select(n =>
            {
                scopeByTable.TryGetValue(n.TableName, out var scope);
                return new TableScopeOutput(
                    n.TableName,
                    n.TableComment,
                    scope?.Enabled ?? false,
                    roleCounts.TryGetValue(n.TableName, out var c) ? c : 0,
                    n.Columns.Count);
            }).ToList();
        }

        /// <summary>切换表级开关(批 AK):upsert 开关行并失效受管表集合缓存(立即生效,不等 7 天 TTL)。</summary>
        public async Task SetEnabledAsync(string tableName, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw AppException.Of(ErrorCodes.FieldScopeTableRequired, args: null, code: 400);
            }

            // 校验:必须是实体元数据里的可配置表(与授权树同源,防乱写表名污染开关表)
            var tree = await GetAllTableColumnAsync();
            if (tree.All(n => !string.Equals(n.TableName, tableName, StringComparison.OrdinalIgnoreCase)))
            {
                throw AppException.With(ErrorCodes.FieldScopeTableNotConfigurable, "table", tableName, 400);
            }

            // WHY 删旧插新(同 EnsureScopesEnabledAsync):"开关=是否声明"⇒ 关=删行,开=删后插 Enabled=true;
            // 规避 GetFirst+Update 未落库的实测疑点,且无"多行并存"可能(唯一性由本方法保证)。
            await _scopeRep.DeletePhysicalByAsync(s => s.TableName == tableName);
            if (enabled)
            {
                await _scopeRep.InsertAsync(new SysTableFieldScope
                {
                    TableName = tableName,
                    Enabled = true,
                    Remark = "批 AK:手动开启"
                });
            }

            await _cache.RemoveAsync(ManagedTablesKey);
        }

        /// <summary>
        /// 取用户的角色 Id 集合(供强制点解析"该用户角色白名单并集";UserManager 只暴露 AccountType/UserId 等 Claim,
        /// 角色关系在 sys_user_role 表)。仅在**受管表**分支被调用,故不额外缓存(避开与角色变更失效的协调)。
        /// </summary>
        public async Task<List<long>> GetUserRoleIdsAsync(long userId)
            => await _userRoleRep.AsQueryable()
                .Where(ur => ur.UserId == userId)
                .Select(ur => ur.RoleId)
                .ToListAsync();

        /// <summary>列名解析:显式 [SugarColumn(ColumnName)] 优先,否则属性名(与响应裁剪共用,防两处口径漂移)。</summary>
        public static string ResolveColumnName(PropertyInfo prop)
            => prop.GetCustomAttributes(typeof(SugarColumn), true)
                .OfType<SugarColumn>()
                .FirstOrDefault()?.ColumnName is { Length: > 0 } name
                ? name
                : prop.Name;

        /// <summary>审计列判定(裁剪侧跳过:不由字段权限治理)。</summary>
        public static bool IsAuditColumn(string column) => AuditColumns.Contains(column);

        /// <summary>
        /// DTO 类型 → 实体类型(保守解析,找不到返回 null=不裁剪):
        /// 按 DTO 名去常见后缀(Output/Detail/Dto/Input/Page/Result)后,与实体简名比对(Sys 前缀双向):
        /// 例如 UserOutput→User→SysUser;NoticeOutput→Notice→SysNotice;SysLogExOutput→SysLogEx(同形直接命中)。
        /// </summary>
        public static Type? ResolveEntity(Type dtoType)
            => EntityNameCache.GetOrAdd(dtoType.FullName ?? dtoType.Name, _ =>
            {
                var name = dtoType.Name;
                foreach (var suffix in new[] { "Output", "Detail", "Dto", "Input", "Page", "Result" })
                {
                    if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                    {
                        name = name[..^suffix.Length];
                        break;
                    }
                }

                var candidates = new[] { name, "Sys" + name };
                foreach (var candidate in candidates)
                {
                    var hit = typeof(SysUser).Assembly.GetTypes().FirstOrDefault(t =>
                        t.IsClass && !t.IsAbstract
                        && t.GetCustomAttribute<SugarTable>(inherit: false) is not null
                        && string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));
                    if (hit is not null)
                    {
                        return hit;
                    }
                }

                return null;
            });
    }
}