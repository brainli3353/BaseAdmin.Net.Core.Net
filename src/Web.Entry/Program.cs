using System.Text;
using AdminCore.Core.Entities;
using AdminCore.Core.Services.User;
using AdminCore.Core;
using AdminCore.Core.Extensions;
using AdminCore.Core.Attributes;
using AdminCore.Core.Cache;
using AdminCore.Core.Exceptions;
using AdminCore.Core.Localization;
using AdminCore.Core.Logging;
using AdminCore.Core.Models;
using AdminCore.Core.SeedData;
using AdminCore.Core.SqlSugar;
using AdminCore.Web.Core.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using AdminCore.Web.Core.Auth.ApiKeyAuth;
using AdminCore.Core.Services.RoleTable;
using AdminCore.Core.Services.Seed;
using AdminCore.Core.Hub;
using AdminCore.Web.Core.Auth.SignatureAuth;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

// SqlSugar 自带同名 ICacheService——别名消歧(本项目缓存抽象是 Core 的)
using ICacheService = AdminCore.Core.Cache.ICacheService;

// 环境缺省回落(task_010):未显式指定环境时缺省 Development(.NET 8+ 宿主缺省是 Production,
// 裸 dotnet run 会直连生产占位配置)。显式指定(shell 环境变量/IDE)永远优先——
// 注意 launchSettings.json 不得再设 ASPNETCORE_ENVIRONMENT:实测 launchSettings 覆盖 shell 环境变量,
// 会挡住「ASPNETCORE_ENVIRONMENT=Staging dotnet run」这类验收/巡检命令
if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null or "")
{
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
}

// 监听端口缺省(task_010 fixa,盲审健康检查门):服务默认监听 0.0.0.0:5090(盲审固定探测端口)。
// 环境变量 ASPNETCORE_URLS / launchSettings / --urls 显式指定时优先(此处仅补缺省,不覆盖);
// 补缺省保证任意启动方式(dotnet run / dotnet <dll> / 看门狗脚本)下 5090 均可达——
// 否则裸 dotnet <dll> 落默认 5000、dotnet run 落 launchSettings 端口,5090 探测落空。
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5090");
}

var builder = WebApplication.CreateBuilder(args);

// 模块配置加载(task_010,参考源 baseadmin 模式):Business/Configuration 下每功能一个 json
// (App/Database/JWT/Cache/Logging) + 环境后缀覆盖 {Module}.{Environment}.json(Development/Staging/Production)。
// 配置链顺序(实测,同键后加载源覆盖先加载源):appsettings.json → appsettings.{env}.json
// (CreateBuilder 加入) → 模块基础 → 模块环境覆盖(LoadModuleConfiguration 追加) → 环境变量 → 命令行
// (下方重加,链尾=最高优先级)。
// WHY 模块 JSON 之后重加环境变量/命令行(G8 修复,2026-09-24):CreateBuilder 加入的 env/命令行被
// LoadModuleConfiguration 追加的模块 JSON 压过 → 生产经环境变量注入密钥/连接串将被 *.Production.json
// 占位值覆盖(受控实验实证:App__Name 环境变量被模块 App.json 覆盖;差距 G8 高危·上线阻断)。
// 重加使 env/命令行恢复各模块 json 文档口径「环境变量注入生产密钥=最高优先」;开发环境无此类
// 环境变量,行为零变化。
var moduleConfigDir = LoadModuleConfiguration(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    builder.Environment.EnvironmentName);

// G8:模块 JSON 之后重加环境变量/命令行,恢复其最高优先级(生产密钥注入通道)。
// 用顶层 args 而非 builder.Args:.NET 10 的 WebApplicationBuilder 已无 Args 属性(实机探针确认)
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// 文件日志通道(批 I 5.2 多通道+按天切分):内建 Console 之外追加文件 provider,
// 配置 Logging:File(Enabled/Path/MinimumLevel,缺省开、logs 目录、Information);置于 Build 之前使启动日志同入文件
builder.Logging.AddFileLogger(builder.Configuration);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(o =>
    {
        // .NET 10 中 JsonOptions.JsonSerializerOptions 属性引用只读，只能原地变更默认实例的属性：
        // G33 起统一经 AdminJsonOptions（出口单源）= camelCase + 中文不转义 + long→string 转换器，
        // 与全局异常中间件/PermissionMiddleware 的统一响应显式同构（long id 不再在 JSON.parse 期丢精度）
        AdminCore.Core.Json.AdminJsonOptions.Configure(o.JsonSerializerOptions);
    });

