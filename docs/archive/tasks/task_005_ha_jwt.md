# task_id: task_005_ha_jwt
# subject: task_005_ha_jwt
# source: huluyeye
# created: 2026-08-26
# format: v3
# dep_on: task_003_ha_entity
# priority: P0
# files: src/Business/Entitys/SysUser.cs, src/Business/Services/AuthService.cs, src/Web.Entry/Controllers/AuthController.cs, src/Core/Security/Pbkdf2Helper.cs, src/Web.Entry/Program.cs, src/Web.Entry/appsettings.json, src/Business/SeedData/

# 任务：JWT 登录（账号密码初验）+ 鉴权 + PBKDF2 密码哈希

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/baseadmin设计精华参考.md 第四节「JWT 鉴权设计（原生实现，零 Furion）」——按此实现，禁止引入 Furion。
**代码纪律**：关键位置必须补中文注释。

## 背景

task_003（实体基类）+ task_004（仓储）已完成。本任务实现单阶段登录（账号密码 → JWT）——**OTP 两阶段是 M2，本任务不做**。

## 契约（按此实现）

1. **`src/Business/Entitys/SysUser.cs`**（继承 EntityBase）：
```csharp
[SugarTable("sys_user", "系统用户")]
[IncreTable]
public class SysUser : EntityBase
{
    [SugarColumn(ColumnDescription = "账号", Length = 64)] public string Account { get; set; }
    [SugarColumn(ColumnDescription = "密码哈希", Length = 256)] public string Password { get; set; }
    [SugarColumn(ColumnDescription = "姓名", Length = 64)] public string RealName { get; set; }
    [SugarColumn(ColumnDescription = "状态")] public int Status { get; set; } = 0; // 0正常 1停用
}
```

2. **`src/Core/Security/Pbkdf2Helper.cs`**（原生，不引包）：
- `HashPassword(string password)`：盐 16 字节随机 + Rfc2898DeriveBytes(password, salt, 100000, SHA256) 32 字节 → 返回 `"{saltBase64}${hashBase64}"`
- `VerifyPassword(string password, string stored)`：解析存储串 → 重算比对（恒定时间比较）

3. **`src/Business/Services/AuthService.cs`**：
- `LoginAsync(string account, string password)`：查 SysUser（仓储）→ 不存在/状态停用 → 返回错误；VerifyPassword 失败 → 返回错误；成功 → 签发 JWT（Claims: UserId/Account/RealName，过期 8 小时，密钥从配置 `Jwt:Secret` 读，`Jwt:Issuer`/`Jwt:Audience` 同配置）→ 返回 token
- 返回简单对象 `{ bool Success; string Error; string Token; }`（task_006 ApiResult 完成后统一改造）

4. **`src/Web.Entry/Controllers/AuthController.cs`**（原生 MVC Controller）：
- `POST /api/auth/login`：body `{account, password}` → AuthService.LoginAsync → 成功返 `{token}`；失败返 401 与错误消息
- `GET /api/auth/me`：贴 `[Authorize]` → 返回当前用户 Claims 信息

5. **Program.cs**：JwtBearer 注册（TokenValidationParameters 从配置读）+ appsettings.json 加 `Jwt` 段（Secret 开发占位，注释"仅开发用"）。

6. **种子用户**：Business/SeedData 里 SysUser 种子（[IncreSeed]）：账号 `admin`、密码 `Admin@123`（PBKDF2 哈希预计算或种子时 HashPassword）、RealName「系统管理员」。

## 交付物

- `/data/worktrees/admin/task_005_ha_jwt/src/Business/Entitys/SysUser.cs`
- `/data/worktrees/admin/task_005_ha_jwt/src/Business/Services/AuthService.cs`
- `/data/worktrees/admin/task_005_ha_jwt/src/Web.Entry/Controllers/AuthController.cs`

（附带修改：Pbkdf2Helper.cs、Program.cs JwtBearer、appsettings Jwt 段、SysUser 种子——同批提交）

## 验收标准

- `cd /data/worktrees/admin/task_005_ha_jwt && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- 启动 `dotnet run --project src/Web.Entry` 后实测（curl，附完整输出进验收报告）：
  - `docker exec huluyeye-dev-a curl -X POST http://127.0.0.1:5090/api/auth/login -H "Content-Type: application/json" -d '{"account":"admin","password":"wrong"}'` → HTTP 401
  - `docker exec huluyeye-dev-a curl -X POST http://127.0.0.1:5090/api/auth/login -H "Content-Type: application/json" -d '{"account":"admin","password":"Admin@123"}'` → HTTP 200 且响应含 token
  - `docker exec huluyeye-dev-a curl -s http://127.0.0.1:5090/api/auth/me -H "Authorization: Bearer <token>"` → HTTP 200 含用户信息
  - `docker exec huluyeye-dev-a curl -s http://127.0.0.1:5090/api/auth/me`（无 token）→ HTTP 401
- git commit（提交号写入验收报告），验收报告 `acceptance_task_005_ha_jwt.md` 附实测输出
