# 参照仓 AdminNetPro「在线用户＋SignalR」实现调研（批 F 移植唯一依据）

> 建立：2026-09-15（批 F 5.13/1.8＋1.1 单设备踢人同批闭合）　性质：**移植依据件**（与 docs/研究/e1_refer_research.md 同性质）
> 事实源：`../AdminNetPro/Admin.NET.Pro.HHBase/Admin.NET/`（只读红线 #7）＋本仓批 F 端到端实证（node v22 客户端 + psql + C# 探针，全部留痕于本件 §8）。
> 本文是移植决策的唯一依据：参照口径逐条列字面量，本仓加固/偏离逐条标注理由。

## 0. 移植落点映射表（速查）

| 参照仓（Admin.NET） | 本仓（BaseAdminCore） | 备注 |
|---|---|---|
| `Admin.NET.Core/Hub/OnlineUserHub.cs` | `src/Core/Hub/OnlineUserHub.cs` | 落 Core（参照同落 Core，依赖方向一致） |
| `Admin.NET.Core/Hub/IOnlineUserHub.cs` | `src/Core/Hub/IOnlineUserHub.cs` | 客户端方法面裁剪 4→2（§4.3） |
| `Admin.NET.Core/Hub/Dto/OnlineUserHubOutput.cs` | `src/Core/Services/OnlineUser/Dto/OnlineUserListMessage.cs` | DTO 就近 Dto/（约定 #6） |
| `Admin.NET.Core/Service/OnlineUser/SysOnlineUserService.cs` | `src/Core/Services/OnlineUser/SysOnlineUserService.cs` | 强踢双保险加固（§3.2） |
| `Admin.NET.Core/Service/OnlineUser/Dto/OnlineUser.cs` | `src/Core/Services/OnlineUser/Dto/OnlineUserInfo.cs` | 字段裁剪 LoginMode/Device（§3.1） |
| `Admin.NET.Core/Const/CacheConst.cs` L57 | `src/Core/CacheConst.cs`（KeyUserOnline/GroupOnline/OnlineUserTtl） | TTL 加固（§3.3） |
| `SysUserService.cs` L576 单设备踢人调用 | `AuthService.SingleLoginAsync` 钩子 → `SysOnlineUserService.ForceOfflineByUserIdAsync` | 1.1 同批闭合 |
| Furion `SysCacheService.HashAdd/GetOne/Del/GetAll` | `ICacheService` 新增 3 个 Hash 原语（§2.4） | 复用既有缓存抽象，不发明平行实现（#11） |
| `[MapHub("/hubs/onlineUser")]` 特性 | `Program.cs` `app.MapHub<OnlineUserHub>("/hubs/onlineUser")` | 路由字面量一致 |
| `Furion.InstantMessaging`（Hub 基类依赖） | 原生 `Microsoft.AspNetCore.SignalR`（shared framework 自带，零新 NuGet） | .NET 10 |

## 1. 参照仓 Hub 完整机制（OnlineUserHub.cs 逐段）

### 1.1 连接注册（OnConnectedAsync）

```csharp
var httpContext = Context.GetHttpContext();              // Furion 扩展
var userId = (httpContext.User.FindFirst(ClaimConst.UserId)?.Value).ToLong();
var account = httpContext.User.FindFirst(ClaimConst.Account)?.Value;
if (userId < 0 || string.IsNullOrWhiteSpace(account)) return;   // ← 守卫:未认证不注册不断连
```

- **守卫口径**：Hub 无 `[Authorize]` 特性 → negotiate/WS 握手不拦截（匿名可连），Hub 层 OnConnectedAsync 读不到有效 User 时**静默 return**（不注册、不断连）。**本仓同口径**（§7.1）。
- 注册动作（认证用户）：
  1. 组装 `OnlineUser{ConnectionId, UserId, UserName, RealName, Time, Ip, Browser, Os, TenantId, LoginMode, Device}`；
  2. `SysCacheService.HashAdd(CacheConst.KeyUserOnline, Context.ConnectionId, user)` —— **Hash 单键，field=ConnectionId**；
  3. `Groups.AddToGroupAsync(Context.ConnectionId, $"GROUP_ONLINE_{tenantId}")` —— 租户分组；
  4. 广播 `Clients.Groups(groupName).OnlineUserList(new OnlineUserList{RealName, Online=true, UserList=同租户全部})`。