// 全局 Action 过滤器,均按请求作用域解析;AddService 而非 Add 实例——过滤器需每请求从当前作用域解析依赖:
// OpLogFilter(契约 §6 操作日志,内部取仓储/日志)、MenuPermFilter(task_022 按钮权限,对 [MenuPerm] 标注端点裁决 403)、
// IdempotentFilter(批 D 1.13,对 [Idempotent] 标注端点窗口内重复提交裁决 429)、
// UnitOfWorkFilter(批 D 3.2,对 [UnitOfWork] 标注端点包本地事务)。
// WHY Configure<MvcOptions> 而非 AddControllers 链式调用:.NET 10 移除了 IMvcBuilder.AddControllersOptions 扩展方法(实机探针确认),
// Configure 路径为框架稳定口径(MvcOptions 与 AddControllers* 共享同一 Options 实例)
// 注册序 = 外层→内层(批 D 定案):权限 403 短路不消费幂等窗口、不开事务;幂等 429 短路不开事务;
// 事务包 action;操作日志最外(记录含 403/429/事务回滚失败的全请求成败口径)
builder.Services.Configure<MvcOptions>(o =>
{
    o.Filters.AddService<OpLogFilter>();
    o.Filters.AddService<MenuPermFilter>();
    o.Filters.AddService<IdempotentFilter>();
    o.Filters.AddService<UnitOfWorkFilter>();
    // 批 AH 2.9 字段权限强制点:响应出口按"角色列白名单"裁剪未授权列(仅对受管表生效,超管直通;见 ColumnScopeFilter remarks)
    o.Filters.AddService<AdminCore.Web.Core.Filters.ColumnScopeFilter>();
});

// 批 O 6.11:模型校验失败(自动 400 短路口)改走 ApiResult 统一出口——工厂签名限 ProblemDetails
// (.NET 10 实机确认),直接返回 ProblemDetails 会与全仓"HTTP 恒 200 + 体业务码"契约断裂
// (ProblemDetails 有专属 JSON converter,形状不可控);改为抛 AppException 复用 ExceptionMiddleware
// 业务异常分支(HTTP 200 + ApiResult.Fail(msg, 400);预期分支只记 Warning 不进异常档)——
// 与 403/429 业务码出口同型(裁决见 docs/研究/g9_refer_research.md §5 ①)
builder.Services.Configure<ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = context =>
    {
        var msg = string.Join(";", context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors.Select(err => string.IsNullOrWhiteSpace(err.ErrorMessage) ? $"{e.Key} 无效" : err.ErrorMessage)));
        // 批 AQ:无明细时走稳定错误码(解释按请求语言渲染);有明细时保留原文(含 .NET 模型校验消息与字段名)
        throw msg.Length == 0 ? AppException.Of(ErrorCodes.ParamInvalid, args: null, code: 400) : new AppException(msg, 400);
    };
});

// SqlSugar 初始化:雪花主键 + 单库连接 + AOP 日志(详见 Core/SqlSugarSetup.cs)
builder.Services.AddSqlSugar(builder.Configuration);

// IOptions 强类型配置集中绑定(批 Q 6.8):本仓实际 8 节(Jwt/DbSettings/Cache/Yit/SysPassword/Otp/Cryptogram/Upload),
// 节名=类名约定;消费方经 IOptions<T>(AddJwtBearer 块/AuthService 已迁,其余散读点保留 IConfiguration 直读——g11 §5 ①)
builder.Services.AddProjectOptions(builder.Configuration);

// 缓存(task_007 M2a):实现由配置 Cache:Type 决定(Redis 默认;ConnectionStrings:Redis 为空时回退 Memory)
builder.Services.AddCache(builder.Configuration);

// 本地化(批 AL 6.6):资源装载 + Accept-Language/?lang= 解析;静态解析器随单例构造装载
builder.Services.AddAdminLocalization();

// Http 出站日志(批 AD 5.3):绑定 Logging:Http 段 + 注册 HttpLoggingHandler(Transient)。
// 只注册不启用——业务侧 opts-in 才生效:services.AddHttpClient("x").AddHttpMessageHandler<HttpLoggingHandler>();
// 开关 Logging:Http:Enabled(缺省 false)决定已挂载管道是否真记录(框架默认零副作用)
builder.Services.AddHttpOutboundLogging(builder.Configuration);

