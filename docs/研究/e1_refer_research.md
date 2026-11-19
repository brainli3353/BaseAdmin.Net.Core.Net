# 参照仓 AdminNetPro「开放接口」实现调研(移植唯一依据)

> **闭合注（2026-09-29 审计）**：本稿移植目标已批 E 落地；下文落点与文件清单为讨论时点（2026-08-26）口径，可能与实际实现有出入——以代码为准。

> 调研对象:`/data/workspace/AdminNetPro/Admin.NET.Pro.HHBase/Admin.NET`(Furion + SqlSugar)。
> 目标仓:`/data/workspace/BaseAdminCore`(ASP.NET Core 10,零 Furion,SqlSugar 5.1.4.217)。
> 本文所有「关键行引用」格式为 `文件名:行号`(路径相对参照仓 `Admin.NET/`)。
> 结论先行:开放接口 = 一张身份表 `SysOpenAccess` + 两个自定义 AuthenticationHandler(HMAC-SHA256 签名 / API Key)+ 一个管理服务(增删改查 + 生成签名/密钥)。参照实现强依赖 Furion(`App` 静态服务定位器、`IDynamicApiController`、`ITransient`、`Oops.Oh`、`[ApiDescriptionSettings]`、`[NonAction]`、Mapster `.Adapt`、`SqlSugarPagedList`),目标仓需逐项替换。

---

## 0. 移植落点映射表(速查)

| 参照(AdminNetPro) | 目标仓(BaseAdminCore)落点 | 目标仓约定 |
|---|---|---|
| `Entity/SysOpenAccess.cs` | `src/Core/Entities/SysOpenAccess.cs` | 继承 `EntityBase`;`[SugarTable("sys_open_access","开放接口身份表")]`(显式 snake_case,见 §1);补 `[IncreTable]`(参照缺,见 §8-R1) |
| `SignatureAuth/*`(8 文件) | `src/Web.Core/Auth/SignatureAuth/*` | 鉴权支撑放 Web.Core;`AuthenticationHandler<T>` 原生 ASP.NET Core,零 Furion 可直接移植 |
| `ApiKeyAuth/*`(2 文件) | `src/Web.Core/Auth/ApiKeyAuth/*` | 同上 |
| `Service/OpenAccess/SysOpenAccessService.cs` | `src/Core/Services/OpenAccess/SysOpenAccessService.cs` + `Dto/`(3 文件就近) | 服务放 `src/Core/Services/{Domain}/`;DTO 就近 `Dto/` |
| 权限点(动态路由) | 控制器 `[MenuPerm("sysOpenAccess/xxx")]` + `src/Core/SeedData/SysMenuSeedData.cs` 按钮种子 | 参照用 Furion 动态 API 隐式路由;目标用 `[ApiController]+[Route]` 手写控制器 |
| `Application/.../DemoOpenApi.cs` | `src/Web.Entry/Controllers/DemoOpenApiController.cs`（实际实现：`OpenApiDemoController.cs`） | 控制器放 `src/Web.Entry/Controllers/` |
| `Program.cs` 注册行(`Startup.cs:83-86`) | `src/Web.Entry/Program.cs`(接在 `.AddJwtBearer(...)` 之后) | 原生 `AddScheme<...>` |
| 前端 `md5Signature.ts` / `data-signature.ts` | **无需移植** | 这两个是无关的 MD5 工具(见 §2.1 末尾),开放接口的 HMAC 签名由后端 `GenerateSignature` 方法定义,调用方按 §6 规格自行实现 |

---

## 1. SysOpenAccess 实体字段表

参照源:`Admin.NET.Core/Entity/SysOpenAccess.cs`

类级特性:`[SugarTable(null, "开放接口身份表")]`(SysOpenAccess.cs:12,`null` = 表名由类名推导,Admin.NET 命名约定产出 `sys_open_access`)+ `[SysTable]`(System 表标记)+ `[SugarIndex("i_{table}_a", nameof(AccessKey), OrderByType.Asc)]`(SysOpenAccess.cs:14,AccessKey 升序索引)。继承 `EntityBase`(雪花主键 `long Id` + 审计 + 软删)。

| 字段名 | 类型 | 含义 | 默认值 | 索引/约束 | 关键行 |
|---|---|---|---|---|---|
| `Id` | `long` | 雪花主键(继承 `EntityBaseId`) | AOP 自动填 | 主键 `IsPrimaryKey, IsIdentity=false` | EntityBase.cs:17 |
| `AccessKey` | `string` | 身份标识(签名用 accessKey;API Key 模式下是 `AccessSecret` 的反向,见 §3) | — | `[Required, MaxLength(128)]`;升序索引 `i_{table}_a`;`ColumnDescription="身份标识", Length=128`;`virtual` | SysOpenAccess.cs:20-22 |
| `AccessSecret` | `string` | 密钥(HMAC 签名密钥 / API Key 本身) | — | `[Required, MaxLength(256)]`;`Length=256`;`virtual` | SysOpenAccess.cs:27-29 |
| `BindTenantId` | `long` | 绑定租户 Id | `0`(值类型) | `ColumnDescription="绑定租户Id"` | SysOpenAccess.cs:34-35 |
| `BindTenant` | `SysTenant` | 绑定租户导航(OneToOne, by BindTenantId) | — | `[Navigate(NavigateType.OneToOne, nameof(BindTenantId))]`;双 JsonIgnore | SysOpenAccess.cs:40-43 |
| `BindUserId` | `long` | 绑定用户 Id | `0` | `ColumnDescription="绑定用户Id"`;`virtual` | SysOpenAccess.cs:48-49 |
| `BindUser` | `SysUser` | 绑定用户导航(OneToOne, by BindUserId) | — | `[Navigate(...)]`;参照**未** JsonIgnore(注释掉,第 54-55 行)→ 序列化会带出用户对象 | SysOpenAccess.cs:56-57 |
| 审计/软删字段 | — | `CreateTime`/`UpdateTime`/`CreateUserId`/`CreateUserName`/`UpdateUserId`/`UpdateUserName`/`IsDelete`/`DeleteTime` | AOP 自动填 | 继承 `EntityBase` | EntityBase.cs:24-91 |

