using System.Collections;
using System.Reflection;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Security;
using AdminCore.Core.Services.RoleTable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminCore.Web.Core.Filters
{
    /// <summary>
    /// 字段权限强制点（批 AH 2.9）：响应写出前，按"当前用户全部角色的列白名单并集"把**未授权列置空**。
    /// </summary>
    /// <remarks>
    /// WHY 放在响应出口而不是 SQL 层：SqlSugar 查询构建后难以安全地"剔除若干列"（尤其投影/嵌套/分页），
    /// 而在响应写出前按 DTO 反射裁剪是**可审计、可测、零 SQL 侵入**的做法（调研结论亦建议"统一查询出口"而非照搬参照的表面积）。
    /// 现行口径（四条，缺一会误伤）：
    /// ① **仅对"受管表"生效**——任何角色只要对某表有授权行，该表进入受管状态；未受管的表一律不动（默认零影响）；
    /// ② **超管直通**（999 不裁剪）；
    /// ③ **fail-closed**：受管表对"其角色白名单并集为空"的用户 ⇒ 全部非审计列置空（未授权即不可见）；
    /// ④ **保守解析**：DTO 名映射不到实体（`UserOutput`→`SysUser`）或属性在实体中不存在 ⇒ **不裁**（宁可不裁也不误裁）。
    /// 不裁的例外：审计列（Id/Create*/Update*/IsDelete/TenantId 等，见 SysRoleTableService.IsAuditColumn）——
    /// 否则"没勾 Id"会让列表主键消失、前端整页不可用。
    /// 代价（诚实标注）：每响应一次反射（有进程内类型缓存）+ 受管表查询有缓存；递归深度上限 6 防环。
    /// </remarks>
    // WHY [Service] 自注册(批 AH 实机抓出):Program.cs 用 MvcOptions.Filters.AddService<ColumnScopeFilter>()
    // 从请求作用域解析本类,未注册即启动后首请求抛「No service for type ... has been registered」(同 OpLogFilter 范式);
    // 显式 ServiceType=具体类:确保注册的是具体类型本身(自动匹配接口会注册成 IAsyncResultFilter→具体类解析不到)。
    [Service(ServiceType = typeof(ColumnScopeFilter), Lifetime = ServiceLifetime.Transient)]
    public class ColumnScopeFilter : IAsyncResultFilter
    {
        private const int MaxDepth = 6;

        private readonly UserManager _userManager;
        private readonly SysRoleTableService _roleTableService;
        private readonly ILogger<ColumnScopeFilter> _logger;

        public ColumnScopeFilter(UserManager userManager, SysRoleTableService roleTableService, ILogger<ColumnScopeFilter> logger)
        {
            _userManager = userManager;
            _roleTableService = roleTableService;
            _logger = logger;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            try
            {
                if (context.Result is ObjectResult { Value: not null } result)
                {
                    await TrimAsync(result.Value, 0).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // 强制点是安全增强件:裁剪失败**不得**阻断响应(记录后放行)——但同时这是安全事件,记 Error 可检索
                _logger.LogError(ex, "字段权限裁剪失败(本响应未裁剪,已放行)");
            }

            await next().ConfigureAwait(false);
        }

        private async Task TrimAsync(object? value, int depth)
        {
            if (value is null || depth > MaxDepth)
            {
                return;
            }

            // 集合(PageResult.List / 数组 / List<T>)→ 逐项处理
            if (value is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    await TrimAsync(item, depth + 1).ConfigureAwait(false);
                }

                return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan))
            {
                return;
            }

            var entityType = SysRoleTableService.ResolveEntity(type);
            if (entityType is null)
            {
                // 非 DTO(或无法映射):只递归其属性(可能内含 DTO 列表,如 ApiResult.Data 之外的自定义包装)
                await TrimChildrenAsync(value, type, null, null, depth).ConfigureAwait(false);
                return;
            }

            // 超管直通判定**前置**(批内自抓修正⑤):先于受管表查询,超管响应零额外查询(此前先查缓存/DB 再判超管)
            if (_userManager.IsSuperAdmin)
            {
                return;
            }

            var tableName = entityType.GetCustomAttribute<SugarTable>(inherit: false)?.TableName ?? entityType.Name;
            var managed = await _roleTableService.GetManagedTablesAsync().ConfigureAwait(false);
            if (!managed.Contains(tableName))
            {
                return; // 未受管表:零影响(不递归——其内嵌对象同样不受本机制治理)
            }

            var roleIds = await _roleTableService.GetUserRoleIdsAsync(_userManager.UserId ?? 0).ConfigureAwait(false);
            var allowedMap = await _roleTableService.GetAllowedColumnsAsync(_userManager.UserId ?? 0, roleIds).ConfigureAwait(false);
            allowedMap ??= new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var allowed = allowedMap.TryGetValue(tableName, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await TrimChildrenAsync(value, type, entityType, allowed, depth).ConfigureAwait(false);
        }

        /// <summary>
        /// 裁剪对象属性：可写属性且映射到实体列名、且非审计列、且不在白名单 → 置 default；
        /// 复杂属性(可能嵌套 DTO)递归处理。
        /// </summary>
        private async Task TrimChildrenAsync(object value, Type type, Type? scopeEntity, HashSet<string>? scopeAllowed, int depth)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var propValue = prop.GetValue(value);
                var isSimple = prop.PropertyType.IsPrimitive || prop.PropertyType.IsEnum
                               || prop.PropertyType == typeof(string) || prop.PropertyType == typeof(decimal)
                               || prop.PropertyType == typeof(DateTime) || prop.PropertyType == typeof(DateTimeOffset)
                               || prop.PropertyType == typeof(Guid) || prop.PropertyType == typeof(TimeSpan)
                               || Nullable.GetUnderlyingType(prop.PropertyType) is not null;

                if (scopeEntity is not null && scopeAllowed is not null)
                {
                    var entityProp = scopeEntity.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (entityProp is not null && isSimple)
                    {
                        var column = SysRoleTableService.ResolveColumnName(entityProp);
                        if (!SysRoleTableService.IsAuditColumn(column) && !scopeAllowed.Contains(column))
                        {
                            if (prop.CanWrite)
                            {
                                prop.SetValue(value, prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null);
                            }
                        }

                        continue;
                    }
                }

                if (!isSimple && propValue is not null)
                {
                    await TrimAsync(propValue, depth + 1).ConfigureAwait(false);
                }
            }
        }
    }
}