// 服务自动发现(task_024):业务服务经 [Service] 特性标注 + 反射扫描注册,根除本文件散注册热点。
// 生命周期与接口映射同迁移前逐条一致:业务服务默认 Scoped(自注册,控制器按具体类注入);
// CryptogramHelper/OtpService 无状态,Singleton;
// UserTokenStateProvider→IUserTokenStateProvider、MenuPermService→IMenuPermService、FileContentStore→IFileContentStore
// 自动匹配唯一公共接口(接口→实现);
// OpLogFilter/MenuPermFilter 为 Transient 且特性显式 ServiceType=具体类(强制自注册)——MVC AddService<T>
// (见上 MvcOptions)按具体类从请求作用域解析,若自动匹配接口→实现则具体类不可解析,故必须自注册。
// 双程序集锚点:Core(业务服务 + Security + MenuPermFilter)、Web.Core(JwtAuthHandler + OpLogFilter);
// 孤儿 SysLogLoginService(原未注册、无人引用,纯增量)一并纳入,见验收报告。
builder.Services.AddAutoServices(
    typeof(SysUserService).Assembly,   // Core 程序集锚点
    typeof(JwtAuthHandler).Assembly);  // Web.Core 程序集锚点

// JWT 鉴权(原生 JwtBearer,零 Furion):校验参数全部从配置 Jwt 段读取(task_010 起落点
// Business/Configuration/JWT.json + 环境覆盖);开发/测试占位密钥按环境区分,生产必须环境变量注入(如 Jwt__Secret)。
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 批 Q 6.8 实证更正(g11 §5 ⑦):JwtBearer 10.0.11 无 AddJwtBearer(Action<IServiceProvider, JwtBearerOptions>)
        // 双参回调重载(探测工程实证 CS1660)——Program builder 世界经 IConfiguration 直读是标准通道,
        // DI 世界消费(AuthService)走 IOptions<JwtOptions>;两处配置源同节同值,无漂移风险
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!)),
            ValidateLifetime = true
        };

        // ① OnMessageReceived(批 F 5.13):SignalR 浏览器端标准通道——WebSocket 握手无法设置 Authorization 头,
        //    JS 客户端 accessTokenFactory 经 ?access_token= 传 token(参照 AdminNetPro OnMessageReceived 口径)。
        // ② OnTokenValidated(task_007):业务校验链——黑名单 + TokenVersion。Handler 依赖 ICacheService/IUserTokenStateProvider,
        //    必须在事件内从 RequestServices 解析(事件按请求触发,Handler 属请求作用域,根容器解析不到)。
        // 两个事件必须合并到同一 JwtBearerEvents:options.Events 整体替换,分开赋值后写覆盖先写。
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Query.ContainsKey("access_token"))
                {
                    context.Token = context.Request.Query["access_token"];
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var handler = context.HttpContext.RequestServices.GetRequiredService<JwtAuthHandler>();
                await handler.HandleAsync(context);
            }
        };
    })
    // 开放接口双 scheme(批 E 1.6/1.7):Signature(HMAC-SHA256 四头)/ApiKey(X-API-KEY)。
    // 默认 scheme 仍为 JwtBearer;自定义 scheme 不随 UseAuthentication 自动触发,
    // 由 [OpenApi] 端点经 PermissionMiddleware 先行 AuthenticateAsync 触发(租户/机构过滤设定时机,见中间件注释)
    .AddScheme<SignatureAuthenticationOptions, SignatureAuthenticationHandler>(
        SignatureAuthenticationDefaults.AuthenticationScheme, options => { })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, options => { });

builder.Services.AddAuthorization();

// SignalR 实时通信(批 F 5.13/1.8):原生 shared framework 自带(无新 NuGet);
// OnlineUserHub 在线用户注册/注销/强踢推送,路由 /hubs/onlineUser(非 /api,不经 PermissionMiddleware 门禁)
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);  // 服务端 ping 间隔
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30); // 客户端 ping 超时
    options.MaximumReceiveMessageSize = 1024 * 1024;       // 1MB(参照 10MB 口径过大,本仓管理端消息小)
})
// G33:SignalR 是独立 JSON 出口(不经 MVC AddJsonOptions),显式挂同一出口单源——
// hub 消息体若含 long id(如 userId)同样字符串化,与 HTTP 出口同构
.AddJsonProtocol(o => AdminCore.Core.Json.AdminJsonOptions.Configure(o.PayloadSerializerOptions));

var app = builder.Build();