### 1.2 断开注销（OnDisconnectedAsync）

```csharp
var user = SysCacheService.HashGetOne<OnlineUser>(CacheConst.KeyUserOnline, Context.ConnectionId);
if (user == null) return;
SysCacheService.HashDel<OnlineUser>(CacheConst.KeyUserOnline, Context.ConnectionId);
// 广播 OnlineUserList{Online=false, UserList=同租户剩余}
```

- **弱口（本仓加固点）**：HashDel 在 OnDisconnectedAsync 内——**进程崩溃/kill -9 时永不触发 → 残字段永久驻留（参照仓无 TTL）**；强踢只推送不删字段（§1.3）→ 被踢端若不响应，残字段同样驻留。

### 1.3 强踢（ForceOffline）

```csharp
public async Task ForceOffline(OnlineUserHubInput input)
{
    await _onlineUserHubContext.Clients.Client(input.ConnectionId).ForceOffline("强制下线");
}
```

- **只推送、不删 field**——客户端离线（网络闪断/被踢前已断）时推送 no-op，field 残留；
- 服务层调用方：`SysOnlineUserService.ForceOffline/ForceOfflineByUserId`（L48-64、L97-102）；业务触发点 = `SysUserService.cs:576`（单设备登录踢旧）＋ 改密等场景（L102）。

### 1.4 在线列表查询（SysOnlineUserService L37）

```csharp
var userList = SysCacheService.HashGetAll<OnlineUser>(CacheConst.KeyUserOnline)
    .Where(u => u.TenantId == _userManager.TenantId).ToList();
```

- **固定租户过滤**——参照仓无"超管看全量"分支（参照仓超管也是普通租户用户）。

## 2. 缓存键与常量（字面量）

| 常量 | 参照仓字面量 | 本仓值 | 位置 |
|---|---|---|---|
| `CacheConst.KeyUserOnline` | `"sys_user_online"`（无冒号） | 同 | 参照 Const/CacheConst.cs:57 |
| 租户分组前缀 | `"GROUP_ONLINE_"`（Hub 内 const） | `"GROUP_ONLINE_"` | 本仓 CacheConst.GroupOnline |
| TTL | **无**（永久驻留） | **24h 滑动**（OnlineUserTtl，写时续） | 本仓加固 §3.3 |

## 3. 本仓三处加固（偏离参照，逐条理由）

### 3.1 强踢双保险（推送＋删 field）

参照 `ForceOffline` 只推送；本仓 `SysOnlineUserService.ForceOfflineAsync` 先 `HashDel`（无论推送是否送达），再 `Clients.Client(cid).ForceOffline(...)` 推送。
**理由**：参照弱口——推送 no-op 时 field 永久残留 → 在线列表虚高、单设备踢人语义失效（旧会话字段还在，新会话踢人查不到）。先删后推，推送只负责通知语义。

### 3.2 24h 滑动 TTL（CacheConst.OnlineUserTtl）

参照无 TTL。本仓 Hash 写路径（HashAdd/HashUpdate）统一续 24h。
**理由**：① 进程异常退出（崩溃/kill -9）OnDisconnectedAsync 不触发 → 残字段 24h 内自然消解（参照仓永久残留）；② Memory 缓存进程重启即清空，TTL 主要保护 Redis 部署面；③ 24h 为管理端会话合理上限（参照仓无此约束，本仓自定，属自行裁决）。

### 3.3 超管全量列表

