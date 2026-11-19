# BaseAdminCore — 基础 Admin 框架（.NET 10）

自建基础 Admin 框架：**原生 ASP.NET Core MVC、零 Furion**，参考 AdminNetPro 结构与功能精华，框架代码统一收敛在 `src/Core`。

> **本仓库 = 框架权威（上游）**。下游 fork 仓库（如 IntelCore）通过 `git pull upstream` 同步，不直接修改本仓库代码（防漂移红线）。

## 特性

- **认证授权**：JWT（原生 JwtBearer）＋ OTP 双阶段登录 ＋ RBAC（菜单／按钮／接口／数据范围）
- **数据层**：SqlSugar ＋ PostgreSQL，CodeFirst 增量建表（`[IncreTable]`）、增量种子（`[IncreSeed]`）、雪花主键、软删全局过滤器、多库路由、分表
- **Web 支撑**：统一响应 `ApiResult`、全局异常中间件、TraceId、操作／审计／异常／慢 SQL 日志、字段级列权限
- **实时与开放接口**：SignalR 在线用户（强踢／推送）、HMAC 签名 ／ ApiKey 开放鉴权
- **工具箱**：代码生成、导入导出、字典、文件存储、消息通知、定时任务、服务器监控、序列号、打印模板、动态查询、本地化
- **前端**：Vue3 ＋ Element Plus ＋ vxe-table SPA，由 Web.Entry 同源自带

## 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET 10（LTS）／ ASP.NET Core MVC |
| ORM | SqlSugarCore（PostgreSQL） |
| 缓存 | StackExchange.Redis（可回退 Memory） |
| 鉴权 | 原生 JwtBearer ＋ 自研 Signature ／ ApiKey scheme |
| 实时 | SignalR（shared framework） |
| 前端 | Vue3 ＋ Vite ＋ Element Plus ＋ vxe-table（pnpm） |

## 快速开始

### 前置

- .NET 10 SDK
- Docker Desktop（或自备 PostgreSQL 17 ＋ Redis 7）
- Node.js ≥ 20 ＋ pnpm（仅前端开发需要）

### 方式一：依赖容器化 ＋ 应用跑宿主机（推荐开发）

```powershell
# 1. 启动 PostgreSQL + Redis（映射 localhost:5432 / 6379）
scripts\dev\up.ps1                 # 等价于 docker compose up -d

# 2. 启动后端（首次自动建表 + 增量种子）
dotnet run --project src/Web.Entry -c Debug

# 3. 另开终端启动前端开发服务器
cd web; pnpm install; pnpm dev    # http://localhost:5173，/api 代理到 5090
```

- 后端：<http://localhost:5090>（缺省监听 5090）
- 健康检查：`GET /health`（依赖实探）、`GET /health/live`
- 默认账号：`admin` / `Admin@123` ＋ 动态码（OTP）

> **端口冲突**：若本机 5432／6379 已被占用，复制 `.env.example` 为 `.env` 并改 `POSTGRES_PORT`／`REDIS_PORT`；同时用环境变量同步应用连接串端口，例如
> `$env:ConnectionConfigs__0__ConnectionString = "Host=localhost;Port=5433;..."`。

### 方式二：全栈容器

```bash
docker compose --profile app up -d --build
```

### 方式三：自备数据库

不依赖 Docker 时，用环境变量注入连接串即可（配置链中环境变量优先级最高）：

```powershell
$env:ConnectionConfigs__0__ConnectionString = "Host=<pg>;Port=5432;Database=huluyeye_admin;Username=<user>;Password=<pwd>"
$env:ConnectionStrings__Redis = "<redis>:6379"
dotnet run --project src/Web.Entry -c Debug
```

## 目录结构

```
src/
├── Core/        # 框架核心：SqlSugar 接入 / 实体基类 / 仓储 / 雪花 / 安全 / 系统实体·服务·种子 / 异常 / 本地化
├── Web.Core/    # Web 支持：鉴权 handler / 权限中间件 / 全局异常 / TraceId / 列权限过滤
├── Web.Entry/   # 入口：Program / Controllers / appsettings / 同源托管前端 dist
├── Business/    # 业务预留层（当前空，留给下游业务代码）
└── Test/        # xUnit 测试（真实连库断言）
web/             # 前端 SPA 源码
thirdparty/      # 库内 vendored 源码：YitIdHelper（雪花）/ Otp.NET（TOTP）
deploy/initdb/   # 首次初始化 SQL（创建测试库）
docs/            # 文档（阅读入口见 docs/README.md）
scripts/         # 辅助脚本（含 dev/up.ps1、base-export、e2e）
```