// 启动配置自检(task_010):三环境(Development/Staging/Production)经环境后缀覆盖切换,启动即打印生效配置
// (敏感值脱敏)——环境名+配置目录确认环境覆盖生效,主库/Redis 确认加载路径。
// 置于外部依赖 I/O(缓存预热/建表/种子)之前:即使库不可达(如生产占位/沙箱无 PG),证据日志已先行产出
app.Logger.LogInformation(
    "配置自检: 环境={Env} 配置目录={ConfigDir} 应用={App} 主库={MainDb} 缓存={CacheImpl} Redis={Redis}",
    app.Environment.EnvironmentName,
    moduleConfigDir,
    app.Configuration["App:Name"] ?? "(未配置)",
    MaskConnectionString(EffectiveMainDbConnectionString(app.Configuration)),
    CacheSetup.DescribeEffectiveImpl(app.Configuration),
    MaskConnectionString(app.Configuration.GetConnectionString("Redis")));

// 链路贯通中间件(批 S-G B4a):管线**最前**,保证所有响应(含下方 ExceptionMiddleware 的错误统一出口、
// 404/静态资源等任何会启动响应的路径)都带 X-Trace-Id 响应头——与操作日志 SysLogOp.TraceId、异常档
// SysLogEx.TraceId 同源(HttpContext.TraceIdentifier),500 排障三面串联不再靠时间+路径猜
app.UseMiddleware<AdminCore.Web.Core.Middlewares.TraceIdMiddleware>();

// 全局异常中间件：放在路由之前，覆盖所有请求（业务异常/未捕获异常统一转 ApiResult）
app.UseMiddleware<AdminCore.Web.Core.Middlewares.ExceptionMiddleware>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // 批 AB 真机验证修正(生产环境缺陷,2026-09-24):UseExceptionHandler 是**内层**中间件,会抢先捕获下游异常——
    // 生产环境(非 Development)下所有 API 业务异常(AppException)都被它吞成 500 HTML,使最外层
    // ExceptionMiddleware 的统一出口(ApiResult:HTTP 200 + 体业务码)形同失效
    // (真机实证:测试版 initDb 默认租户分支抛 AppException"默认租户恒用主库…",返回的是 500 HTML 而非 code=400)。
    // 修法:错误页兜底只挂**非 /api、非 /hubs** 路径(页面/静态资源错误仍有兜底),
    // API 与 Hub 的异常留给最外层 ExceptionMiddleware → 恢复「HTTP 恒 200 + 体业务码」契约。
    app.UseWhen(
        ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/hubs"),
        branch => branch.UseExceptionHandler("/Home/Error"));
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// SPA 前端静态资源(前端适配,批):前端构建产物(Vue3+Element Plus,见仓根 web/,小写)经本服务同源自带,
// 免 CORS/免独立反代——候选目录:① SPA_DIST_DIR 环境变量(部署显式指定,最高优先)
// ② {ContentRoot}/../../web/dist(dev 源树:src/Web.Entry → 仓根/web/dist)
// ③ {BaseDirectory}/web/dist(发布布局:构建脚本把 web/dist 随发布物复制)。
// 取首个含 index.html 的目录;全部缺失 → 纯 API 形态(不影响 /health 等既有端点)
var spaRoot = ResolveSpaRoot(app);
if (spaRoot is not null)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaRoot),
        ServeUnknownFileTypes = true
    });
}

// wwwroot 静态服务(批 BU D1,MapStaticAssets 的运行时补全):MapStaticAssets 只服务**构建期清单
// (manifest)**里的资产(.NET 9+ 官方口径,运行时新增文件不可见),Local 上传档运行期写入
// wwwroot/uploads 的文件不在清单 ⇒ 直链 404。此中间件默认 FileProvider=WebRoot(wwwroot),
// 运行时实时查盘,补上该面。置认证前=Local 直链免 token 设计(雪花 Id 不可枚举);
// 与 MapStaticAssets 并存是官方推荐形态(清单资产走端点优化,运行时文件走中间件)。
app.UseStaticFiles();

// 认证必须排在授权之前:[Authorize] 端点先由 JwtBearer 解析 Bearer token 填充 User,再由授权裁决。
// 接口权限校验(契约 §2)插在认证与授权之间:依赖 User Claims 做 默认拦截/超管放行/403,
// 先于 [Authorize] 执行——未授按钮权限的请求在授权裁决前即被 403 拒绝
app.UseAuthentication();
app.UseMiddleware<PermissionMiddleware>();
app.UseAuthorization();