> ⚠ **目标仓移植注意**:
> - 参照 `[SugarTable(null, ...)]`,目标仓 `SysConfig.cs` 等用**显式** `[SugarTable("sys_config", ...)]`,故目标写 `[SugarTable("sys_open_access", "开放接口身份表")]`。
> - 参照实体**没有** `[IncreTable]`;目标仓约定「实体默认 `[IncreTable]`」且 `Program.cs` 的 `db.InitTables` 按 `[IncreTable]` 分流建表(见 §8-R1),目标须补 `[IncreTable]`。
> - 参照 `BindUser` 导航未忽略序列化(会输出整个 SysUser,含 `Password` 哈希!)。目标仓移植时**强烈建议**给 `BindUser`/`BindTenant` 加 `[System.Text.Json.Serialization.JsonIgnore]`(参照 `BindTenant` 已加、`BindUser` 漏加,属参照仓疏漏,不要照抄)。

---

## 2. 签名认证 scheme 完整流程(HMAC-SHA256)

参照源:`Admin.NET.Core/SignatureAuth/`(8 文件)+ 注册在 `SysOpenAccessService.GetSignatureAuthenticationEventImpl()`。

### 2.1 Header 字段(精确字面量)

`SignatureAuthenticationHandler.HandleAuthenticateAsync()` 读取(Header 大小写不敏感,参照用小写读取):

| Header 字面量 | 含义 | 关键行 |
|---|---|---|
| `accessKey` | 身份标识 | SignatureAuthenticationHandler.cs:40 |
| `timestamp` | 时间戳(参照注释「精确到秒」,实际兼容毫秒,见下) | :41 |
| `nonce` | 唯一随机数(防重放) | :42 |
| `sign` | 签名值(Base64) | :43 |

> ⚠ 请求 **body / query 都不参与签名**——签名串只含 method+path+accessKey+timestamp+nonce(见 §2.2)。因此同一 path 下 query 不同也算"签名有效",安全边界靠 nonce+timestamp 窗口兜底。移植时保持此口径即可(若要保护 body 需改算法,超出现有参照)。

### 2.2 签名串拼接顺序 + HMAC 算法细节

拼接(关键行:SignatureAuthenticationHandler.cs:122-131,`GetMessageForSign`):

```
签名串 = "{METHOD}&{URL}&{accessKey}&{timestamp}&{nonce}"
```
- `METHOD` = `context.Request.Method`(大写,如 `GET`/`POST`)
- `URL` = `context.Request.Path`(仅路径,无协议/域名/查询串,以 `/` 开头,如 `/api/demo/hello`)
- `accessKey` / `timestamp` / `nonce` = Header 原值(字符串)

HMAC(关键行:SignatureAuthenticationHandler.cs:139-147,`SignData`):
- 算法 = **HMAC-SHA256**;密钥 = `accessSecret` 的 UTF-8 字节;输入 = 签名串 UTF-8 字节;**输出 = Base64 字符串**(不是 hex)。
- 服务端比对用 `!=`(非恒定时间比较,存在时序侧信道,参照即如此;目标仓可保持或用 `CryptographicOperations.FixedTimeEquals` 加固)。

时间戳窗口 / 自动判秒/毫秒(关键行:SignatureAuthenticationHandler.cs:54-66):
- 先 `long.TryParse(timestampStr, out timestamp)`,失败 → 失败"timestamp 值不合法"。
- `bool isMilliseconds = timestamp > 9999999999;`(:59,魔法阈值 9999999999,区分秒级/毫秒级时间戳)。
- `requestDate` = `isMilliseconds ? FromUnixTimeMilliseconds : FromUnixTimeSeconds`(再 `ToLocalTime()`)。
- 偏差窗口 `Options.AllowedDateDrift`,**默认 `TimeSpan.FromMinutes(5)`**(SignatureAuthenticationOptions.cs:19)。判定:`requestDate > utcNow + drift` 或 `requestDate < utcNow - drift` → 失败"timestamp 值已超过允许的偏差范围"。`utcNow = TimeProvider.GetUtcNow()`(.NET 8+ 静态,目标 .NET 10 可用)。

nonce 防重放(关键行:SignatureAuthenticationHandler.cs:81-86):
- 缓存键 = `$"{CacheConst.KeyOpenAccessNonce}{accessKey}|{timestampStr}|{nonce}"` = `"sys_open_access_nonce:" + accessKey + "|" + timestamp + "|" + nonce`(CacheConst.cs:107)。
- `cache.ExistKey(cacheKey)` 为真 → 失败"重复的请求"。
- 否则 `cache.Set(cacheKey, null, Options.AllowedDateDrift.Multiply(2))` → **TTL = 2 × 5min = 10min**(略大于窗口,保证窗口内重放必被拦)。

### 2.3 HandleAuthenticateAsync 关键逻辑(伪码 + 行引用)

```
1. 读 4 个 Header(:40-43)
2. 任一为空 → Fail("xxx 不能为空")(:45-52)
3. timestamp 解析失败 → Fail("timestamp 值不合法")(:55-56)
4. 秒/毫秒自判(:59-62)→ 超 AllowedDateDrift → Fail("timestamp 值已超过允许的偏差范围")(:64-66)
5. accessSecret = Events.GetAccessSecret(new GetAccessSecretContext{AccessKey}) (:69-70)
   └ 由 SysOpenAccessService.GetByKey(accessKey) 查库(带缓存)取 AccessSecret;为空 → Fail("accessKey 无效")(:71-72)
6. serverSign = SignData(UTF8(accessSecret), GetMessageForSign(Context)) (:75-76)
   └ serverSign != sign → Fail("sign 无效的签名")(:78-79)
7. nonce 防重放:ExistKey → Fail("重复的请求");否则 Set(key,null,drift*2) (:82-86)
8. 成功:构造 SignatureValidatedContext{ Principal=ClaimsPrincipal(ClaimsIdentity("Signature")), AccessKey }
   → await Events.Validated(ctx)(此时由 OnValidated 往 identity 追加 Claims,见 §2.5)(:89-94)
   → 若 ctx.Result 被 OnValidated 改写则返回之,否则 ctx.Success() 后返回(:96-101)
```

