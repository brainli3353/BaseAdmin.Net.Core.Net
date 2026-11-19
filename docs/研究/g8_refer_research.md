# 批 N 参照调研：JWT 续期 / Refresh Token（6.4）

> 依据件：参照仓 Admin.NET JWT 续期机制逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/Service/Auth/SysAuthService.cs`（L388-392 登录签双 token）＋`Admin.NET.Application/Service/App/Auth/AppAuthService.cs`（L247 `GetRefreshToken(accessToken)`）。

## §0 映射总表

| 参照 | 本仓现状/落点 | 处置 |
|---|---|---|
| 登录响应签 **access + refresh 双 JWT**（`GenerateRefreshToken(accessToken, expire)`——refresh 由 access 派生的长时效 JWT，同签名密钥） | 登录二阶段只签一个 8h JWT（`GenerateJwtToken`，`TokenLifetime=8h`） | **补**：同法签第二个长时效 JWT（claims 相同，仅 Exp 不同），响应头 `X-Refresh-Token` 透传（data=token 形状不变，批 M 同手法） |
| 配置 `GetRefreshTokenExpire()`（sys_config 表） | JWT 配置走 appsettings `Jwt:` 段（Secret/Issuer/Audience） | 同段 +`Jwt:RefreshTokenExpireHours`（缺省 168h=7 天） |
| `GetRefreshToken(accessToken)` 端点（客户端持 access 换 refresh） | 无 | **不做**（裁决③）：登录时 refresh 已下发，客户端长期持有；access 过期后客户端切换用 refresh 调接口（同签名密钥，JwtAuthHandler 天然接受），到期前重新登录换双新 token |
| refresh token 校验 | 同 JwtBearer 管线（签名/iss/aud/exp 通用校验） | **零新校验代码**：refresh 与 access 同签名密钥同 claim 集（含 TokenVersion）→ `JwtAuthHandler`/`OnTokenValidated` 同口径天然接受（裁决④） |
| 强制失效 | TokenVersion claim 校验（参照同机制） | **零新代码**：refresh 携带同一 TokenVersion claim → 改密/停用/重置 `TokenVersion++` 后 refresh 立即失效（既有闭环自然覆盖） |
| 登出黑名单 | 参照 `SignoutToSwagger` 不写 | **零新代码**（裁决⑤）：登出只黑名单客户端提交的 access（现状）；refresh 7 天自然过期（其 TokenVersion 门禁仍生效） |

**参照实证**：参照的"续期"= **长时效双 JWT**（非 OAuth opaque refresh_token + 轮换/复用检测）——`GenerateRefreshToken(accessToken, expire)` 由 accessToken 派生，`GetRefreshToken` 端点仅 App 端有（管理端登录响应头已带）。本仓按同语义移植（裁决①②），不升级为 OAuth 轮换模型（#8 最小必要；轮换/复用检测为按需项，参照未做）。

## §1 参照原文要点

```csharp
// SysAuthService.LoginAsync (L388-392)
var refreshTokenExpire = await _sysConfigService.GetRefreshTokenExpire();
var refreshToken = JWTEncryption.GenerateRefreshToken(accessToken, refreshTokenExpire);
// 响应头双 token
_httpContextAccessor.HttpContext.SetTokensOfResponseHeaders(accessToken, refreshToken);
// 响应体 LoginUserOutput 亦带 RefreshToken = refreshToken

// AppAuthService.GetRefreshToken (L247)——access 换 refresh(App 端)
public virtual string GetRefreshToken([FromQuery] string accessToken)
    => JWTEncryption.GenerateRefreshToken(accessToken, _sysConfigService.GetRefreshTokenExpire()...);