// 就绪探针(批 S-G B4a):实探 DB(select 1)+缓存,2 秒超时;双就绪 ⇒ 200 {status:"ok",db:"ok",cache:"<type>"},
// 任一失败 ⇒ 503 {status:"degraded",db:"ok|fail",cache:"ok|fail"}。WHY:旧静态 {status:"ok"} 在 PG/Redis
// 断连时仍绿,systemd/部署健康门探不出依赖故障(500 排障审计实锤)。状态映射抽 HealthStatus.ToResult
// (AdminCore.Core.Models,纯函数零库可测),此处只做探测接线。
// 部署健康门契约:deploy-test.sh 打 /health 且要求 200 ⇒ DB+缓存正常时恒 200;探测是请求期行为,
// 不引入任何阻塞启动的初始化(启动序列不动)。
app.MapGet("/health", async (HttpContext ctx) =>
{
    var (dbOk, cacheOk, cacheType) = await ProbeHealthDependenciesAsync(ctx.RequestServices, app.Logger);
    var probe = HealthStatus.ToResult(dbOk, cacheOk, cacheType);
    if (probe.StatusCode != StatusCodes.Status200OK)
    {
        // 降级告警只记枚举档位(依赖项失败明细已由 ProbeSafelyAsync 记 Warning,均不含连接串/异常原文)
        app.Logger.LogWarning("健康探活降级 Db={Db} Cache={Cache}", probe.Db, probe.Cache);
    }

    return Results.Json(new { status = probe.Status, db = probe.Db, cache = probe.Cache }, statusCode: probe.StatusCode);
});

// 存活探针(liveness,原 /health 静态应答迁此,批 S-G B4a):恒 200 静态 {status:"ok"},只证"进程已起且能应答"。
// live/ready 语义分离——live 挂 ⇒ 重启进程才有意义;ready(/health)挂 ⇒ 依赖故障,重启无益(等依赖恢复)。
// 存活门勿指向 /health:依赖抖动会触发误重启;就绪门(部署健康门)勿指向 /health/live:依赖故障测不出。
app.MapGet("/health/live", () => Results.Json(new { status = "ok" }));

app.MapStaticAssets();

// SPA 入口(批):GET / 直出 index.html——字面量路由优先于 MVC 默认模板路由
// ({controller=Home}/{action=Index} 会命中 HomeController 但无 Razor 视图,原为模板残留)
if (spaRoot is not null)
{
    app.MapGet("/", async context =>
    {
        await RenderSpaIndex(context, spaRoot);
    });

    // SPA history 路由回退:前端 vue-router(createWebHistory)深链刷新(/sys/user 等)——
    // 未匹配任何端点的 GET/HEAD 回落 index.html,由前端路由接管。
    // /api 与 /hubs 前缀不回退(API 404 语义/SignalR 协商路径保持原生行为,不吐 HTML)
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await RenderSpaIndex(context, spaRoot);
    });
}

// 在线用户 Hub(批 F):negotiate/WS 请求经 UseAuthentication(JwtBearer)认证,未带 token 即 401
app.MapHub<OnlineUserHub>("/hubs/onlineUser");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// 启动即监听(task_010 fixa,盲审健康检查门):先 StartAsync 让 Kestrel 绑定 5090 并对外应答 /health,
// 再执行外部依赖 I/O(缓存预热/建表/种子)。WHY 先监听:盲审三环境(Development/Staging/Production)均要求
// 端口 5090 健康,而 Production 主库为 CHANGE_ME 占位、Dev/Staging 的 PG/Redis 在验收沙箱不可达——这些场景下
// 建表/种子/缓存预热会抛异常;旧实现 I/O 在前,进程在绑定前崩溃,5090 永不健康。存活探针(/health 仅证明进程
// 已起且能应答)与就绪(建表/种子完成)解耦:外部依赖失败不阻断服务存活。
await app.StartAsync();

