# task_id: task_021_ha_login_log
# subject: task_021_ha_login_log
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: 无
# files: src/Business/Entitys/SysLogLogin.cs, src/Business/Services/Log/SysLogLoginService.cs

# 任务：登录日志（SysLogLogin 实体 + 服务 + 分页查询 API）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §3.4（审计字段自动填充——SysLogOp 已有先例）。SysLogOp（操作日志）已实现，本任务补齐 SysLogLogin（登录日志）。
**代码纪律**：中文注释；实体 [IncreTable]；禁止修改 files 外文件。

## 契约（按此实现）

1. **实体**（Business/Entitys/SysLogLogin.cs，继承 EntityTenant + [IncreTable]）：
   - Account（登录账号）、RemoteAddr（IP）、Browser（UA 解析）、Os（OS 解析）
   - Status（成功/失败）、Message（描述：成功/密码错误/OTP 错误/账号锁定等）
   - LoginType（登录/登出）
2. **服务**（Business/Services/Log/SysLogLoginService.cs）：
   - Page（分页查询，支持 Account/时间范围/Status 过滤）
   - Add（登录流程写入——供 AuthService 调用，但不在本任务改 AuthService）
   - Clear（清理 N 天前数据，N 从配置读取默认 30）
3. **集成点预留**：Service 暴露 `LogAsync(SysLogLogin entity)` 异步写入方法——本任务不改 AuthService（后续任务接入），但确保接口可用

## 交付物

- `src/Business/Entitys/SysLogLogin.cs`
- `src/Business/Services/Log/SysLogLoginService.cs`（含 Dto 同批）

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- psql 验证 sys_log_login 表存在
- Page 分页查询实测（curl 附输出，空数据返回空列表非报错）
- Clear 清理接口可用