```

## §2 本仓现状

- `AuthService.GenerateJwtToken(user)`：claims=UserId/Account/RealName/AccountType/**TokenVersion**/TenantId；`TokenLifetime=8h` 静态常量；HS256 同密钥。
- `JwtAuthHandler`（OnTokenValidated）：签名/iss/aud/exp 标准校验＋TokenVersion 回查比对（强制下线闭环）＋黑名单检查（`sys_token_blacklist:{token}`，TTL=剩余有效期）。
- 登录二阶段 `LoginOtpAsync`：`return new LoginResult(true, null, null, token, passwordExpired)`；`AuthController.LoginOtp` 响应头已承载 `X-Password-Expired`（批 M 先例）。
- 配置：appsettings `Jwt:` 段（Secret/Issuer/Audience）＋`JWT.Development.json`（环境 Secret）。

## §3 设计（本批实施口径）

1. `AuthService.GenerateRefreshToken(user)`：同 `GenerateJwtToken` 同 claim 集，仅 `expires = UtcNow + RefreshTokenExpire`；`RefreshTokenExpire` 读配置 `Jwt:RefreshTokenExpireHours`（缺省 168；<=0 时回落 `TimeSpan.FromHours(7)`——**不关闭**：关闭续期会使前端契约断裂，语义上"续期"是登录契约一部分；如需禁用设超大值）。
2. `LoginResult` +`string? RefreshToken = null`；`LoginOtpAsync` 签发点双 token 同签。
3. `AuthController.LoginOtp`：`X-Refresh-Token` 响应头（与 `X-Password-Expired` 同段；data=token 形状不变）。
4. **不做**：opaque refresh 表/轮换/复用检测/`POST /api/auth/refresh` 端点/`GetRefreshToken(accessToken)` 端点（裁决③）；登出不动（裁决⑤）。
5. `Jwt:RefreshTokenExpireHours` 配置键**只加代码默认值不写 json**（与批 M `SysPassword:ExpirationDays` 同口径：缺省值即缺省行为，json 不动——#8 最小必要）。

## §4 验证口径（真实 PG）

`BatchNRefreshTokenTest` 用例：
1. 登录二阶段成功 → `LoginResult.RefreshToken` 非空且 ≠ AccessToken
2. 双 token claims 一致（UserId/Account/TokenVersion 相同；解码 JWT 段比对）
3. refresh 时效 > access 时效（exp 解码比对；缺省 7d vs 8h）
4. `Jwt:RefreshTokenExpireHours=24` 配置生效（exp≈24h）
5. **强制失效覆盖 refresh**：改密（TokenVersion++）后 refresh token 的 TokenVersion claim 与 DB 不再匹配（解码 claim 值比对——JwtAuthHandler 回查逻辑在批 A 已测，此处只证 refresh 携带旧版本）
e2e：登录 → 响应头 `X-Refresh-Token` 存在 → 拿 refresh 调 `GET /api/auth/me`（Authorization: Bearer refresh）→ 200（refresh 当 access 用，天然接受）。
探针用户＋TOTP 一阶段（复用 e2e 手法）；探针 finally 清理。

## §5 参照偏差声明

1. **不引入 OAuth 轮换/复用检测模型**（参照即双 JWT 简版；轮换为按需项，#8）。
2. 参照配置走 sys_config 表 → 本仓 appsettings `Jwt:` 段（同配置族，批 M 先例）。
3. **不做 `GetRefreshToken(accessToken)` 端点**（参照 App 端才有；本仓登录响应已下发 refresh，无换发场景——#8）。
4. **refresh 与 access 同 claim 集不加分型 claim**（参照 `GenerateRefreshToken(accessToken, expire)` 即派生同载 JWT；分型会让 JwtAuthHandler 分支化，无必要）。
5. 登出不黑名单 refresh（参照同口径 `SignoutToSwagger`；refresh 的 TokenVersion 门禁仍生效）。

## §6 范围边界（不做）

- opaque refresh token 表/轮换/复用检测（按需项）。
- refresh 刷新时 access 静默续期端点（`POST /api/auth/refresh`；按需项）。
- 多端 refresh 互踢（1.1 单设备踢人已覆盖连接层）。