try
{
    // 缓存预热 + 启动自检(task_007):立即执行一次写删往返——缓存配置错误在启动即暴露(降级告警,不阻断存活)
    var cache = app.Services.GetRequiredService<ICacheService>();
    await cache.SetAsync("__warmup__", "1");
    await cache.RemoveAsync("__warmup__");
    app.Logger.LogInformation("缓存预热完成");

    // SqlSugar 客户端(AddSqlSugar 注册的共享单例):建表/种子按实体特性多库路由,全靠它
    var db = app.Services.GetRequiredService<ISqlSugarClient>();

    // 实体程序集全集(建表 + 种子共用):业务实体在 Business、系统实体在 Core,漏传任一侧即漏建。
    // 批 AS 口径留痕:原三处调用传的是 { SysUser 程序集, SysConfig 程序集, Business 程序集 }——
    // SysConfig 与 SysUser 同属 Core 程序集(同一 Assembly),故此处去重为 { Core, Business },实体集与种子集不变
    // (去重前 InitSeeds 会把 Core 的种子类型扫两遍,同一命令式种子在单次调用内执行两次)。
    var entityAssemblies = new[] { typeof(SysUser).Assembly, typeof(AdminCore.Business.Class1).Assembly };

    // 机构数据范围过滤器实体注册(task_013):显式传全部实体所在程序集(框架自保兜底仅扫 Core,
    // 漏传 Business = 业务实体不在过滤范围)——注册与建表同一批,业务实体继承 EntityBaseData 即自动获得数据权限。
    // 批 AS:本件是**进程内注册**(无跨实例副作用),故不入集群锁内——每个实例都要有自己那一份,顺序前移无行为差。
    SqlSugarFilter.Init(typeof(SysUser).Assembly, typeof(AdminCore.Business.Class1).Assembly);

    // 启动时 CodeFirst 建表(契约 §5.1)+ 增量种子:按 DbSettings:EnableIncreTable 分流(见 SqlSugarSetup 注释)。
    // 批 AS(集群基线①):整段收进 StartupSchemaInitializer——多实例同刻启动时**只有取到 sys_startup_schema 锁的
    // 实例执行 DDL/种子**,其余实例整段跳过(避免 PG 并发 ALTER/CREATE 互等与 42701/42P07、种子主键冲突);
    // 单实例与 Memory 档行为等价(锁必可得,缓存不可用则保守照常执行)。三态返回值供下一段裁决"需表已存在"的启动件。
    var schemaOutcome = await StartupSchemaInitializer.InitAsync(
        db, cache, app.Logger, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", entityAssemblies);

    // 批 AL:启动期显式解析一次本地化服务 ⇒ L.Resolver 在首个请求前就绪(AppException 资源键可翻译)
    // 历史留痕(G23,已销账):批 AL 时代该服务同接口**双注册**(显式 Singleton + [Service] 缺省 Scoped,.NET 按
    // 最后注册者生效取 Scoped)⇒ 从 root provider 直解在 Development 抛 "Cannot resolve scoped service",
    // 被外层 catch 收成"启动初始化降级"并连带其后的字段权限迁移永不执行(实测潜伏数月)。
    // 批 BP D1 已拆双注册(仅余 AddAdminLocalization 显式 Singleton)⇒ 经作用域解析对本件已无必要,
    // 但保留此形态:对 Singleton 完全合法(同一实例),且即使未来注册面再出 Scoped,此处也不会复发 G23 失败模式。
    using (var localizeScope = app.Services.CreateScope())
    {
        localizeScope.ServiceProvider.GetRequiredService<ILocalizationService>();
    }

    // 批 AK:字段权限表级开关升级迁移(开关表空 && 历史授权存在 → 批量入册;幂等,失败不阻断启动)
    // 批 AS:他实例持锁、本实例跳过建表时,表可能尚未就绪 ⇒ 只在"本实例确实执行过 schema 段"时跑
    // (本迁移幂等,跳过方下次重启补跑;不引入等待,保持 task_010 存活/就绪解耦口径)
    if (schemaOutcome != StartupSchemaInitOutcome.SkippedHeldByOther)
    {
        try
        {
            // 缺陷修复(G23):`SysRoleTableService` 同为 Scoped ⇒ 同样必须经作用域解析(WHY 见上一段)
            using (var migrateScope = app.Services.CreateScope())
            {
                var roleTableSvc = migrateScope.ServiceProvider.GetRequiredService<SysRoleTableService>();
                await roleTableSvc.MigrateLegacyScopesAsync();
            }

            // 可观测性(缺陷修复 G23):该步骤此前**成功也无任何日志**——正是"从未执行"能潜伏数月而无人察觉的原因;
            // 幂等语义下正常空转也要留痕(与失败告警一并构成"跑没跑"的可判据)。
            app.Logger.LogInformation("字段权限升级迁移完成(开关表空且有历史授权时已批量入册;否则按幂等空转)");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "字段权限表级开关升级迁移失败(启动继续;旧授权将不生效,可手动重配)");
        }

        // G32 命令式启动种子挂点:声明式 [IncreSeed] 已在上方 StartupSchemaInitializer 锁段内跑完
        // (HealColumnCase→InitTables→InitSeeds→InitSeedData)⇒ 此处接命令式种子,按 IStartupSeed.Order 串行。
        // WHY 同在上面的 schemaOutcome 守卫内:命令式种子与 [IncreSeed] 同属"需表已就绪"的 DB 侧启动件——
        // 他实例持锁时本实例整段跳过,避免与持锁方的建表/种子并发互踩(与字段权限迁移同判据)。
        // 种子缺省 Scoped ⇒ 从本作用域解析(与上一段 G23 教训同口径,勿从 root provider 直解)。
        // 失败口径:runner 内部缺省 fail-fast(响亮),异常由外层 catch 收敛为"启动降级",存活/就绪解耦不变。
        using (var seedScope = app.Services.CreateScope())
        {
            var seedRunner = seedScope.ServiceProvider.GetRequiredService<StartupSeedRunner>();
            await seedRunner.RunAllAsync(seedScope.ServiceProvider);
        }

        // 批 S-G B2b:唯一性底座——业务唯一键部分唯一索引(WHERE "IsDelete"=false,软删行不占键、可同键重建)。
        // WHY 接在 InitTables(锁段)之后、同 schemaOutcome 守卫内:索引依赖表/列已存在;他实例持锁方整段跳过,
        // 本实例下次重启补跑(与字段权限迁移/命令式种子同判据)。
        // 失败口径:EnsureIndexesAsync 逐条审计+创建并自吞单条异常(重复/异常 LogError 入跳过清单,fail-loud 可检索),
        // 此处 try/catch 只兜方法级意外——「唯一性暂缺」不阻断启动(服务层 check-then-act 仍在兜底),响亮告警即可。
        try
        {
            var integrity = await SchemaIntegrityInitializer.EnsureIndexesAsync(db, app.Logger);
            app.Logger.LogInformation(
                "唯一性底座汇总:已建 {Created},已存在 {Existed},跳过 {Skipped}(共 {Total})",
                integrity.CreatedIndexes.Count,
                integrity.AlreadyExistingIndexes.Count,
                integrity.SkippedIndexes.Count,
                integrity.CreatedIndexes.Count + integrity.AlreadyExistingIndexes.Count + integrity.SkippedIndexes.Count);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "唯一性底座执行失败(启动继续;唯一性暂由服务层 check-then-act 兜底,须排查)");
        }
    }
}
catch (Exception ex)
{
    // 外部依赖(PG/Redis)不可达/占位(如生产 CHANGE_ME)→ 降级:服务已监听、健康检查可达;
    // 失败响亮告警(不静默),就绪缺口由后续业务请求/巡检体现(存活与就绪解耦)
    app.Logger.LogError(ex, "启动初始化降级(缓存预热/建表/种子未完成,服务保持监听,健康检查可用)");
}

