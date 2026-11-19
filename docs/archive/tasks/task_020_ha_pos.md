# task_id: task_020_ha_pos
# subject: task_020_ha_pos
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: 无
# files: src/Business/Entitys/SysPos.cs, src/Business/Services/Pos/

# 任务：职位管理（SysPos 实体 + CRUD + API）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：仓库 docs/框架开发参考手册.md §2.6（职位管理：结构树/代码参考/Furion 替换点）。
**代码纪律**：中文注释；实体 [IncreTable]；禁止修改 files 外文件。

## 契约（按此实现）

1. **实体**（Business/Entitys/SysPos.cs，继承 EntityTenant + [IncreTable]）：Name、Code（唯一）、Sort、Remark、Status
2. **服务**（Business/Services/Pos/SysPosService.cs）：
   - Page（分页查询，支持 Name/Code 模糊）
   - Add / Update / Delete（Delete 校验：有关联用户时拒绝）
3. **控制器**：`/api/sysPos/*`
4. **种子数据**：初始种子（Name=管理员，Code=admin）

## 交付物

- `src/Business/Entitys/SysPos.cs`
- `src/Business/Services/Pos/SysPosService.cs`（含 Dto 同批）

## 验收标准

- `dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误
- CRUD 实测（curl 附输出）：Add→Page 查到→Update→Delete 成功
- psql 验证 sys_pos 表存在且种子数据在位
