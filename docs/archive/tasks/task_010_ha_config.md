# task_id: task_010_ha_config
# subject: 配置加载与参考源对齐（Business/Configuration 模块化 + 三环境）
# source: huluyeye-admin
# created: 2026-08-27
# format: v3
# priority: P1
# files: /data/huluyeye-admin/src/Business/Configuration/, /data/huluyeye-admin/src/Web.Entry/Program.cs, /data/huluyeye-admin/src/Web.Entry/appsettings.json
# dep_on: task_008_ha_rbac

# 任务：配置加载与参考源（baseadmin）对齐——业务项目 Configuration 目录模块化 + 按环境（开发/测试/正式）加载

## 背景

参考源 baseadmin 的配置模式：`Admin.NET.Business/Configuration/` 下每功能一个 json（App/JWT/Database/Cache/Logging 等）+ **环境后缀覆盖**（App.json + App.Development.json）。当前 huluyeye-admin 配置集中在 Web.Entry/appsettings.json——需对齐：**配置从业务项目（Business）下的 Configuration 目录加载，按运行环境（Development/Staging/Production）切换**。

## 交付物

1. `src/Business/Configuration/` 目录（模块化配置）：
   - `App.json` + `App.Development.json` + `App.Staging.json` + `App.Production.json`（基础 + 三环境覆盖——App 含应用名/系统配置等）
   - `Database.json` + 三环境覆盖（DB 连接串——dev/test/prod 三库）
   - `JWT.json` + 三环境覆盖（Issuer/Audience/Secret——prod 留占位注释"生产必须环境变量注入"）
   - `Cache.json`（Redis 连接——按需）
   - `Logging.json`（日志级别——按环境）
2. `src/Web.Entry/Program.cs` 配置加载改造：
   - AddJsonFile 指向 `Business/Configuration/`（basePath 定位——每个模块文件 optional + reloadOnChange）
   - 环境后缀合并：`{Module}.json` + `{Module}.{Environment}.json`（ASP.NET Core 标准——Development/Staging/Production）
   - 保留 Web.Entry/appsettings.json 作为补充（环境无关的运行时配置）
   - 现有读取点（SqlSugarSetup 的 ConnectionStrings:Default、Jwt 段）迁移到新路径（GetSection 键名对齐）
3. `tests/` 或启动验证：三环境加载实测（ASPNETCORE_ENVIRONMENT 切换 → 不同配置生效）

## 约束

- 配置键名与现有读取点兼容（或同步改读取点——迁移清单写入验收报告）
- 生产（Production）敏感值（DB 密码/JWT Secret）**不落库**——留环境变量注入占位（红线：密钥不入库）
- 参考源结构对照：`../baseadmin/Admin.NET.Pro.HHBase/Admin.NET/Admin.NET.Business/Configuration/`（本地无——按约定结构实现，键名参考 docs/baseadmin设计精华参考.md）

## 验收标准

- `cd /data/worktrees/admin/task_010_ha_config && dotnet build src/huluyeye-admin.slnx` 输出 0 个警告 0 个错误
- `ASPNETCORE_ENVIRONMENT=Development dotnet run` 启动日志显示开发配置（如连接 dev 库 huluyeye_admin）
- `ASPNETCORE_ENVIRONMENT=Staging dotnet run` 启动日志显示测试配置（连接 test 库 huluyeye_admin_test）
- `ASPNETCORE_ENVIRONMENT=Production dotnet run` 启动日志显示正式配置（连接 prod 库——不实连，日志确认加载路径）
- git commit（提交号写入验收报告），验收报告 `acceptance_task_010_ha_config.md` 附实测输出（三环境启动证据）