await app.WaitForShutdownAsync();

// 加载 Business/Configuration 模块配置(task_010,参考源 baseadmin 模式,零 Furion 原生实现):
// 每模块(App/Database/JWT/Cache/Logging)一个 json + 环境后缀覆盖 {Module}.{Environment}.json;
// 基础在前、环境覆盖在后(同键后加载源覆盖先加载源,标量与数组索引合并均实测成立);
// 每文件 optional(无该环境覆盖文件不报错)+ reloadOnChange(开发热加载)。
// basePath 定位:优先源树 Business/Configuration(dev:content root 与 Business 为兄弟目录,
// 源树编辑即时生效);发布布局回落输出目录 Configuration(Web.Entry.csproj 的 Content 项
// 把 Business/Configuration 复制过去,见 csproj 注释)。
static string LoadModuleConfiguration(ConfigurationManager configuration, string contentRoot, string environment)
{
    var candidates = new[]
    {
        // 源树布局:Web.Entry 与 Business 为兄弟目录(src/Web.Entry → ../Business/Configuration)
        Path.Combine(contentRoot, "..", "Business", "Configuration"),
        // 发布布局:输出目录 Configuration(csproj Content Link 复制而来)
        Path.Combine(AppContext.BaseDirectory, "Configuration")
    };

    var dir = candidates.FirstOrDefault(Directory.Exists);
    if (dir is null)
    {
        // 配置目录缺失属部署缺陷:启动即快速失败,不静默缺配置运行
        throw new InvalidOperationException($"未找到模块配置目录:{string.Join(" / ", candidates)}");
    }

    foreach (var module in new[] { "App", "Database", "JWT", "Cache", "Logging" })
    {
        configuration.AddJsonFile(Path.Combine(dir, $"{module}.json"), optional: true, reloadOnChange: true);
        configuration.AddJsonFile(Path.Combine(dir, $"{module}.{environment}.json"), optional: true, reloadOnChange: true);
    }

    return dir;
}

// 取生效主库连接串:ConnectionConfigs 中 ConfigId=main 项;
// 缺 main 时回落存量 ConnectionStrings:Default(与 SqlSugarSetup.LoadConnectionConfigs 口径一致)
static string? EffectiveMainDbConnectionString(IConfiguration config)
{
    foreach (var child in config.GetSection("ConnectionConfigs").GetChildren())
    {
        if (string.Equals(child["ConfigId"], "main", StringComparison.OrdinalIgnoreCase))
        {
            return child["ConnectionString"];
        }
    }

    return config["ConnectionStrings:Default"];
}