失败统一走 `AuthenticateResultFailAsync(msg)`(:154-159):把 msg 写入 `Context.Items["SignatureAuthenticateFailMsg"]`(`SignatureAuthenticationDefaults.AuthenticateFailMsgKey`,SignatureAuthenticationDefaults.cs:22),再返回 `AuthenticateResult.Fail(msg)`。

### 2.4 失败路径与状态码

- 认证失败 → ASP.NET Core 触发 challenge → HTTP **401**。
- 失败消息出口:参照由 Furion 统一响应 `AdminNETResultProvider.OnResponseStatusCodes` 捕获 401,读 `Context.Items["SignatureAuthenticateFailMsg"]` 作为 `msg` 输出(AdminNETResultProvider.cs:74-78)。
- **目标仓对应**:目标无 `AdminNETResultProvider`。目标的全局出口是 `ExceptionMiddleware`(Web.Core)统一转 `ApiResult`;401 的自定义消息需**在目标侧新增一个 401 处理器**(或中间件)读取同一 `HttpContext.Items["SignatureAuthenticateFailMsg"]` 键,把中文失败原因透传给前端(见 §8-R6)。否则签名失败只会得到默认 401,拿不到"sign 无效的签名"这类细分原因。
- 失败消息全集(精确字面量):`accessKey 不能为空` / `timestamp 不能为空` / `nonce 不能为空` / `sign 不能为空` / `timestamp 值不合法` / `timestamp 值已超过允许的偏差范围` / `accessKey 无效` / `sign 无效的签名` / `重复的请求`。

### 2.5 生成的 Claims(每个 ClaimType 字面量 + 取值来源)

`ClaimsIdentity` 的 authType = `"Signature"`(Handler.cs:91,`SignatureAuthenticationDefaults.AuthenticationScheme`)。
Claims 由 `SysOpenAccessService.GetSignatureAuthenticationEventImpl()` 的 `OnValidated` 追加(SysOpenAccessService.cs:188-199)。`openAccess` = `GetByKey(context.AccessKey)`(带 BindUser/SysOrg 导航):

| ClaimType 字面量(= `ClaimConst.*`) | 取值来源 | 关键行 |
|---|---|---|
| `UserId` | `openAccess.BindUserId + ""` | SysOpenAccessService.cs:190 |
| `TenantId` | `openAccess.BindTenantId + ""` | :191 |
| `Account` | `openAccess.BindUser.Account + ""` | :192 |
| `RealName` | `openAccess.BindUser.RealName` | :193 |
| `AccountType` | `((int)openAccess.BindUser.AccountType).ToString()` | :194 |
| `OrgId` | `openAccess.BindUser.OrgId + ""` | :195 |
| `OrgName` | `openAccess.BindUser.SysOrg?.Name + ""` | :196 |
| `OrgType` | `openAccess.BindUser.SysOrg?.Type + ""` | :197 |
| `TokenVersion` | `openAccess.BindUser.TokenVersion + ""` | :198 |

> 字面量值(= `ClaimConst`,见 §8-R2 映射):`UserId`/`TenantId`/`Account`/`RealName`/`AccountType`/`OrgId`/`OrgName`/`OrgType`/`TokenVersion`(均为 PascalCase 字符串)。
> ⚠ **目标仓差异**:目标 `JwtClaimNames` 只有 `UserId/Account/RealName/AccountType/TokenVersion/TenantId` 六个,**缺 `OrgId/OrgName/OrgType`**。且目标 `SysUser` 无 `SysOrg` 导航、目标 `SysOrg` 无 `Type` 字段(见 §8-R3)。目标仓**实际消费**的 Claim 只有 `UserId`(AOP 审计 CreateUserId)、`Account`(审计用户名)、`TenantId`(PermissionMiddleware 行级过滤)、`TokenVersion`(JwtAuthHandler 强校)、`RealName`(/me 展示)。故最小移植只需保证 `UserId`+`TokenVersion`(int 可解析)+`TenantId`+`Account`;`OrgId` 需新增常量并取自 `BindUser.OrgId`;`OrgName/OrgType` 建议**省略**(目标实体缺字段)或按需另查(见 §8-R3)。

### 2.6 事件/上下文类型清单(8 文件各自职责)

| 文件 | 类型 | 职责 |
|---|---|---|
| SignatureAuthenticationHandler.cs | `AuthenticationHandler<SignatureAuthenticationOptions>` | 核心认证逻辑(§2.3);`CreateEventsAsync` 抛 `NotImplementedException` 强制外部注入 Events(:36) |
| SignatureAuthenticationOptions.cs | `AuthenticationSchemeOptions` | `AllowedDateDrift`(默认 5min)+ `Events`(new 强类型) |
| SignatureAuthenticationEvent.cs | 事件容器 | `OnGetAccessSecret`(必配,未配抛异常 :38)/ `OnChallenge` / `OnValidated`(默认 no-op) |
| SignatureAuthenticationDefaults.cs | 常量 | `AuthenticationScheme="Signature"`、`AuthenticateFailMsgKey="SignatureAuthenticateFailMsg"` |
| SignatureAuthenticationExtensions.cs | 扩展 | `AddSignatureAuthentication(builder[ , options])` → `AddScheme<Options, Handler>("Signature", ...)` |
| GetAccessSecretContext.cs | `BaseContext<Options>` | 携带 `AccessKey`,供 `OnGetAccessSecret` 用 |
| SignatureValidatedContext.cs | `ResultContext<Options>` | 携带 `AccessKey`/`AccessSecret` + `Principal`;`OnValidated` 内追加 Claims |
| SignatureChallengeContext.cs | `PropertiesContext<Options>` | `AuthenticateFailure` + `Handled`(Challenge 时若 Handled 则跳过默认逻辑,Handler.cs:104-116) |

### 2.7 注册方式

