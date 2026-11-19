# task_id: task_018_ha_config_module_port
# subject: task_018_ha_config_module_port
# source: huluyeye
# created: 2026-08-27
# format: v3
# priority: P1
# dep_on: task_008_ha_rbac
# files: src/Business/Configuration/, src/Web.Entry/appsettings.json, src/Web.Core/Startup.cs

# 任务：配置中心模块移植（假合并存量补录之二）

项目：/data/huluyeye-admin（原生 ASP.NET Core MVC 零 Furion，net10.0）。
**必读参考**：本地分支 `task_task_010_ha_config_fixa` 的两个新增提交 `7b29db2`（配置模块化）与 `9e24d04`（端口 5090 缺省 + 存活/就绪解耦）（git show 可读完整 diff）。分支中其余文件差异是相对老 base 的过时状态——**只参照这两份 diff 的语义，禁止整树搬移**。
**代码纪律**：中文注释；禁止修改 files 外文件；**appsettings.json 只允许新增节段，严禁改动 ConnectionStrings/ConnectionConfigs/Yit/JWT 等既有节的任何值**（main 上连接串已容器名化主机名——postgres/redis，这是当前正确状态）。

## 契约（按此实现）

1. **Business/Configuration 模块化**：按分支实现把配置读取收敛为模块化类组（对齐原 010 契约精神），Program/Startup 里注册使用
2. **端口 5090 缺省**：Web.Entry 默认监听口为 5090（launchSettings/默认 URL 配置对齐），5091 保持 test 稳定版专用不受影响
3. **存活与就绪解耦**：/healthz（liveness：进程活着即 ok）与 /readyz（readiness：依赖 db/cache 就绪才 ok）双端点
4. **启动配置自检**：启动时输出所载配置摘要到日志，**密码/密钥段一律脱敏显示**（自检代码里禁止打明文）

## 交付物

- `/data/worktrees/admin/task_018_ha_config_module_port/src/Business/Configuration/`（模块化类组）
- `/data/worktrees/admin/task_018_ha_config_module_port/src/Web.Entry/appsettings.json`（仅新增节段）
- `/data/worktrees/admin/task_018_ha_config_module_port/src/Web.Core/Startup.cs`（装配）

## 验收标准

- `cd /data/worktrees/admin/task_018_ha_config_module_port && dotnet build src/huluyeye-admin.slnx` 输出 0 警告 0 错误；dotnet test 全绿
- 三环境实测：Development/Staging(default)/Production 三档启动各一次，日志可见各自覆盖来源
- 就绪探测实测：curl http://127.0.0.1:5090/healthz → 200；停 postgres 无法实做时以 mock/条件跳过并在报告注明 [受限] 理由
- 自检脱敏实测：启动日志 grep 密码字面（如 huluyeye_dev_2026）零命中
- 连接串回归：psql 未提供时以服务能查 sys_user 种子数据佐证 DB 通（curl 拿 token 的既有路径）