## 配置与环境变量

配置加载链（后者覆盖前者）：

```
appsettings.json → Business/Configuration/{Module}.json → {Module}.{Environment}.json → 环境变量 → 命令行
```

| 环境变量 | 用途 |
|---|---|
| `ConnectionConfigs__0__ConnectionString` | 主库连接串（main） |
| `ConnectionStrings__Default` | 单库兼容回落连接串 |
| `ConnectionStrings__Redis` | Redis 连接串（留空回退 Memory） |
| `DbSettings__EnableIncreTable` | true＝增量建表，false＝全量建表 |
| `Yit__WorkerId` | 雪花工作节点 Id（0–63，多实例必须唯一） |
| `Jwt__Secret` | JWT 对称密钥（≥32 字节，生产必注入） |
| `Cryptogram__Key` | AES 密钥（Base64 32 字节，生产必注入） |
| `App__Name` | 站点名／品牌（前端标题、OTP issuer 来源） |
| `POSTGRES_*` / `REDIS_PORT` | 仅供 docker-compose.yml 消费 |

完整样例见 `.env.example`。

## 常用命令

```bash
make help          # 列出全部命令
make up            # 起 PG + Redis
make build         # 严格门构建（须 0W/0E）
make test          # 全量回归（真实 PG，约 10–15 分钟）
make test-zero     # 零库档快测（无需 PG/Redis）
make web-install   # 前端安装依赖
make web-dev       # 前端开发服务器
make web-build     # 前端构建
```

Windows 无 make 时，可用 `scripts/dev/up.ps1`、`scripts/dev/down.ps1`，或直接 `dotnet` / `docker compose` 命令。

## 测试

| 档位 | 命令 | 依赖 |
|---|---|---|
| 严格门 | `dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj -warnaserror` | 无（须 0W/0E） |
| 零库档 | `dotnet test src/Test/AdminCore.Test.csproj --filter "FullyQualifiedName~..."` | 无 |
| 单域档 | 按批号前缀过滤，如 `--filter "FullyQualifiedName~BatchCP"` | PG |
| 全量档 | `dotnet test src/Test/AdminCore.Test.csproj --logger "console;verbosity=detailed"` | PG（约 10–15 分钟） |

前端：`cd web && pnpm typecheck && pnpm build`。
真机链路验证：`python scripts/e2e/verify-5090.py`（只读面，加 `--write` 才写库）。

## 文档导航

| 主题 | 位置 |
|---|---|
| **阅读入口（先读它）** | `docs/README.md` |
| 立项与里程碑 | `docs/立项.md` |
| 设计精华参考 | `docs/baseadmin设计精华参考.md` |
| 框架索引（在哪里） | `docs/框架索引.md` |
| 框架使用指南（怎么用） | `docs/框架使用指南.md` |
| 框架开发参考手册（为什么这样设计） | `docs/框架开发参考手册.md` |
| 功能清单 ／ 差距台账 | `docs/框架功能清单.md` ／ `docs/框架差距清单.md` |

## 工程约定摘要

- 编码：`.cs`/`.md`/`.props`/`.csproj` 用 UTF-8 **with BOM**；`.json` 禁 BOM；行尾一律 LF
- 分层：业务代码只写 `Business`，框架层只做支撑
- 命名：4 空格缩进、接口 `I` 前缀、PascalCase、异步方法 `Async` 后缀
- 实体 `[IncreTable]`、种子 `[IncreSeed]`（增量）
- 最小必要改动；复用既有仓储／缓存／配置／鉴权／错误码，不造平行实现
- 修改后做最小有效验证；无验证须明说

## 安全说明

仓库内所有凭据（数据库口令、`Jwt__Secret`、`Cryptogram__Key` 等）均为**本地开发占位**，不得用于生产。生产必须经环境变量注入，且占位值不得带进生产环境。
