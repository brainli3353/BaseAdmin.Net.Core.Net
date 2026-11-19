using System.Text.Encodings.Web;
using System.Text.Json;
using AdminCore.Core.Attributes;
using AdminCore.Core.Entities;
using AdminCore.Core.Security;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Models;
using AdminCore.Core.Services.Org;
using AdminCore.Core.SqlSugar;
using AdminCore.Web.Core.Auth.SignatureAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AdminCore.Web.Core.Auth
{
    /// <summary>
    /// /api 接口门禁中间件(task_022 改版):未认证 401 全局门禁 + 每请求租户行级过滤。
    /// 顺序:UseAuthentication → 本中间件 → UseAuthorization(放在认证之后,依赖 User Claims)。
    /// </summary>
    /// <remarks>
    /// 判定链(仅 /api 前缀路径;其余路径如 /health、静态资源、MVC 页面直通):
    /// 0) [OpenApi] 端点(批 E 6.10)→ 先行触发 [Authorize(AuthenticationSchemes)] 声明的 scheme 认证
    ///    (UseAuthentication 只跑默认 JwtBearer,自定义 scheme 须在此提前跑——认证成功才能设租户/机构行级过滤),
    ///    失败 → 401(细分消息经 Items[SignatureAuthenticateFailMsg] 透传);缺 AuthenticationSchemes → 401(防裸开放接口);
    /// 1) [AllowAnonymous] 端点 → 显式放行;
    /// 2) 未认证 → 401;
    /// 3) 已认证 → 放行(按钮权限 403 由 [MenuPerm] 标注端点的 MenuPermFilter 裁决,task_022 默认放行模型)。
    /// WHY 保留 401 门禁:业务控制器未逐端点贴 [Authorize],默认放行模型下若不在此统一拦未认证请求,
    /// 未标注接口将对匿名可达;超管/身份端点无需特判——已认证即放行,登录态端点(auth/me、auth/logout)自然可用。
    /// WHY 008 路由白名单 403 在此退役:task_022 契约改为「特性标注、显式校验、未标注默认放行」,
    /// 权限集消费方同步换为 MenuPermFilter(IMenuPermService),旧路由比对移除。
    /// 租户过滤(契约 §5.3 基座):当前请求 TenantId Claim → 每请求设定 QueryFilter 具体值(超管/无 Claim 跳过),
    /// 请求结束 finally 清理。机构数据范围过滤(task_013):已认证非超管 → 用户最大范围 + 可见机构集
    /// (SysOrgService 缓存解析)→ SqlSugarFilter 装配,finally 同清。
    /// 基座期口径:SqlSugarScope 单例共享、QueryFilter 为全局状态——
    /// 多租户并发下存在过滤串扰窗口(本单租户基座期良性),多租户模块落地时升级为按请求作用域
    /// (本中间件为唯一改动点,业务代码零感知)。
    /// </remarks>
    public class PermissionMiddleware
    {
        /// <summary>统一响应序列化口径(出口单源 AdminJsonOptions:与控制器/异常中间件同构 + G33 long→string)。</summary>
        private static readonly JsonSerializerOptions JsonOptions = AdminCore.Core.Json.AdminJsonOptions.Create();

        private readonly RequestDelegate _next;
        private readonly ISqlSugarClient _db;
        private readonly IServiceScopeFactory _scopeFactory;

        public PermissionMiddleware(RequestDelegate next, ISqlSugarClient db, IServiceScopeFactory scopeFactory)
        {
            _next = next;
            _db = db;
            _scopeFactory = scopeFactory;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            // 0) 开放接口 [OpenApi](批 E 6.10):自定义 scheme 认证须先行——UseAuthentication 只跑默认 JwtBearer
            //    scheme,Signature/ApiKey 若等到 UseAuthorization 的 [Authorize] 才触发,租户/机构行级过滤已错过
            //    设定时机(过滤依赖已认证的 User Claims)。失败 401 + 细分消息透传;成功后落回正常判定链
            //    (已认证自然放行,按钮权限 [MenuPerm] 照常裁决)。
            if (context.GetEndpoint()?.Metadata.GetMetadata<OpenApiAttribute>() is not null)
            {
                var authorize = context.GetEndpoint()!.Metadata.GetMetadata<AuthorizeAttribute>();
                var schemes = authorize?.AuthenticationSchemes;
                if (string.IsNullOrWhiteSpace(schemes))
                {
                    await WriteResultAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.OpenApiSchemeMissing);
                    return;
                }
                // WHY 逐 scheme 尝试:.NET 10 AuthenticateAsync 只接受单 scheme(多 scheme 字符串整体传入抛
                // InvalidOperationException "No authentication handler is registered for the scheme 'Bearer,Signature,ApiKey'");
                // [Authorize(AuthenticationSchemes = "A,B")] 语义 = 任一 scheme 认证成功即通过,按声明顺序尝试,首个成功即停(批 V 部署全量验证抓出)
                foreach (var scheme in schemes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var result = await context.AuthenticateAsync(scheme);
                    // 批 BV 实锤(活体 5090 + 本地 5092 双复现,.NET 10.0.12):IAuthenticationService.AuthenticateAsync
                    // 成功后不回填 context.User(认证结果 Succeeded=true 而 User 仍匿名,sameRef=false)——
                    // UseAuthentication 的 AuthenticationMiddleware 只代默认 scheme 回填,[OpenApi] 自定义 scheme
                    // 在此手动触发若不显式回填,下方 IsAuthenticated 门禁恒 401(签名/ApiKey 开放接口完全不可用)。
                    // 判定同步改用 AuthenticateResult.Succeeded(框架权威语义,不依赖 Identity.IsAuthenticated 派生态)。
                    if (result?.Succeeded == true && result.Principal is not null)
                    {
                        context.User = result.Principal;
                        break;
                    }
                }
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    var failMsg = context.Items.TryGetValue(
                        SignatureAuthenticationDefaults.AuthenticateFailMsgKey, out var msg)
                        ? msg as string
                        : null;
                    await WriteResultAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.OpenApiAuthFailed, failMsg);
                    return;
                }
            }

            // 租户过滤 + 机构数据范围过滤先行(认证后才有 Claims):设定成功才需 finally 清理,防状态泄漏到下一请求
            var tenantFilterSet = SetTenantFilter(context);
            var orgFilterSet = await SetOrgFilterAsync(context);

            // 租户执行上下文 + 租户库连接(批 AB 多租户库级隔离 C1/C3):按 Claim 设定 TenantScope
            // (供 [Tenant] 实体选库解析),并确保该租户独立库连接已注册(懒加载;默认租户/未配置连接串=主库);
            // 上下文在 finally 清理,连接注册常驻(进程级资源,不随请求销毁)
            var tenantScopeSet = await PrepareTenantScopeAsync(context);
            try
            {
                // 非 API 路径直通(契约仅约束 /api 接口权限)
                if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && path != "/api")
                {
                    await _next(context);
                    return;
                }

                // 1) 显式放行:[AllowAnonymous] 端点(登录/OTP 等匿名可达端点)
                if (context.GetEndpoint()?.Metadata.GetMetadata<AllowAnonymousAttribute>() is not null)
                {
                    await _next(context);
                    return;
                }

                // 2) 未认证 → 401(/api 全局登录门禁,见类注释 WHY)
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    await WriteResultAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized);
                    return;
                }

                // 3) 已认证 → 放行。按钮权限 403 由 [MenuPerm] 标注端点的 MenuPermFilter 裁决(task_022 默认放行模型)
                await _next(context);
            }
            finally
            {
                if (tenantScopeSet)
                {
                    // 批 AB:清理租户执行上下文(AsyncLocal)——防上下文随线程复用泄漏到下一请求
                    TenantScope.Clear();
                }

                if (tenantFilterSet)
                {
                    // Clear<接口>:只清本接口注册的过滤器,全局软删过滤器(IDeletedFilter)幸存(探针实测)
                    _db.QueryFilter.Clear<ITenantIdFilter>();
                }

                if (orgFilterSet)
                {
                    // 只清具体类型注册,接口级过滤器(软删/租户)幸存(探针实测,见 SqlSugarFilter.ClearOrgFilter)
                    SqlSugarFilter.ClearOrgFilter(_db);
                }
            }
        }

        /// <summary>
        /// 设定当前请求的租户行级过滤:TenantId Claim 解析成功且非超管时 Add 具体值(契约 §5.3:
        /// 无 Claim 或超管跳过——超管豁免对齐 baseadmin)。返回是否设定(供 finally 清理)。
        /// </summary>
        private bool SetTenantFilter(HttpContext context)
        {
            var user = context.User;
            if (user.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            // 超管豁免(契约 §5.3);批 D 1.14:身份经 UserManager 门面(中间件单例 → 请求作用域解析)
            using var scope = _scopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager>();
            if (userManager.IsSuperAdmin)
            {
                return false;
            }

            if (userManager.TenantId is not long tenantId)
            {
                return false;
            }

            // 先清后设:同一作用域上一请求遗留的具体值不得覆盖(设定值永远是当前请求的租户)。
            // Clear<接口>:只清本接口注册的过滤器,全局软删过滤器(IDeletedFilter)幸存(探针实测)
            _db.QueryFilter.Clear<ITenantIdFilter>();
            _db.QueryFilter.AddTableFilter<ITenantIdFilter>(u => u.TenantId == tenantId);
            return true;
        }

        /// <summary>
        /// 设定当前请求的机构数据范围行级过滤(task_013):已认证且非超管 → 解析用户最大范围 + 可见机构集
        /// (SysOrgService.GetUserOrgScopeAsync,KeyUserOrg/KeyRoleMaxDataScope 7 天缓存)→ SqlSugarFilter 装配 QueryFilter。
        /// 超管/无用户 Claim 豁免(与租户过滤同口径,契约 4)。返回是否设定(供 finally 清理)。
        /// 基座期口径:QueryFilter 串扰窗口与租户过滤同(类注释)。
        /// </summary>
        private async Task<bool> SetOrgFilterAsync(HttpContext context)
        {
            var user = context.User;
            if (user.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            // 批 D 1.14:身份经 UserManager 门面(中间件单例 → 请求作用域解析;超管/无 Claim 豁免契约 4 口径不变)
            // Scoped 依赖(SysOrgService/UserManager)→ 每请求 CreateScope(本中间件是单例)
            using var scope = _scopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager>();
            if (userManager.IsSuperAdmin)
            {
                return false;
            }

            if (userManager.UserId is not long userId)
            {
                return false;
            }

            var orgService = scope.ServiceProvider.GetRequiredService<SysOrgService>();
            var (maxScope, orgIds) = await orgService.GetUserOrgScopeAsync(userId);
            return SqlSugarFilter.SetOrgEntityFilter(_db, userId, maxScope, orgIds);
        }

        /// <summary>
        /// 设定租户执行上下文并确保租户库连接(批 AB C1/C3 多租户库级隔离):
        /// 已认证且 Claim 有 TenantId → TenantScope.Set(tenantId)([Tenant] 实体选库据此路由)
        /// + TenantDbRegistry.EnsureConnectionAsync(懒加载独立库连接;默认租户/未配置连接串=主库)。
        /// 返回是否设定上下文(供 finally 清理)。
        /// </summary>
        /// <remarks>
        /// WHY 超管也设定上下文(与租户行级过滤"超管豁免"不同):行级过滤豁免是"看得更宽"(查询不加
        /// TenantId 条件),而**库路由**是"数据落在哪个库"——超管自身仍隶属某租户(TenantId Claim),
        /// 其在该租户下写入的数据必须落该租户库,否则超管写入会串到主库(批 AB 裁决留痕)。
        /// WHY 在此处(而非仓储构造内)懒加载:连接解析需查库/解密(异步 I/O),仓储构造是同步路径;
        /// 中间件在请求期一次性完成,仓储仅同步读注册表(未注册即 fail-loud,见 SqlSugarSetup.SelectConfigIdFor)。
        /// </remarks>
        private async Task<bool> PrepareTenantScopeAsync(HttpContext context)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            using var scope = _scopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager>();
            if (userManager.TenantId is not long tenantId)
            {
                return false;
            }

            TenantScope.Set(tenantId);
            await TenantDbRegistry.EnsureConnectionAsync(tenantId);
            return true;
        }

        /// <summary>401/403 统一响应体:HTTP 语义 + ApiResult 业务体同时表达(与契约 007 口径一致)。
        /// 批 AQ:<paramref name="errorCode"/> 为稳定错误码 → 按请求语言渲染解释文案;
        /// <paramref name="rawMessage"/> 为外部透传消息(如签名认证失败细分),非空则优先原样返回。</summary>
        private static async Task WriteResultAsync(HttpContext context, int statusCode, string errorCode, string? rawMessage = null)
        {
            var localization = context.RequestServices.GetService<ILocalizationService>();
            var message = string.IsNullOrEmpty(rawMessage) ? localization?.Get(errorCode) ?? errorCode : rawMessage;
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(ApiResult.Fail(message, statusCode, errorCode), JsonOptions);
        }
    }
}