参照 `Admin.NET.Web.Core/Startup.cs:83-86`(接在 `.AddJwtBearer(...)` 链尾):
```csharp
.AddSignatureAuthentication(options =>
{
    options.Events = SysOpenAccessService.GetSignatureAuthenticationEventImpl();
})
```
默认 scheme 仍为 JwtBearer(`AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`)。

---

## 3. API Key scheme 完整流程

参照源:`Admin.NET.Core/ApiKeyAuth/ApiKeyAuthenticationHandler.cs` + `ApiKeyAuthenticationOptions.cs`。

- Scheme 名:`"ApiKey"`(Handler.cs:18,`public const string AuthenticationScheme`)。
- **Header 字面量:`X-API-KEY`**(Handler.cs:29)。
- **API Key 就是 `AccessSecret`**:查库 `FirstAsync(c => c.AccessSecret == apiKey)`,带 `.Includes(BindUser).Includes(BindUser, p => p.SysOrg)`(Handler.cs:34)。即调用方直接把它当密钥用,不走时间戳/nonce/签名。
- 空 → Fail("API-KEY 不能为空")(Handler.cs:30-31);查无 → Fail("API-KEY 无效")(Handler.cs:35-36)。
- 失败消息同样写入 `Context.Items["SignatureAuthenticateFailMsg"]`(Handler.cs:58,复用同一键),401 出口同 §2.4。
- **Claims**(Handler.cs:39-50,`ClaimsIdentity` authType = `"ApiKey"`):

| ClaimType | 取值来源 | 行 |
|---|---|---|
| `UserId` | `acsInfo.BindUserId` | :41 |
| `TenantId` | `acsInfo.BindTenantId` | :42 |
| `Account` | `acsInfo.BindUser.Account` | :43 |
| `RealName` | `acsInfo.BindUser.RealName` | :44 |
| `AccountType` | `(int)acsInfo.BindUser.AccountType` | :45 |
| `OrgId` | `acsInfo.BindUser.OrgId` | :46 |
| `OrgName` | `acsInfo.BindUser.SysOrg?.Name` | :47 |
| `OrgType` | `acsInfo.BindUser.SysOrg?.Type` | :48 |
| `TokenVersion` | **`"1"`(写死字面量,非用户真实 TokenVersion)** | :49 |

> ⚠ **关键风险**:参照 ApiKey 的 `TokenVersion` 写死 `"1"`(Handler.cs:49),而签名方案用 `openAccess.BindUser.TokenVersion`。目标仓 `JwtAuthHandler` 会把 `TokenVersion` Claim 与库中当前版本比对,不一致即拒(JwtAuthHandler.cs:54)。若目标仓对 ApiKey 方案也跑 TokenVersion 校验,写死 `"1"` 会导致绑定用户 TokenVersion≠1 时永远被拒。**目标移植建议**:ApiKey 方案的 `TokenVersion` 也取 `BindUser.TokenVersion`(与签名方案一致);且需决策 ApiKey/签名方案是否要接目标的强校验链(见 §8-R4)。
> 目标仓 `SysOrg` 无 `Type` → `OrgType` 无法直接移植(同 §8-R3)。

注册(`Startup.cs:86`):`.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.AuthenticationScheme, options => { });`

---

## 4. SysOpenAccessService 各方法

参照源:`Admin.NET.Core/Service/OpenAccess/SysOpenAccessService.cs` + `Dto/`(3 文件)。
类标注:`[ApiDescriptionSettings(Order = 244, Description = "开放接口")]`,`: IDynamicApiController, ITransient`(:15-16,Furion 动态 API + 瞬态生命周期)。构造函数注入 `SqlSugarRepository<SysOpenAccess>` + `SysCacheService`(:18-29)。
权限点(参照为 Furion 动态 API,路由 = `sysOpenAccess/{action}`;`[ApiDescriptionSettings(Name="Add")]` 把路由段改成 `add`):

| 方法 | 路由/权限点(参照) | 入参 → 出参 | 逻辑要点 | 关键行 |
|---|---|---|---|---|
| `GenerateSignature` | `sysOpenAccess/generateSignature` | `GenerateSignatureInput` → `GenerateSignatureOutput{Timestamp, Signature}` | 见 §6(签名算法);Timestamp==0 时填当前毫秒 | :37-57 |
| `Page` | `sysOpenAccess/page` | `PageOpenAccessInput`(BasePageInput + `AccessKey`) → `SqlSugarPagedList<OpenAccessOutput>` | LeftJoin SysUser(BindUserId==Id)/SysTenant(BindTenantId==Id)/SysOrg(b.OrgId==c.Id);`WhereIF` AccessKey 包含;Select 映射 `BindUserAccount=a.Account`、`BindTenantName=c.Name`;`ToPagedListAsync(Page, PageSize)` | :65-78 |
| `AddOpenAccess` | `sysOpenAccess/add`(`[ApiDescriptionSettings(Name="Add")], HttpPost`) | `AddOpenAccessInput` → void | AccessKey 唯一校验(`AnyAsync(u => u.AccessKey==input.AccessKey && u.Id!=input.Id)`)→ 冲突 `throw Oops.Oh(O1000)`;`input.Adapt<SysOpenAccess>()`(Mapster)→ `InsertAsync` | :85-94 |
| `UpdateOpenAccess` | `sysOpenAccess/update` | `UpdateOpenAccessInput` → void | 同上唯一校验;`_sysCacheService.Remove(CacheConst.KeyOpenAccess + AccessKey)`(失效读缓存);`Adapt` → `UpdateAsync` | :101-112 |
| `DeleteOpenAccess` | `sysOpenAccess/delete` | `DeleteOpenAccessInput`(BaseIdInput) → void | `GetByIdAsync(Id)` → 命中则 `Remove(KeyOpenAccess + AccessKey)`;`DeleteByIdAsync(Id)`(软删) | :119-128 |
| `CreateSecret` | `sysOpenAccess/createSecret` | — → `string` | `Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..^2]`(16 字节 GUID 的 Base64 截掉末 2 位 = 22 字符密钥) | :135-138 |
| `GetByKey` | `[NonAction]`(不暴露为 API) | `accessKey` → `SysOpenAccess?` | **读缓存**:`_sysCacheService.GetOrAdd(KeyOpenAccess + accessKey, _ => First(Includes(BindUser).Includes(BindUser, p=>p.SysOrg), u => u.AccessKey==accessKey))`。参照未传 expire(默认 -1) | :146-157 |
| `GetSignatureAuthenticationEventImpl` | `[NonAction]` static | — → `SignatureAuthenticationEvent` | 组装 `OnGetAccessSecret`(取 secret)+ `OnValidated`(追加 Claims,§2.5);内部用 `.GetAwaiter().GetResult()` 同步桥异步 | :163-203 |

