# 批 I 移植调研:5.1 异常档 SysLogEx(+sys_oplog 开关) / 5.2 文件日志通道

> 依据件(约定 #13):批 I 实施前对参照仓 AdminNetPro 的对应实现逐点调研,落盘留痕。
> 参照仓:`/data/workspace/AdminNetPro/Admin.NET.Pro.HHBase/Admin.NET/`(只读,#7)。
> 调研日期:2026-09-16｜批 I 范围:81 功能点 5.1(🟡缺异常档/开关/请求体大字段)/5.2(🟡缺文件日志通道)。

## 0. 移植落点映射表(速查)

| 参照仓(AdminNetPro) | 本仓落点 | 说明 |
|---|---|---|
| `Entity/SysLogEx.cs`([SysTable][LogTable],继承 SysLogVis) | `Core/Entities/SysLogEx.cs`([IncreTable][LogTable],继承 EntityBase) | 本仓无 SysLogVis 基类(平铺实体范式);字段裁剪见 §3 |
| `Logging/LogExceptionHandler.cs`(IGlobalExceptionHandler→发布事件) | `Web.Core/Middlewares/ExceptionMiddleware.cs` 未处理异常分支→发布 "ex_log" 事件 | 参照 IGlobalExceptionHandler 是 Furion 机制;本仓 5.17 异常出口=ExceptionMiddleware,事件链复用 task_014 EventBus |
| `AppEventSubscriber.CreateExLog`(事件→落库) | `Core/Services/Log/ExLogSubscriber.cs`([EventSubscribe("ex_log")]) | 与 OpLogSubscriber 同范式(总线后台异步落库,[LogTable] 选库) |
| `Service/Log/SysLogExService.cs`(Page/Detail,IgnoreColumns 大字段) | `Core/Services/Log/SysLogExService.cs`+`SysLogExController` | Page 列表投影排除三大字段(参照 IgnoreColumns 口径);Detail 全字段 |
| `Const/ConfigConst.SysOpLog="sys_oplog"`+种子 SysConfigSeedData | `SysConfigService.SysOpLog` 常量＋`Core/SeedData/SysConfigSeedData.cs`(新,[IncreSeed]) | 开关值 sys_oplog 行种子(True 内置) |
| `DatabaseLoggingWriter` L169 开关检查(关→不落库) | `OpLogFilter` 发布前检查(关→不发事件) | 参照检查在落库前;本仓事件发布本身轻量,前置检查=等价语义+零空跑 |
| `Log.Error("全局异常", ex)`(Furion 文件日志) | `FileLoggerProvider`(自建 ILoggerProvider,按天切分) | 参照文件通道靠 Furion 内建 Serilog;本仓零 Furion→原生 Logging 抽象上自建(§5) |

## 1. 参照 SysLogEx 实体(逐条)

```csharp
[SugarTable(null, "系统异常日志表")] [SysTable] [LogTable]
public partial class SysLogEx : SysLogVis   // 继承访问日志基类(EntityTenantId 系)
{
    string? HttpMethod;          // 请求方式
    string? RequestUrl;          // 请求地址(大字段)
    string? RequestParam;        // 请求参数(大字段)
    string? ReturnResult;        // 返回结果(大字段)
    int? EventId;                // 事件Id(ILogger 事件上下文)
    int? ThreadId;               // 线程Id
    string? TraceId;             // 请求跟踪Id(128)
    string? Exception;           // 异常信息(大字段)
    string? Message;             // 日志消息Json(大字段)
}
```

SysLogVis 基类携带:ControllerName/ActionName/DisplayTitle/Status/RemoteIp/Location/Longitude/Latitude/Account/RealName/LogDateTime/LogLevel 等(访问日志字段集,部分异常场景无意义)。

## 2. 参照异常处理链(逐条)

```csharp
// Logging/LogExceptionHandler.cs OnExceptionAsync
Log.Error("全局异常", context.Exception);   // ① 文件日志
var sysLogEx = new SysLogEx {
    Account/RealName = App.User Claim,
    ControllerName = actionMethod.DeclaringType.FullName,
    ActionName = actionMethod.Name,
    DisplayTitle = DisplayNameAttribute(若标注),
    Message = $"异常信息：{ex.Message} \n异常来源：{ex.Source} \n堆栈信息：{ex.StackTrace}",
    RequestParam = ex.TargetSite.GetParameters().ToString(),  // 注:实为方法参数元信息,非请求体
    HttpMethod/RequestUrl/RemoteIp/Browser/TraceId/ThreadId,
    LogDateTime = DateTime.Now, LogLevel = LogLevel.Error
};
await _eventPublisher.PublishAsync(new ChannelEventSource(nameof(AppEventSubscriber.CreateExLog), sysLogEx)); // ② 事件→落库
await _eventPublisher.PublishAsync(nameof(AppEventSubscriber.SendErrorMail), sysLogEx);                    // ③ 邮件告警(垂直,不迁)
```

要点:
- **异常全文拼进 Message**(异常信息/来源/堆栈三段);实体另有 Exception 字段但处理链未赋值(参照自身字段使用漂移)。
- **RequestParam 实为方法参数元信息**(`TargetSite.GetParameters()`),并非请求体——参照口径本就不记 body。
- 事件链异步落库(与 OpLog 同机制);邮件告警 SendErrorMail 属垂直能力(参照有 SMTP 配置),本仓不迁(裁决)。

## 3. 本仓 SysLogEx 字段裁剪(参照→本仓)

| 参照字段 | 本仓 | 裁决理由 |
|---|---|---|
| ControllerName/ActionName | ✅ 保留 | 路由层定位(同 SysLogOp 口径,取 RouteValues) |
| HttpMethod/RequestUrl/RemoteIp | ✅ 保留 | RequestUrl 大字段(text) |
| TraceId | ✅ 保留(128) | HttpContext.TraceIdentifier(ASP.NET Core 原生) |
| ThreadId | ✅ 保留 | Environment.CurrentManagedThreadId |
| Account/RealName | ✅ 保留 | 经 UserManager 门面(批 D 1.14 口径) |
| Message(三段拼接全文) | ✅ 落 `Exception` 字段 | 参照处理链实际拼进 Message;本仓用语义正确的 Exception 字段承载同内容(参照字段漂移修正) |
| Exception(实体声明未赋值) | (并入上行) | 不另建冗余字段 |
| RequestParam | ✅ 保留(text 大字段) | 存查询串(Request.QueryString 全量)+路由值;**不缓冲 body**(参照口径本就不记 body;body 重读需 EnableBuffering 侵入,最小必要 #8——裁决) |
| ReturnResult | ✅ 保留(text 大字段) | 存中间件实际写出的响应 JSON(异常场景=统一文案) |
| LogLevel | ✅ 保留(32,恒 "Error") | 保留对齐参照(未来 Warning 级入档可扩展) |
| EventId | ❌ 不建 | 本仓异常不经 ILogger 事件上下文,无源(裁决) |
| Status | ❌ 不建 | 异常档恒失败,冗余(裁决) |
| DisplayTitle | ❌ 不建 | 本仓无 DisplayNameAttribute 机制(裁决) |
| Location/Longitude/Latitude | ❌ 不建 | 地理垂直能力(裁决) |
| LogDateTime | ❌ 不建 | EntityBase.CreateTime(AOP 自动)=日志时间,同口径不重复(裁决) |
| EntityTenantId 基类 | EntityBase | 本仓日志实体统一 EntityBase(全租户可见运维视角,SysLogOp 同口径) |

## 4. 参照 sys_oplog 开关(逐条)

```csharp
// Const/ConfigConst.cs
public const string SysOpLog = "sys_oplog";   // 记录操作日志

// SeedData/SysConfigSeedData.cs(种子行)
new SysConfig{ Id=1300000000141, Name="记录操作日志", Code="sys_oplog", Value="True", SysFlag=YesNoEnum.Y, Remark="是否记录操作日志", GroupCode=SysDefaultGroup }

// Logging/DatabaseLoggingWriter.cs L169(op 日志落库前置检查)
if (!await _sysConfigService.GetConfigValueByCode<bool>(ConfigConst.SysOpLog)) return;   // 关→不落库
```

要点:
- 开关缺失时参照口径=false(不记录);参照靠种子保证正常路径 True。
- 本仓 `SysConfigService.GetConfigValueByCodeAsync(code)` 已建(批 D 4.1,缓存 7 天+写路径失效)——**直接复用**(#11)。
- 本仓 sys_config 存量表**无数据**(批 D 手工 fast-default DDL 迁移,无种子)→ 必须补种子,否则开关缺失。
- **本仓缺失口径裁决=fail-open(记录)**:`bool.TryParse(value) && !on → skip`——null/解析失败不 skip。理由:操作日志是审计面,漏记是隐性缺陷;种子保证正常路径 True 时行为与参照一致。

## 5. 参照文件日志通道 → 本仓实现(5.2)

参照:Admin.NET 的 `Log.Error/Log.Information`(Furion 内建 ILogger,底层 Serilog)按天写文件,无显式配置(默认 wwwroot/Logs 按日期目录)。

本仓(零 Furion)实现口径:
- **复用 Microsoft.Extensions.Logging 抽象**(#11):自建 `FileLoggerProvider : ILoggerProvider`+`FileLogger : ILogger`,经 `builder.Logging.AddFileLogger(...)` 装配(与内建 Console provider 并列多通道)。
- **按天切分**:文件名 `logs/app-yyyy-MM-dd.log`(日期入文件名=按天切分;单目录平铺,不按参照的日期子目录——管理端日志量小,平铺更直观)。
- **写入**:单 StreamWriter 惰性打开(append),写前检查日期→跨天重开;lock 保护;每条 Flush(管理端日志频率低,逐条刷盘可接受,#8)。
- **格式**:`yyyy-MM-dd HH:mm:ss.fff [Level] Category: Message` + 异常行(与 Console 通道信息对齐)。
- **配置**(`Logging.json` 加段):`Logging:File`={ Enabled(bool,缺省 true)/ Path(缺省 "logs")/ MinimumLevel(缺省 Information) };provider 级 MinimumLevel 过滤(与内建 LogLevel 过滤器互补:内建管 category 级,provider 管文件通道缺省门槛)。
- 不引第三方 NuGet(Serilog):零 Furion 口径=原生 Logging 抽象上最小自建(裁决;~150 行)。

## 6. 查询端点范式(参照 SysLogExService.Page)

```csharp
// 参照 Page 关键口径
.WhereIF(StartTime, u => u.CreateTime >= input.StartTime)
.WhereIF(EndTime, u => u.CreateTime <= input.EndTime)
.WhereIF(Account, u => u.Account.StartsWith(input.Account))
.WhereIF(ControllerName, u => u.ControllerName.StartsWith(input.ControllerName))
.WhereIF(RemoteIp, u => u.RemoteIp == input.RemoteIp)
.IgnoreColumns(u => new { u.RequestParam, u.ReturnResult, u.Message })   // ★ 列表不载大字段
```

- **IgnoreColumns=「请求体大字段」缺口的完整口径**:大字段落库(text),列表查询排除(防慢查询),Detail 端点全字段。
- 本仓 SqlSugar 等价:Page 用 record 投影(Select 排除三大字段);Detail 单独查询全字段。
- 参照 WhereIF 用 StartsWith,本仓 SysLogOp 用 Account 精确/ControllerName Contains——**沿用本仓既有口径**(Account 精确、ControllerName 包含、RemoteIp 精确、CreateTime 闭区间),不照抄 StartsWith(本仓 SysLogOp/SysLogLogin 同口径,裁决)。

## 7. 菜单/权限(种子)

| Id | Pid | Name | Title | Type |
|---|---|---|---|---|
| 10128 | 10104 | sysLogEx/page | 异常日志查询 | 3(按钮) |
| 10129 | 10104 | sysLogEx/detail | 异常日志详情 | 3(按钮) |

Pid=10104(「日志管理」菜单,批 D 已建,含 sysLogOp/page 10105 + sysLogLogin/page 10106);接段尾 10127(批 H)之后。

## 8. 验证口径

- 单测(真实 PG):SysLogEx 落库(事件→订阅者)/Page 大字段不载+过滤/Detail 全字段/oplog 开关关→OpLog 不落库(经 filter 解析口径)/SysConfigSeedData 幂等。
- 端到端:`POST /api/demo/crash`(除零→DivideByZeroException,既有端点)→ 等总线落库 → `GET /api/sysLogEx/page` 命中(含 TraceId/异常全文);日志目录出现 `app-yyyy-MM-dd.log` 且含未处理异常堆栈行;oplog 开关 False → 新请求 sys_log_op 不增长 → 恢复 True。
- AppException 不记异常档(业务异常预期分支,5.17 口径)——`/api/demo/error` e2e 反向验证。

## 9. 本批自行裁决清单(呈报葫芦爷爷)

| # | 裁决 | 依据 |
|---|---|---|
| ① | 异常档仅记未处理异常;AppException(业务异常)不记档 | 5.17 口径 AppException=预期分支(Warning 无堆栈);参照 IGlobalExceptionHandler 也仅全局异常入口 |
| ② | 异常全文(信息+来源+堆栈)落 `Exception` 字段,不另建 Message 字段 | 参照处理链实拼进 Message 而 Exception 字段闲置(自身漂移);本仓取语义正确单字段 |
| ③ | RequestParam 存方法参数,不缓冲请求 body | 参照口径本就不记 body(记方法参数元信息);body 重读需 EnableBuffering 侵入请求链路(#8)。**批 AZ 更新(2026-09-30,葫芦爷爷拍板「按参照口径补」)**:RequestParam=方法参数 JSON(内存 `ActionArguments`)、ReturnResult=返回对象 JSON(内存 `ObjectResult.Value`)——仍零 EnableBuffering、不读 HTTP body,参照口径裁决不变;两大字段仅详情载(列表投影排除),鉴权登录端点(Auth\|Login/LoginOtp)请求参数豁免不落库(密码/动态码绝不入审计库) |
| ④ | EventId/Status/DisplayTitle/Location/Longitude/Latitude/LogDateTime 七字段不建 | 无源/冗余/垂直/与 CreateTime 重复(§3 逐条) |
| ⑤ | 邮件告警(SendErrorMail)不迁 | 垂直能力(参照有 SMTP 配置);异常档+日志已覆盖排查面 |
| ⑥ | 文件通道自建 FileLoggerProvider(零第三方),按天切分平铺目录 | 零 Furion 口径复用原生 Logging 抽象(#11);Serilog 引入依赖不取(#8) |
| ⑦ | sys_oplog 开关缺失/解析失败=fail-open(记录) | 审计面漏记是隐性缺陷;种子保证正常路径与参照一致 |
| ⑧ | oplog 开关检查前置到 OpLogFilter 发布前(而非落库时) | 参照在落库前检查;本仓前置=等价语义+关时零事件总线空跑 |
| ⑨ | 查询过滤沿用本仓口径(Account 精确/ControllerName 包含),不照抄参照 StartsWith | 本仓 SysLogOp/SysLogLogin 既有同口径,一致性优先 |