参照固定 `Where(u => u.TenantId == _userManager.TenantId)`；本仓 `SysOnlineUserService.GetListAsync` 增加分支：`TenantId==0`（超管口径，与数据权限域同值域）→ 不过滤，返回全量。
**理由**：管理端运维场景需要全局视角（参照仓超管=租户内超管，无全局租户概念；本仓 1.8 功能点要求超管全量）。

### 3.4 字段裁剪（LoginMode/Device 去除）

参照 OnlineUser 含 `LoginMode`（PC/APP 多端）与 `Device`（客户端设备指纹）。本仓 OnlineUserInfo 不含两者：
- 本仓单登录**不分端**（无 LoginMode claim，登录模式域未立项）；
- 设备指纹依赖 Furion 扩展（`GetClientDeviceInfo`），零 Furion 口径不引入。
**理由**：81 功能点 1.8/5.13 无多端需求；最小必要改动（#8）。

## 4. 消息面与接口契约

### 4.1 本仓 IOnlineUserHub（客户端方法面）

```csharp
public interface IOnlineUserHub
{
    Task OnlineUserList(OnlineUserListMessage message);  // 列表广播(连接/断开/强踢后)
    Task ForceOffline(object context);                   // 强踢通知(参照字面量 "强制下线")
}
```

- 参照 4 方法（OnlineUserList/ForceOffline/PublicNotice/ReceiveMessage）裁剪为 2：PublicNotice/ReceiveMessage 属**站内消息域**，本仓 81 功能点无对应项（不预置）。
- 广播触发点：OnConnectedAsync（Online=true）、OnDisconnectedAsync（Online=false）、强踢后列表刷新——与参照一致。