缓存策略小结:
- 读:`sys_open_access:{accessKey}`(CacheConst.cs:102),参照 `GetOrAdd` 未显式 TTL,失效主机制 = Update/Delete 主动 `Remove`。
- 防重放:`sys_open_access_nonce:{accessKey}|{ts}|{nonce}`,TTL = 10min(= 2×drift)。
- 目标仓建议:读缓存补一个 TTL 兜底(如 7 天,与 `KeyConfig` 同口径),失效仍走写路径 `Remove`(§8-R5)。

DTO(`Dto/`):
- `OpenAccessInput.cs`:`PageOpenAccessInput : BasePageInput{AccessKey}`、`AddOpenAccessInput : SysOpenAccess`(override `AccessKey/AccessSecret/BindUserId`,均 `[Required]`)、`UpdateOpenAccessInput : AddOpenAccessInput`(空)、`DeleteOpenAccessInput : BaseIdInput`(空)、`GenerateSignatureInput{AccessKey, AccessSecret, Method:HttpMethodEnum, Url, Timestamp, Nonce}`（实际实现：`Dto/` 下 7 独立文件、无 Delete、增 OpenAccessIdentity）。
- `OpenAccessOutput.cs`:`OpenAccessOutput : SysOpenAccess{BindUserAccount, BindTenantName}`。
- `GenerateSignatureOutput.cs`:`{long Timestamp, string Signature}`。

> 目标仓 DTO 约定:参照 DTO 继承实体(`AddOpenAccessInput : SysOpenAccess`)在目标仓**不推荐**(会把审计/导航字段一并带入)。目标惯例是**独立 record/类**(见 `SysConfig` 的 `AddConfigInput` 等)。移植时按目标 Dto 惯例重写为独立入参。

---

## 5. Furion 依赖识别 + 目标仓替代

| Furion 依赖(参照) | 出现位置 | 目标仓(零 Furion)替代 |
|---|---|---|
| `App.GetRequiredService<T>()`(Furion 静态服务定位器) | Handler.cs:82;Service.cs:172 | `Context.RequestServices.GetRequiredService<T>()`(Handler 内)或 DI 注入;目标仓无静态 `App` |
| `IDynamicApiController`(自动暴露为 API,路由由类名/方法名生成) | Service.cs:16;DemoOpenApi.cs:15 | `[ApiController] + [Route("api/sysOpenAccess")] + 每个 action `[HttpPost("add")]` 等**手写控制器**(放 `src/Web.Entry/Controllers/`) |
| `ITransient`(Furion DI 生命周期标记) | Service.cs:16;DemoOpenApi.cs:15 | 目标用 `[Service]` 特性(Core/Attributes/ServiceAttribute.cs),由 `Program.cs` 的 `AddAutoServices` 反射扫描注册(默认 Scoped) |
| `[ApiDescriptionSettings(Order/Name/Description/GroupName)]` | Service.cs:15/85/101/119;DemoOpenApi.cs:14 | `[Route]` + `[HttpGet/HttpPost]`;分组/排序在 Swagger 侧用 `[ApiExplorerSettings]`/XML 注释,非必需 |
| `[DisplayName("...")]` | Service.cs:36/64/86/102/120/134 | XML `/// <summary>`(目标 Swagger 读 XML 注释) |
| `[NonAction]` | Service.cs:145/162 | 控制器里不暴露的方法本就不会路由;服务层私有/内部方法无需 `[NonAction]`(目标无该特性,放 Web.Entry 控制器之外即可) |
| `Oops.Oh(ErrorCodeEnum.O1000)`(Furion 业务异常,`O1000`=错误码枚举) | Service.cs:90/106 | `throw new AppException("身份标识已存在")`(Core/Exceptions/AppException.cs,业务码默认 1,由 `ExceptionMiddleware` 统一转 `ApiResult`,不泄露堆栈) |
| `.Adapt<SysOpenAccess>()`(Mapster 对象映射) | Service.cs:92/108 | 手工 new / 属性赋值(目标无 Mapster;`SysConfig` 服务即手工映射) |
| `SqlSugarPagedList<T>` + `.ToPagedListAsync(Page,PageSize)` + `BasePageInput.Page` | Service.cs:65-77 | 目标 `PageResult<T>{Total, List}` + `_repo.AsQueryable()...ToPageListAsync(pageIndex, pageSize, RefAsync<int> total)`(见 `SysConfigService.PageAsync`);分页入参 `PageIndex/PageSize` |
| `BasePageInput` / `BaseIdInput`(Furion 基类) | Dto/OpenAccessInput.cs:12/45 | 目标用 record(`PageConfigInput(..., int PageIndex=1, int PageSize=10)`)或独立类;Id 入参 `[FromQuery] long id` |
| `SysCacheService`(Furion 缓存,同步 `Set/ExistKey/GetOrAdd/Remove`) | Handler.cs:82-86;Service.cs:109/125/149 | 目标 `ICacheService`(**全异步** `SetAsync/ExistKeyAsync/RemoveAsync/GetAsync<T>` + `GetExpireAsync`),见 §8-R5(参照的同步 `GetOrAdd`/`Set`/`ExistKey` 目标无,需改异步 + 自行拼「读-未命中-回源-写」) |
| `HttpMethodEnum`(Furion 自带枚举) | Dto/OpenAccessInput.cs:66 | 目标已有 `src/Core/Enums/HttpMethodEnum.cs`(同名),直接用（不实：全仓无此文件，实现直接用 string 方法名） |
| `[IncreTable]` / `[IncreSeed]` | 参照实体/种子 | 目标同名特性在 `Core/Attributes/`(语义一致,见 §8-R1/R7) |
| `[SysTable]`(System 表标记) | SysOpenAccess.cs:13 | 目标 `Core/Attributes/SysTableAttribute.cs` 同名同义,可用 |

