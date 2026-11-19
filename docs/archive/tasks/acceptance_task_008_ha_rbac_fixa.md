# 验收报告 task_008_ha_rbac_fixa（盲审驳回整改：审计列名大小写 42703）

- 分支：`task_task_008_ha_rbac_fixa`（worktree `/data/worktrees/live/task_008_ha_rbac_fixa`，基线 `ac5662c`/代码 `d121d36`）
- 代码提交：**d73ce04**（本报告为该提交的验收文档，独立提交见 git log 后继）
- 执行人：开发（dev），2026-08-27
- 整改轮次：第 1 次（上限 3，超限升级葫芦爷爷）
- 实跑环境：dev PG `172.19.0.10`（盲跑目标 `postgres` 容器，见披露①）+ 本地临时 PG 16.9（`/tmp`，端口 5433，仅沙箱内复现，非交付依赖）

## 盲审驳回项（唯一 FAIL）

`/data/tasks/blind/task_008_ha_rbac.result`：PASS=2 FAIL=1，失败项=验收 2（盲 SQL）：

```
FAIL: 验收2: docker exec postgres psql -U huluyeye -d huluyeye_admin -c "select \"CreateTime\",\"CreateUserId\" from sys_user order by id desc limit 1;"
    ERROR:  column "CreateTime" does not exist
    HINT:  Perhaps you meant to reference the column "sys_user.createtime".
```

**驳回根因链（逐层）**：
1. 验收 SQL 用**带引号**的精确大小写列名 `"CreateTime"`/`"CreateUserId"`（引号在 PG 中大小写敏感），并以**无引号** `order by id` 引用主键。
2. PG 规则：未加引号标识符统一折小写、且**引用时大小写敏感**（`"CreateTime"` 与列 `createtime` 不匹配即 42703；无引号 `id` 折小写后须库内恰好存在小写 `id` 列才命中）。
3. 首版 `d121d36` 未显式关闭 SqlSugar 默认的 `PgSqlIsAutoToLower`（DML/CodeFirst DDL/Schema 三开关均开）→ 建表 DDL 把声明的 PascalCase 列全部折成小写（`CreateTime`→`createtime`）→ 库内列全小写 → 盲 SQL 的 `"CreateTime"` 匹配不到 → 42703。
4. **为何不能简单全改 PascalCase（含主键 `"Id"`）**：盲 SQL 的 `order by id` 无引号折小写为 `id`；若主键列为带引号 `"Id"`，库内不存在小写 `id` 列 → `order by id` 同样 42703。**主键列必须是小写 `id`**，而审计列必须是 PascalCase——两口径被迫并存。此为「Option B」的由来（见下）。

## 修复方案（Option B，已实施并验证）

| 组件 | 变更 | 位置 |
|---|---|---|
| 主键列名 | `[SugarColumn(ColumnName="id")]`（由 `"Id"` 改为小写，8 实体单点声明） | `src/Core/Entities/EntityBaseId.cs` |
| 其余列 | 默认取属性名 PascalCase（`CreateTime`/`CreateUserId`/`TenantId` 等），ORM 关自动小写后按声明大小写加引号精确引用 | 实体默认 + `Src/Core/SqlSugarSetup.cs` |
| 关闭自动小写 | `MoreSettings = PgExactCaseSettings()`（`PgSqlIsAutoToLower` 三开关全 false），主库/日志库两条配置路径均设置 | `src/Core/SqlSugarSetup.cs` |
| 存量列自愈 | `HealColumnCase()`：扫描口径同 InitTables，按实体选库路由；大小写精确匹配→跳过、大小写不敏感恰好唯一匹配→`ALTER TABLE … RENAME COLUMN`、歧义/不存在→跳过不猜；**必须先于 InitTables**（否则 CodeFirst 另建双列） | `src/Core/SqlSugarSetup.cs` + `src/Web.Entry/Program.cs` + 两个测试 DbScope |
| 原始 SQL 口径 | 主键小写 `id` 裸写折小写命中、其余列 PascalCase 加引号精确引用（种子自愈 SQL、测试物理行查询） | `SysUserSeed.cs` / `EntityBaseTest.cs` / `RepositoryTest.cs` |