// SPA 根目录解析(前端适配):候选见 UseStaticFiles 处注释;GetFullPath 归一化后取首个含 index.html 者
static string? ResolveSpaRoot(WebApplication app)
{
    var candidates = new[]
    {
        Environment.GetEnvironmentVariable("SPA_DIST_DIR"),
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist"),
        Path.Combine(AppContext.BaseDirectory, "web", "dist")
    };

    return candidates
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => Path.GetFullPath(c!))
        .FirstOrDefault(d => File.Exists(Path.Combine(d, "index.html")));
}

// SPA index.html 直出(入口路由与 history 回退共用):缺失 → 404(不吐 HTML 空壳)
static async Task RenderSpaIndex(HttpContext context, string spaRoot)
{
    var indexPath = Path.Combine(spaRoot, "index.html");
    if (!File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(indexPath);
}

// /health 依赖实探(B4a):DB=select 1(经 ISqlSugarClient,CreateScope 解析——单例 SqlSugarScope,同实例);
// 缓存=Memory 档(进程内,无远端可断)视为就绪记 "memory";Redis 档做一次 写→读→删 往返(ICacheService
// 既有接口无 ping 能力,**不新增接口方法**,写读删即最真探活;探针键 5 秒自过期,Remove 失败也不残留)。
// 两项并行、各自 2 秒超时,超时/异常一律布尔化 fail——异常原文只进日志(Warning),不进响应。
static async Task<(bool DbOk, bool CacheOk, string CacheType)> ProbeHealthDependenciesAsync(IServiceProvider services, ILogger logger)
{
    using var scope = services.CreateScope();
    var scoped = scope.ServiceProvider;

    var dbTask = Task.Run(async () =>
    {
        var db = scoped.GetRequiredService<ISqlSugarClient>();
        var scalar = await db.Ado.GetScalarAsync("select 1");
        return scalar is not null;
    });

    var cacheTask = Task.Run<(bool Ok, string Type)>(async () =>
    {
        var cache = scoped.GetRequiredService<ICacheService>();
        if (cache is MemoryCacheService)
        {
            return (true, "memory");
        }

        // Redis 档:写读删往返,回读值等于写入值才算就绪(连接断开/只写不可读均判 fail)
        var key = "__health__:" + Guid.NewGuid().ToString("N");
        await cache.SetAsync(key, "1", TimeSpan.FromSeconds(5));
        var read = await cache.GetAsync<string>(key);
        await cache.RemoveAsync(key);
        return (read == "1", "redis");
    });

    var dbOk = await ProbeSafelyAsync(dbTask, logger, "db") ?? false;
    var cacheProbe = await ProbeSafelyAsync(cacheTask, logger, "cache");
    return (dbOk, cacheProbe?.Ok ?? false, cacheProbe?.Type ?? string.Empty);
}

// 探测安全壳:2 秒超时(TimeoutException)与探测异常统一归 fail 并记 Warning——探针只输出就绪布尔,
// 不外泄异常原文/连接串(与 HealthStatus.ToResult 的"降级不带原文"约定配套)
static async Task<T?> ProbeSafelyAsync<T>(Task<T> probe, ILogger logger, string name) where T : struct
{
    try
    {
        return await probe.WaitAsync(TimeSpan.FromSeconds(2));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "健康探活失败: {Dependency}(2 秒超时或异常;响应仅回 fail,不携带异常原文)", name);
        return null;
    }
}

// 敏感值脱敏(安全红线:含密码的连接串不得明文进日志):
// PG 连接串 Password= 段 / Redis password= 段 / URI :user:pass@ 形式 → ***,其余段保留(排查需见 host/库名)
static string MaskConnectionString(string? conn)
{
    if (string.IsNullOrWhiteSpace(conn))
    {
        return "(未配置)";
    }

    var masked = System.Text.RegularExpressions.Regex.Replace(conn!, "(?i)password=[^;\\s]*", "password=***");
    return System.Text.RegularExpressions.Regex.Replace(masked, "://[^@/\\s]+@", "://***@");
}

/// <summary>
/// 入口类显式可见性(S-G B4b):top-level statements 生成的 Program 默认 internal,
/// WebApplicationFactory&lt;Program&gt; 需要 public 部分类才能定位宿主入口(标准做法,零行为变化)。
/// </summary>
public partial class Program
{
}