> `[ApiController]` + `[Authorize]`(Microsoft.AspNetCore.Authorization)已是目标仓基线;**不存在** Furion 的 `[ApiDescription]/[AllowAnonymous]` 之外的隐式权限——参照服务层未加任何 `[Permission]`,靠 Furion 动态 API 的登录门禁 + 前端按钮;目标仓需**显式** `[MenuPerm("sysOpenAccess/xxx")]`(Core/Attributes/MenuPermAttribute.cs)标注 + 菜单按钮种子(§8-R7)。

---

## 6. 签名算法的完整可执行规格(C# 参考实现)

三步自包含:key 生成 / 签名生成 / 验签。每个魔法常量标注来源。

```csharp
using System.Security.Cryptography;
using System.Text;

public static class OpenAccessSign
{
    // ── 魔法常量来源 ─────────────────────────────────────────────
    // "sys_open_access_nonce:"  = 参照 CacheConst.cs:107 (KeyOpenAccessNonce)
    // 9999999999                = 参照 Handler.cs:59(秒/毫秒自判阈值)
    // AllowedDateDrift 默认 5min= 参照 SignatureAuthenticationOptions.cs:19
    // nonce TTL = drift*2 = 10min = 参照 Handler.cs:86 (AllowedDateDrift.Multiply(2))

    // [1] 生成 AccessSecret(= 参照 SysOpenAccessService.CreateSecret, Service.cs:137)
    //     16 字节 GUID 的 Base64,截掉末 2 位 → 22 字符
    public static string CreateSecret()
        => Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..^2];

    // [2] 生成签名(= 参照 Service.cs:44-51 + Handler.cs:122-147 同一算法)
    //     入参:accessSecret(密钥)、method(大写)、url(仅 Path,以 / 开头,无查询串)、
    //          accessKey、timestamp(秒或毫秒,须与请求头 timestamp 用同一字符串)、nonce
    public static string Sign(string accessSecret, string method, string url,
        string accessKey, long timestamp, string nonce)
    {
        // 拼接顺序(参照 Handler.cs:130): {METHOD}&{URL}&{accessKey}&{timestamp}&{nonce}
        var message = $"{method.ToUpper()}&{url}&{accessKey}&{timestamp}&{nonce}";
        using var hmac = new HMACSHA256();
        hmac.Key = Encoding.UTF8.GetBytes(accessSecret);           // 密钥 = accessSecret UTF8 字节
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))); // Base64(非 hex)
    }

    // [3] 验签(= 参照 Handler.cs:54-79 的时序;时间窗口/nonce 防重放由缓存服务承担)
    public static bool Verify(string accessSecret, string expectedSign,
        string method, string url, string accessKey, string timestamp, string nonce)
    {
        var actual = Sign(accessSecret, method, url, accessKey, long.Parse(timestamp), nonce);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expectedSign));
    }

    // [3b] 时间戳窗口校验(= 参照 Handler.cs:55-66;窗口默认 5min)
    public static bool InWindow(string timestamp, TimeSpan drift = TimeSpan.FromMinutes(5))
    {
        if (!long.TryParse(timestamp, out var ts)) return false;
        bool isMs = ts > 9999999999; // 参照 Handler.cs:59
        var req = isMs ? DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().DateTime
                       : DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().DateTime;
        var now = TimeProvider.GetUtcNow().LocalDateTime;
        return req <= now.Add(drift) && req >= now.Subtract(drift);
    }
}
```

> 说明:
> - 参照服务端比对用 `serverSign != sign`(非恒定时间);上表 `Verify` 用 `FixedTimeEquals` 是**加固**,行为等价,可二选一(保持参照用 `!=` 也行)。
> - nonce 防重放不在纯函数内:参照在 Handler 里用 `ICacheService` 判 `sys_open_access_nonce:{ak}|{ts}|{nonce}` 是否存在(§2.2),目标仓移植时同样放 Handler 内,用 `ExistKeyAsync`/`SetAsync(..., 10min)`。
> - 客户端(第三方)接入步骤:`accessSecret` 由管理端 `CreateSecret`/建号时给;每次请求算 `timestamp`(建议秒级或毫秒级均可,服务端自判)+ 随机 `nonce` → `Sign(...)` 得 `sign` → 四个头 `accessKey/timestamp/nonce/sign` 一起发。
> - **客户端不需要参照仓前端的 md5Signature.ts / data-signature.ts**(那是别处用的 MD5 工具,与开放接口 HMAC 无关,见下注)。

> 注(前端签名工具):`Web/src/utils/md5Signature.ts` = `MD5(JSON.stringify(规范化对象))`(键排序稳定化);`Web/src/utils/data-signature.ts` = `signatureByKSort`,MD5 小写 hex,拼 `key=value,key=value...`。二者均 **MD5** 且**不参与开放接口 HMAC-SHA256 流程**,属参照仓其它功能的数据签名工具,移植开放接口时**忽略**。开放接口的签名唯一口径 = §6 的 HMAC-SHA256。

---

## 7. DemoOpenApi 端点

参照源:`Admin.NET.Application/Service/Test/DemoOpenApi.cs`(Furion 动态 API)。
类标注:`[ApiDescriptionSettings(ApplicationConst.GroupName, Order = 100, Name = "Demo")]`(:14)。