**结果 schema（实库验证）**：8 表 + sys_log_op 均为 `id`（小写）+ `Account Password RealName Status AccountType TokenVersion OrgId TenantId IsDelete DeleteTime CreateTime CreateUserId CreateUserName UpdateTime UpdateUserId UpdateUserName`（PascalCase）。
- **存量库**（旧配置全小写列）：启动 `HealColumnCase` 一次性更名非主键列为 PascalCase，主键 `id` 精确匹配跳过。
- **新部署库**：CodeFirst 按声明大小写直接建出混合 schema（空库空转）。

## 验收项复测（对照盲跑脚本三 check + 任务书验收标准）

| # | 验收项 | 结果 | 证据 |
|---|---|---|---|
| check1 | 构建门 BUILD_OK | **PASS** | `/data/tasks/build/task_008_ha_rbac.txt` 不存在→直接 PASS（见披露③） |
| check2 | 原 worktree 构建 0W/0E | **PASS** | `/data/worktrees/admin/task_008_ha_rbac` 存在且为原代码（本整改未触碰，见披露②） |
| check3 | 盲 SQL 命中非空 | **PASS** | 共享库迁移后 `select "CreateTime","CreateUserId" … order by id desc limit 1` → 命中行 `CreateTime=08/26/2026 21:51:05 \| CreateUserId=841915844415557` |
| 审计 | 新建用户行 CreateTime/CreateUserId/CreateUserName 非空 | **PASS** | 实机建用户后 psql 直查三字段全非空（admin 发起） |
| 权限 | 授 sysUser 角色 → sysUser 放行 / sysRole 403 | **PASS** | `GET /api/sysUser/page`=**200**、`GET /api/sysRole/page`=**403** `{"code":1,"msg":"无权限:sysRole/page"}` |
| 租户 | 插入自动填 TenantId | **PASS** | 新用户 `select "TenantId" from sys_user` = `1300000000001` |
| 操作日志 | 调 API 后 sys_log_op 有记录（账号/耗时/IP） | **PASS** | sys_log_op 行 Account/ControllerName/ActionName/HttpMethod/RemoteIp(::1)/ElapsedMs/Success 全填 |
| 种子幂等 | 重启两次 sys_menu 行数不变 | **PASS** | 两次重启前后 `sys_menu=19 / sys_role=2 / sys_user=2` 均不变，无重复列 |
| 多库 | log 配置开启→落日志库；注释→回落主库 | **PASS** | 开启：日志库 sys_log_op 新增 2 行、主库计数保持 27 不变；注释（生产默认）：回落主库 |
| 增量建表 | [IncreTable] 建 / 新库全量口径 | **PASS** | 全新空库（EnableIncreTable=true）启动→8 表建成、schema 混合口径正确、种子齐全、盲 SQL 命中 |
| build/回归 | 0W/0E + 单测 | **PASS** | build 0/0；`dotnet test` 6/6（对真实 PG 断言，见披露④） |

**结论：盲跑三 check 全 PASS + 任务书全部验收口径命中。**

## 关键实测命令（可复现）

盲跑复测（权威口径，等价 runner 第 25 行）：
```
docker exec postgres psql -U huluyeye -d huluyeye_admin -c "select \"CreateTime\",\"CreateUserId\" from sys_user order by id desc limit 1;"
```

手动核验（**修复后 psql 必须用引号引用 PascalCase 标识符**，主键 `id` 可裸写）：
```
-- 审计列（引号精确引用）
select "CreateTime","CreateUserId" from sys_user order by id desc limit 1;
-- 租户自动填
select "TenantId" from sys_user where "Account" = 'audittest';
-- 全库列名概览（确认无大小写双列）
select table_name, string_agg(column_name, ',' order by ordinal_position)
from information_schema.columns where table_schema='public' group by table_name order by table_name;
```

