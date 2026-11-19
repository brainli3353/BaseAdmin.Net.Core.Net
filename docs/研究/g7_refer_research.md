# 批 M 参照调研：密码有效期（1.11 补件）

> 依据件：参照仓 Admin.NET 密码有效期机制逐条映射 + 本仓落点 + 自行裁决留痕（批内自主，批末呈报）。
> 参照源：`Admin.NET.Core/Service/User/SysUserService.cs` `VerifyPwdExpirationTime`（L451-468）＋`SysAuthService` LoginUserOutput（L457）＋改密/重置落点（L408/L434）。

## §0 映射总表

| 参照 | 本仓现状/落点 | 处置 |
|---|---|---|
| `SysUser.LastChangePasswordTime`（改密=Now，重置=null） | 无此字段 | **补**（CodeFirst 增量加列，可空） |
| 配置 `SysPasswordExpirationTime`（sys_config 表，天数，>0 启用） | 密码策略走 appsettings `SysPassword:` 段（`EnableStrength`/`HistoryCount`，批 A） | 同段 +`SysPassword:ExpirationDays`（缺省 0=关闭；本仓配置族一致） |
| `VerifyPwdExpirationTime()`（null→过期；超天数→过期） | 无 | **补** `SysUserService.IsPasswordExpiredAsync(userId)`（参照口径：null=过期，含存量/重置后用户） |
| **调用点：参照为死方法（无调用者）**；`LoginUserOutput.LastChangePasswordTime` 回传前端引导 | 登录二阶段 `LoginResult(Success, Error, Data, Token)` | **补**：`LoginResult` +`PasswordExpired` bool，token 签发点计算挂回（前端引导改密页；服务端不拒登防锁死） |
| 改密 `UpdatePassword` 落 `LastChangePasswordTime=Now` | 改密闭环已有（批 A） | **补一行** |
| 重置 `ResetPwd` 落 `LastChangePasswordTime=null` | 重置闭环已有（批 A） | **补一行** |

**参照实证**：`VerifyPwdExpirationTime` 在参照仓无调用点（grep 全仓仅定义）——参照为半成品（方法在、登录响应仅回传时间字段靠前端判）。本仓补全为"服务端判定＋登录响应挂标志"闭环。

## §1 参照原文要点

```csharp
public async Task<bool> VerifyPwdExpirationTime()
{
    var sysConfig = await _sysConfigService.GetConfig(ConfigConst.SysPasswordExpirationTime);
    if (int.TryParse(sysConfig.Value, out int expirationTime) && expirationTime > 0)
    {
        var user = await _sysUserRep.GetByIdAsync(_userManager.UserId);
        if (user.LastChangePasswordTime == null) return false;          // 未改过密码 = 过期
        if ((DateTime.Now - user.LastChangePasswordTime.Value).Days > expirationTime) return false; // 超期 = 过期
    }
    return true;
}
// 改密:  user.LastChangePasswordTime = DateTime.Now;  (L408)
// 重置:  user.LastChangePasswordTime = null;          (L434, 重置=用户未自证新密码 → 必触发到期改密)
```

## §2 本仓现状

- `SysUser` 无 LastChangePasswordTime；`SysUserService.UpdatePassword`（旧密校验+强度/历史+哈希覆写+历史入库+强制下线）/`ResetPwd`（管理员代设+强制下线）已有闭环（批 A）。
- 密码策略配置族 appsettings `SysPassword:` 段（`EnableStrength` 缺省 false/`StrengthExpression`/`EnableHistory`/`HistoryCount`）。
- 登录二阶段 `AuthService.LoginOtpAsync`：OTP 校验→绑定落库→用户/租户门禁→单登录踢旧→`GenerateJwtToken`→`return new LoginResult(true, null, null, token)`。
- `AuthService` 依赖无 `SysUserService`（无循环：SysUserService 不依赖 AuthService）。

## §3 设计（本批实施口径）

1. `SysUser` +`LastChangePasswordTime`（DateTime?，`[SugarColumn(IsNullable = true)]`；存量行 NULL=未改过→启用有效期即到期，标准安全实践"首次使用强制改密"）。
2. `UpdatePassword` → `= DateTime.Now`；`ResetPwd` → `= null`（同参照：重置后必触发到期改密）。
3. `SysUserService.IsPasswordExpiredAsync(long userId)`（public，可单测）：`ExpirationDays<=0` → false（关闭）；`LastChangePasswordTime==null` → true；`(Now - t).Days > ExpirationDays` → true。
4. `AuthService.LoginOtpAsync` token 签发点：`var passwordExpired = await _userService.IsPasswordExpiredAsync(user.Id);` → `LoginResult +bool PasswordExpired = false` 挂回（**服务端不拒登**——拒绝则用户无法登录改密=锁死；参照 LoginUserOutput 同语义"回传前端引导"）。
5. 配置 `SysPassword:ExpirationDays`（int 缺省 0；`appsettings.json`/`Database.json` 不动——密码策略段在 `appsettings.json` `SysPassword` 段，检查现有段位置后补键注释）。

## §4 验证口径（真实 PG）

`BatchMPwdExpirationTest` 用例（`IsPasswordExpiredAsync` 直测 + 落库断言）：
1. `ExpirationDays=0`（关闭）→ false
2. 启用+`LastChangePasswordTime=null`（存量/重置后）→ true
3. 启用+刚改密（Now）→ false
4. 启用+超期（回填 Now-（N+1）天）→ true
5. `UpdatePassword` 后 `LastChangePasswordTime` 落库 = Now±容差
6. `ResetPwd` 后 `LastChangePasswordTime` = null
探针用户（真实密码 PBKDF2，改密/重置全链路走通）finally 清理。
e2e：临时 `ExpirationDays=1`＋回填 admin 改密时间为 2 天前 → 两阶段登录 → 响应含 `PasswordExpired:true`（文件/日志无新增面，纯响应断言）。

## §5 参照偏差声明

- 参照判定方法无调用点（半成品）→ 本仓接通登录响应（裁决①）。
- 参照配置走 sys_config 表 → 本仓走 appsettings `SysPassword:` 段（同配置族，裁决②）。
- 参照不拒登（回传时间字段）→ 本仓回传布尔标志（更强类型化，同语义，裁决③）。

## §6 范围边界（不做）

- 到期后服务端强制拒登/强制改密拦截（锁死风险；前端引导为参照口径）。
- 到期提醒（提前 N 天通知，垂直增值）。
- refresh token / 6.4 JWT 续期（独立点，未立项）。