| 端点 | HTTP 方法 | 路径 | 开放接口标注 | 关键行 |
|---|---|---|---|---|
| `HelloWordSignatureAuthentication` | `GET` | `helloWordSignatureAuthentication` | `[Authorize(AuthenticationSchemes = SignatureAuthenticationDefaults.AuthenticationScheme)]` = `[Authorize(AuthenticationSchemes = "Signature")]` | :28-33 |
| `HelloWordSignatureAuthenticationOrJwt` | `GET` | `HelloWordSignatureAuthenticationOrJwt` | `[Authorize(AuthenticationSchemes = "Bearer,Signature")]`(参照写 `JwtBearerDefaults.AuthenticationScheme + "," + "Signature"`) | :39-44 |

- "开放接口"的语义 = 用 `[Authorize(AuthenticationSchemes = ...)]` 指定**具体 scheme**(Signature / Bearer / ApiKey),而非全局默认。参照**未用** `[AllowAnonymous]`(这两个端点仍需认证,只是换了认证方式);真正的"开放"是把 JWT 换成签名/密钥认证。
- 目标仓移植:控制器 `[ApiController] + [Route("api/demo")]`,action `[HttpGet("hello")]` + `[Authorize(AuthenticationSchemes = "Signature")]`;「OrJwt」那个用 `[Authorize(AuthenticationSchemes = "Bearer,Signature")]`(目标 JwtBearer scheme 名 = `JwtBearerDefaults.AuthenticationScheme` = `"Bearer"`,Program.cs:100)。若要加 ApiKey 参与,则 `"Bearer,Signature,ApiKey"`。
- 注意:目标仓全局 `/api` 401 门禁由 `PermissionMiddleware` 承担(读 User Claims);对非默认 scheme(Signature/ApiKey)认证的请求,PermissionMiddleware 需能识别已认证身份(经 `UserManager` 收敛,见 §8-R4),否则可能被 401 或误设租户过滤。

---

## 8. 移植风险清单(参照 vs 目标差异,逐条)

- **R1 实体基类/建表特性**
  - 参照 `SysOpenAccess : EntityBase`,**无** `[IncreTable]`(仅 `[SysTable]`);目标仓 `Program.cs` 的 `db.InitTables` 按 `[IncreTable]` 分流建表,且约定「实体默认 `[IncreTable]`」。→ **目标须加 `[IncreTable]`**,否则 CodeFirst 不建表。
  - 参照 `[SugarTable(null, "开放接口身份表")]`(null=类名推导);目标惯例显式 `[SugarTable("sys_open_access", "开放接口身份表")]`(同 `SysConfig`)。
  - 参照 `BindUser` 导航**未** JsonIgnore(会序列化出 `SysUser.Password` 哈希);目标移植须给 `BindUser`/`BindTenant` 都加 `[System.Text.Json.Serialization.JsonIgnore]`。

- **R2 JwtClaimNames 对应关系(关键)**
  - 参照 `ClaimConst` vs 目标 `JwtClaimNames`:字面量值完全一致的有 `UserId/TenantId/Account/RealName/AccountType/TokenVersion`(两边都是 PascalCase 字符串)→ **直接复用 `JwtClaimNames` 即可,零字面量漂移**。
  - 参照多出的 `OrgId/OrgName/OrgType` 目标 `JwtClaimNames` **没有** → 若要保留需新增常量;但目标实际消费链(见下)不读这三个,建议省略。
  - 目标实际消费:审计 AOP 读 `UserId`+`Account`(CreateUserId/CreateUserName);`PermissionMiddleware` 读 `TenantId`(行级过滤);`JwtAuthHandler` 强校 `UserId`(必解析)+`TokenVersion`(int 解析,与库比对)。→ 两方案 Claims **必须**含 `UserId`、`TokenVersion`(int 可解析)、`TenantId`、`Account`;`RealName` 供 /me;`OrgId/OrgName/OrgType` 可选/建议省。

- **R3 实体导航/字段缺口**
  - 参照 Claims 用 `BindUser.SysOrg?.Name`(OrgName)、`BindUser.SysOrg?.Type`(OrgType);但**目标 `SysUser` 无 `SysOrg` 导航属性**(只有 `OrgId` 列),**目标 `SysOrg` 无 `Type` 字段**(只有 Pid/Name/Code/Sort/Remark/Status)。→ `OrgName/OrgType` **无法直接移植**。处理:`OrgId` 可取 `BindUser.OrgId`;`OrgName` 如需须按 `OrgId` 另查 `SysOrg.Name`;`OrgType` 目标无字段,**只能省略**。推荐最小集省略这三项,避免为开放接口去改用户/机构实体(违反最小改动 + 框架权威红线)。

- **R4 认证后强校验链 + 全局门禁**
  - 参照:签名/ApiKey 方案**不跑** JwtBearer 的 `OnTokenValidated`(TokenVersion 黑名单校验),只填 Claims;401 由 `AdminNETResultProvider` 捕获并透传失败消息。
  - 目标:JwtBearer 的 `OnTokenValidated` 里跑 `JwtAuthHandler`(TokenVersion/黑名单,Program.cs:118-123);全局 `/api` 401 门禁 + 租户过滤在 `PermissionMiddleware`。**移植须决策**:Signature/ApiKey 方案是否也接 `JwtAuthHandler` 强校验(建议接 TokenVersion 校验,保证改密/踢出对开放接口同样生效)——若接,则 ApiKey 方案 `TokenVersion` **不能**照抄参照的写死 `"1"`,须取 `BindUser.TokenVersion`(见 §3)。
  - 目标无 `AdminNETResultProvider` → 签名失败的细分中文原因(§2.4)**不会自动透传**,需在目标新增 401 处理器/中间件读 `HttpContext.Items["SignatureAuthenticateFailMsg"]`(或把失败原因写入 `ApiResult`)。否则前端只见裸 401。