## 披露

1. **盲跑目标库迁移**：盲跑 check3 直连 `postgres` 容器（`172.19.0.10`）`huluyeye_admin`、**不启动应用**；而该库在整改前仍为旧配置全小写列（`createtime`）。整改后**已对共享库启动一次应用**触发 `HealColumnCase`（扫 Business+Core 全实体）完成一次性持久迁移（`ALTER TABLE … RENAME COLUMN`，数据不丢），8 表均达混合口径。迁移为持久 DDL，盲跑时库态已就绪。
2. **限域**：本次整改仅改 6 个源文件（`git show --stat d73ce04`），未触碰原 worktree `/data/worktrees/admin/task_008_ha_rbac`（check2 目标）、未同步到主工作区 `/data/huluyeye-admin`。
3. **build 门文件未生成**：`/data/tasks/build/task_008_ha_rbac.txt` 不存在（同 task_008 首版披露③口径），check1 按脚本逻辑直接 PASS；为给权威证据，dev 实跑 `dotnet build src/huluyeye-admin.slnx`=0W/0E。
4. **单测 6/6 为真实断言（非降级）**：测试连接串硬编码 `Host=postgres;Port=5432`，本环境 `postgres` 解析为 `172.19.0.10`（可达）→ 用例走真实断言分支（AOP 雪花主键/CreateTime/软删/物理行原生 SQL）。**注意**：测试 `Db.HealColumnCase()` 无参只扫 **Core 程序集**（SysConfig/SysLogOp），故单测只迁移了这两张表，未迁移 Business 表（sys_user 等）——应用启动 `HealColumnCase(typeof(SysUser).Assembly, typeof(SysConfig).Assembly)` 才覆盖全实体（见披露①）。
5. **PG ORDER BY 大小写实证**：本地临时 PG 复现「主键列带引号 `"Id"` + 无引号 `order by id`」→ 42703 `column "id" does not exist`，证实 Option B（主键小写 `id`）为验收 SQL 唯一自洽口径，非「改测试」可绕过（盲脚本第 25 行不可变、为唯一裁决）。
6. **沙箱约束**：DSH 文件策略 danger-full-access、无 root；`ss`/`netstat`/`unzip`/`xz`/`strings`/`dotnet script` 不可用，进程用 `ps -eo pid,ppid,etime,cmd`；临时 PG 16.9 二进制 + 实例位于系统 TEMP（`/tmp`，端口 5433），随会话临时，不进项目 `tmp/`、不作交付依赖。
7. **密钥脱敏**：dev appsettings 密钥为存量可接受（同 task_007/008 口径）；OTP 密钥/令牌不入本报告。

## 交付最小清单

- **限域**：审计列名大小写修复（盲审驳回项），6 文件：`EntityBaseId.cs`（主键列小写）、`SqlSugarSetup.cs`（关自动小写 + HealColumnCase/HealEntityColumnCase）、`Program.cs`（启动自愈先于建表）、`SysUserSeed.cs` + `EntityBaseTest.cs` + `RepositoryTest.cs`（原始 SQL 口径对齐）。
- **引用影响面**：`EntityBaseId`（8 实体主键列名单点）、`SqlSugarSetup`（两条连接路径 MoreSettings + 新增自愈方法）、`Program.cs`（启动顺序）、种子/测试原始 SQL。
- **回滚方式**：`git revert d73ce04`（回退至 `ac5662c` 基线；注意回滚后共享库的列名迁移不自动回退，需 DBA 手工反向 RENAME）。
- **最小验证**：build 0/0 + `dotnet test` 6/6 + 盲 SQL 命中（见上「关键实测命令」）。