### 4.2 服务端接口（OnlineUserController，/api 路由）

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/onlineUser/list` | GET | 在线列表（超管全量/租户过滤，§3.3） |
| `/api/onlineUser/forceOffline` | POST | 强踢指定 ConnectionId（双保险 §3.1） |

- 走 PermissionMiddleware 门禁（/api 前缀），权限 10115/10116（SysMenuSeedData 增量种子，10114=目录）。
- 强踢端点与 Hub 的 `ForceOffline` 方法**分离**：端点面向管理端 UI（需鉴权门禁），Hub 方法面向被踢端接收推送。

### 4.3 强类型 SignalR（.NET 10 口径）

- `Hub<IOnlineUserHub>`：客户端方法 = 接口声明（参照同口径）。
- `IHubContext<OnlineUserHub, IOnlineUserHub>`：
  - `.Clients` 成员**返回 TClient 本身**（强类型：`Clients.Client(cid).ForceOffline(...)` 直接可调，无需字符串方法名）；
  - `.Groups` = `IGroupManager`（`AddToGroupAsync/RemoveFromGroupAsync` 返回 Task）；
  - `.Contexts` 非泛型（连接集合，本仓未用）。
- `builder.Services.AddSignalR(options => ...)`：KeepAliveInterval=15s、ClientTimeoutInterval=30s、MaximumReceiveMessageSize=1MB（参照默认 10MB，管理端消息面小，自定 1MB）。
- **IHubContext 属 Singleton**：无连接时 DI 仍可解析、推送 no-op——测试 DbScope 加一行 `services.AddSignalR()` 即修复 DI 链（10 个既有测试文件同批补，口径同批留痕）。

## 5. 鉴权链路（/hubs/onlineUser 端点）

- `app.MapHub<OnlineUserHub>("/hubs/onlineUser")` 路由**非 /api** → 不经 PermissionMiddleware 门禁；
- **经 UseAuthentication**（JwtBearer 全业务链：黑名单/TokenVersion/租户）→ **强于参照**（参照仅 Hub 守卫）：token 失效/黑名单命中的连接在 negotiate 阶段即 401；
- 已建连接的 token 失效（如黑名单/TokenVersion 变更）**不主动断连**——靠 `ForceOfflineByUserIdAsync` 兜底（与参照同口径：SignalR 连接无周期性 re-auth）。
- **JwtBearer events 合并铁律（本 session 实证 bug）**：`options.Events = new JwtBearerEvents{...}` 是**整体替换**——分开赋值（先 OnMessageReceived 后 OnTokenValidated）后者覆盖前者。本仓必须合并为同一对象（Program.cs 批 F 段注释已固化）。

### 5.1 客户端认证双通道（@microsoft/signalr 实证）

| 通道 | 适用 | 服务端读取点 |
|---|---|---|
| WS URL `&access_token=<jwt>` | **浏览器**（WebSocket 握手无法自定义 header；JS 客户端 `accessTokenFactory` 自动追加） | JwtBearer `OnMessageReceived` 读 `Request.Query["access_token"]` |
| WS upgrade header `Authorization: Bearer <jwt>` | **node/桌面**（JS 客户端 node 路径走 `ws` 包，支持自定义 header） | JwtBearer 默认读取 |

- 两通道均已端到端验证（§8）。**OnMessageReceived 为参照 AdminNetPro 同口径**（其 Program 同样有 access_token query 兼容）。

## 6. SignalR wire 协议（node v22 实证，客户端对接铁律）

| 步骤 | 事实 | 实证细节 |
|---|---|---|
| negotiate | `POST /hubs/onlineUser/negotiate?negotiateVersion=1` | **无 token → 200**（不 401——Hub 无 [Authorize]，守卫在 Hub 层，§1.1）；带 token → 200 |
| negotiate 响应 | **无 `url` 字段** | negotiateVersion=1 返回 `{connectionId, connectionToken, availableTransports}`——客户端自拼 WS URL = `<hubUrl>?id=<connectionToken>` |
| WS handshake 请求 | 客户端→服务端 text 帧 `{"protocol":"json","version":1}` | **必须以 `\x1e`（0x1E record separator）结尾**——SignalR TextMessageFormat 协议；缺分隔符服务端不响应（本 session 实证：内置 WebSocket 与 `ws` 库均 handshake 超时，补 `\x1e` 立即返回） |
| WS handshake 响应 | 服务端→客户端 `{}`（成功）或 `{error:...}`（失败） | 协议帧 type=7 HandshakeResponse；消息体本身无 type 字段（区别于 type 1 的 hub 消息） |
| hub 消息帧 | `{"type":1,"target":"<方法名>","arguments":[...]}` | 广播/单播同格式，target=IOnlineUserHub 方法名（OnlineUserList/ForceOffline） |
| 心跳 | 服务端 KeepAlive 15s 发 ping | 客户端 30s 未收 ping 判死 |

## 7. 口径裁定（参照 vs 本仓，逐条）

### 7.1 negotiate 不拦截（参照口径）

参照 Hub 无 `[Authorize]`，匿名可 negotiate/握手，Hub 守卫兜底（不注册）。本仓同口径——**Hub 级 [Authorize] 会让匿名 negotiate 直接 401，与参照行为不一致**；且本仓 UseAuthentication 已在链上，有效 token 正常认证。

### 7.2 单实例无 backplane

参照无 ScaleOut（单实例）。本仓同口径（管理端部署面单实例）；多实例需加 Redis backplane（`AddStackExchangeRedis`）——**不在 81 功能点范围，留差距清单**。

### 7.3 无启动清理

参照无进程启动时的在线表清理。本仓同口径 + TTL 兜底（§3.2）：Memory 缓存重启即空；Redis 部署面由 24h 滑动 TTL 收敛残字段。

### 7.4 强踢文案

`"强制下线"` 字面量与参照一致（客户端 `ForceOffline(context)` 参数）。

## 8. 本 session 端到端实证（node v22 + 临时探针，全部已清理）

**环境**：app 5090 本地（Memory 缓存＋PG 127.0.0.1 临时配置）、临时用户 `batchf_probe`（PBKDF2 密码）＋OTP 绑定、官方 @microsoft/signalr@8 客户端＋内置 WebSocket 双客户端。

| # | 断言 | 结果 |
|---|---|---|
| 1 | 登录两阶段（step1 challengeId → step2 token 381 chars） | ✅ |
| 2 | negotiate 无 token → 200（参照口径） | ✅ |
| 3 | negotiate access_token query → 200 + connectionToken | ✅ |
| 4 | WS 握手（`{"protocol":"json","version":1}\x1e` → `{}`） | ✅（缺 `\x1e` 时超时——§6 铁律） |
| 5 | 在线列表含本连接（`&access_token=` query 通道认证生效） | ✅（修复 Events 合并 bug 前为匿名不注册——§5 铁律） |
| 6 | 强踢端点 200（先删 field 后推送） | ✅ |
| 7 | 客户端收 `ForceOffline` 推送（target=ForceOffline） | ✅ |
| 8 | 断开后列表移除该连接（OnDisconnectedAsync 注销） | ✅ |

**官方客户端（node 路径，Authorization header 通道）同断言 5 亦通过**——双认证通道实证齐备。

### 8.1 TOTP 实证（登录二阶段调试沉淀，供后续复用）

- 密码存储 = **PBKDF2**（`Pbkdf2Helper`：100000 轮/SHA256/16B salt/32B，格式 `{saltBase64}${hashBase64}`）；AES-256-CBC（`CryptogramHelper`，key 在 appsettings）**仅用于 OTP SecretCipher**。
- TOTP = RFC6238 SHA1/30s/6 位；OtpNet 为仓内源码 `thirdparty/Otp.NET/`（ProjectReference）；node 与 Otp.NET 对固定 counter 的 HMAC/otp/code **逐字节等价**（C# 探针实证）。
- `bindRequired=false` 时 step1 响应 `secret=null`——验证用表内 `SecretCipher`（解密后 Base32 secret）。
- **防重放铁律**：`OtpService.Validate` 拒绝 `matchedStep <= LastUsedTimeStep`；成功登录即更新 `LastUsedTimeStep` → **同一 30s 窗口内已用过的 code 不能再登录** → 自动化脚本须动态偏移：`offset = max(0, min(1, lastStep - curStep + 1))`（服务端窗口 ±1）。
- 表内 secret 恒定（成功登录写回同值，永不轮转）。

## 9. 批 F 自行裁决清单（呈报葫芦爷爷）

| # | 裁决 | 依据 |
|---|---|---|
| 1 | Hub 落 Core（src/Core/Hub/） | 参照同落 Core；依赖方向 Core←Web.Core 不变 |
| 2 | KeyUserOnline="sys_user_online" 无冒号 | 参照字面量 |
| 3 | 24h 滑动 TTL | 加固（参照无）；管理端会话上限自定 |
| 4 | 强踢双保险（先删 field 后推送） | 加固（参照只推送） |
| 5 | 超管全量列表（TenantId==0 不过滤） | 1.8 功能点要求 |
| 6 | 去 LoginMode/Device 字段 | 无多端需求；设备指纹依赖 Furion 不引入 |
| 7 | MaxReceiveMessageSize 1MB | 参照默认 10MB 对管理端过大 |
| 8 | 单登录不分端 | 无 LoginMode claim |
| 9 | Hub 消息面仅 2 方法 | PublicNotice/ReceiveMessage 属站内消息域（未立项） |
| 10 | access_token query 兼容（OnMessageReceived） | 浏览器标准通道；参照同口径 |
| 11 | 无启动清理，TTL 兜底 | 参照同口径 |
| 12 | 单实例无 backplane | 参照同口径；多实例留差距 |
| 13 | negotiate 不拦截，Hub 守卫 | 参照口径（§7.1） |
| 14 | 测试 DbScope 全量加 AddSignalR | IHubContext Singleton 无连接可解析、推送 no-op |
| 15 | JwtBearer events 必须合并（OnMessageReceived+OnTokenValidated 同一对象） | 实证 bug：整体替换语义（§5） |
| 16 | handshake 须以 `\x1e` 结尾（客户端对接铁律） | 实证（§6）；已入使用指南 |