- **R5 缓存服务 API 差异(同步→异步)**
  - 参照 `SysCacheService` 是**同步** `Set/ExistKey/GetOrAdd/Remove`(Furion);目标 `ICacheService` **全异步** `SetAsync/ExistKeyAsync/RemoveAsync/GetAsync<T>` + `GetExpireAsync`,**没有** `GetOrAdd`。
  - 移植改法:
    - nonce 防重放:`await _cache.ExistKeyAsync(key)` → 有则拒;否则 `await _cache.SetAsync(key, null, TimeSpan.FromMinutes(10))`。
    - `GetByKey` 读缓存:参照 `GetOrAdd(key, _ => First(...))` 目标须手写「`await GetAsync<T>(key)` → 未命中则查库 `FirstAsync` → `await SetAsync(key, dto, ttl)`」。
    - **缓存值**:参照缓存**整实体带导航**(`Includes(BindUser).Includes(BindUser,SysOrg)`);目标 `ICacheService` 用 JSON 序列化,缓存实体图(含导航)有循环/冗余风险 → **建议缓存轻量 DTO**(AccessKey/AccessSecret/BindUserId/BindTenantId + 绑定用户的 Account/RealName/AccountType/OrgId/TokenVersion),OnGetAccessSecret 与 OnValidated 共用同一份,避免二次查库与实体图序列化。
    - `SetAsync` 值可为 `null`(nonce 场景),注意目标 `GetAsync<T>` 对缺失/已过期返回 `default`——判空语义与参照一致。

- **R6 401 失败消息出口缺失**(与 R4 关联)
  - 目标仓无参照 `AdminNETResultProvider.OnResponseStatusCodes` 的 401 分支。目标统一出口是 `ExceptionMiddleware`(只处理异常→ApiResult)。→ 需补一个 401 响应处理器读 `AuthenticateFailMsgKey`,把"sign 无效的签名/重复的请求"等透传;否则签名调试体验差、排障难。

- **R7 权限点与菜单种子**
  - 参照服务层**无** `[Permission]`,靠 Furion 动态 API 隐式路由 + 登录门禁;目标仓无动态 API,须**手写控制器**并给每个写操作加 `[MenuPerm("sysOpenAccess/add|update|delete|page")]`(MenuPermAttribute.cs,超管 `AccountType==999` 穿透),并在 `src/Core/SeedData/SysMenuSeedData.cs` 补对应按钮种子(Type=3,`Permission` 与路由一致,"一处配置两端生效")。`generateSignature`/`createSecret` 是管理端辅助接口,是否纳入按钮权限按产品定(参照无约束)。

- **R8 对象映射 / 分页 / 基类**
  - 参照 `.Adapt<>`(Mapster)→ 目标手工 new;`SqlSugarPagedList`+`ToPagedListAsync(Page,PageSize)` → 目标 `PageResult<T>`+`ToPageListAsync(PageIndex,PageSize, RefAsync<int> total)`;`BasePageInput.Page`→目标 `PageIndex`。
  - 参照 DTO 继承实体(`AddOpenAccessInput : SysOpenAccess`)→ 目标改独立 record(避免审计/导航字段污染入参)。

- **R9 `App` 静态定位器 & 同步桥异步**
  - 参照 Handler 内 `App.GetRequiredService<SysCacheService>()`、事件内 `...GetByKey(...).GetAwaiter().GetResult()`(Furion 静态 App + 同步桥异步)。目标:Handler 内 `Context.RequestServices.GetRequiredService<ICacheService>()`;事件回调本就是 `Func<T,Task<...>>`,**全程 await 即可,无需 `GetAwaiter().GetResult()`**(更符合目标异步规范,禁 .Result/.Wait)。

- **R10 种子数据**
  - 目标仓开放接口身份**无内置种子需求**(AccessKey/Secret 是运行期由管理员创建的,不是系统内置数据),故**不需要** `[IncreSeed]` 种子类。仅当要给某个演示身份预置一条时才加 `[IncreSeed]+[SeedData(n)]` 的数据种子(参考 `SysRoleSeedData.cs` 写法)。参照仓也没有 SysOpenAccess 的种子文件(佐证:身份是运行期数据)。

---

## 附:目标仓移植文件清单(建议)

| 新建文件 | 内容 |
|---|---|
| `src/Core/Entities/SysOpenAccess.cs` | 实体(§1,加 `[IncreTable]` + 显式表名 + BindUser/BindTenant JsonIgnore) |
| `src/Core/Services/OpenAccess/SysOpenAccessService.cs` | 服务(`[Service]`,Page/Add/Update/Delete/GenerateSignature/CreateSecret/GetByKeyAsync) |
| `src/Core/Services/OpenAccess/Dto/{PageOpenAccessInput,AddOpenAccessInput,UpdateOpenAccessInput,DeleteOpenAccessInput,GenerateSignatureInput,GenerateSignatureOutput,OpenAccessOutput}.cs` | 独立入参/出参(§4,不继承实体) |
| `src/Web.Core/Auth/SignatureAuth/{SignatureAuthenticationHandler,Options,Event,Defaults,Extensions,GetAccessSecretContext,SignatureValidatedContext,SignatureChallengeContext}.cs` | 8 文件(§2.6),`App`→`RequestServices`、`SysCacheService`→`ICacheService`(异步) |
| `src/Web.Core/Auth/ApiKeyAuth/{ApiKeyAuthenticationHandler,ApiKeyAuthenticationOptions}.cs` | 2 文件(§3),TokenVersion 取 `BindUser.TokenVersion` |
| `src/Web.Entry/Controllers/SysOpenAccessController.cs` | `[ApiController]+[Route("api/sysOpenAccess")]` + `[MenuPerm]` 各写操作 |
| `src/Web.Entry/Controllers/DemoOpenApiController.cs`（实际实现：`OpenApiDemoController.cs`） | 示例开放端点(§7) |
| `src/Core/Security/JwtClaimNames.cs` | (可选)按需补 `OrgId/OrgName/OrgType` 常量 |
| `src/Core/CacheConst.cs` | 补 `KeyOpenAccess="sys_open_access:"`、`KeyOpenAccessNonce="sys_open_access_nonce:"` |
| `src/Web.Entry/Program.cs` | 在 `.AddJwtBearer(...)` 链尾加 `.AddSignatureAuthentication(...)` + `.AddScheme<ApiKey...>(...)`(§2.7/§3) |
| `src/Core/SeedData/SysMenuSeedData.cs` | (若纳入权限)补 `sysOpenAccess/*` 按钮种子(Type=3) |
