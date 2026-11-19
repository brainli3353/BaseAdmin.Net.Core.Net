# BaseAdminCore — 项目索引（会话入口）

> 本文件是本项目入口，供人与 AI 会话快速定位。**先读本文件，再按事选读 `docs/` 下文档。**
>
> **历史定案（三条）**：立项（参考 AdminNetPro／原生 ASP.NET Core MVC／**零 Furion**／业务统一 Business 层）｜架构（框架代码归 `AdminCore.Core`，Business＝业务预留层）｜开发模式（由接手会话在本仓直接开发）。详见 `docs/立项.md`。

## 项目定位

基础 Admin 框架：原生 ASP.NET Core MVC ＋ SqlSugar（PostgreSQL）＋ 原生 JWT，框架代码统一收敛在 `src/Core`。**本仓库 = 框架权威（上游）**，下游 fork 仓库（如 IntelCore）经 `git pull upstream` 同步，不直接修改本仓库代码（防漂移红线）。

## 入口指针

| 主题 | 位置 |
|---|---|
| **文档导航（阅读入口，先读它）** | `docs/README.md` ★ |
| 立项（目标／技术栈／里程碑） | `docs/立项.md` |
| 设计精华参考（开发前必读） | `docs/baseadmin设计精华参考.md` |
| 框架索引（文件地图／机制装配点） | `docs/框架索引.md` |
| 框架使用指南（业务开发者 cookbook） | `docs/框架使用指南.md` |
| 框架开发参考手册（为什么这样设计） | `docs/框架开发参考手册.md` |
| 功能清单 ／ 差距台账 | `docs/框架功能清单.md` ／ `docs/框架差距清单.md` |
| 环境管理（库边界／网络实测） | `docs/环境管理.md` |

## 目录结构

> 逐目录文件地图见 `docs/框架索引.md`。分层要点：框架代码**唯一落点 = `src/Core`**；`src/Business` 是业务预留层（当前空）；`web/` 是前端 SPA（pnpm，由 Web.Entry 同源自带）。

```
src/Core · Web.Core · Web.Entry · Business · Test
web/ · thirdparty/(YitIdHelper, Otp.NET) · deploy/initdb/ · docs/ · scripts/
```

## 硬性约定

1. 业务代码只写 `Business`，框架层只做支撑。
2. 编码：`.cs`/`.md`/`.props`/`.csproj` 用 UTF-8 **with BOM**；`.json` 禁 BOM；行尾一律 LF；4 空格缩进；接口 `I` 前缀；PascalCase。
3. 异步方法 `Async` 后缀，禁 `.Result`/`.Wait()` 阻塞等待。
4. 实体 `[IncreTable]`、种子 `[IncreSeed]`（增量）。
5. 密钥／凭据纪律：仓库内值均为本地开发占位；生产必须经环境变量注入；提交前查敏感文件。
6. 复用优先：既有仓储／缓存／配置／鉴权／错误码，不造平行实现。
7. 最小必要改动（不顺手扩散）；增量更新只改明确允许的字段，禁整对象覆盖。
8. 异常不无说明吞；对外信息稳定可检索（不泄露堆栈）。
9. 修改后做最小有效验证；无验证须明说。
10. 结构性变更（新增／重命名／移动、新机制、新缓存键／Claim／ConfigId）与 `docs/框架索引.md`、`docs/框架使用指南.md`、`docs/框架开发参考手册.md` **同批**更新，不单独漂移。

## 本地运行与验证

```powershell
# 依赖（PostgreSQL + Redis）
scripts\dev\up.ps1                       # 或 docker compose up -d
# 后端（缺省 :5090，首次自动建表 + 种子）
dotnet run --project src/Web.Entry -c Debug
# 前端开发服务器（:5173，代理 /api）
cd web; pnpm install; pnpm dev
```

| 门 | 命令 | 判据 |
|---|---|---|
| 严格门 | `dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj -warnaserror` | 0W/0E |
| 全量回归 | `dotnet test src/Test/AdminCore.Test.csproj --logger "console;verbosity=detailed"` | exit 0（真实 PG，约 10–15 分钟） |
| 零库档 | 见 `README.md` 的 filter | 无需 PG/Redis |
| 前端 | `cd web && pnpm typecheck && pnpm build` | 全绿 |

- 健康检查：`GET /health`（依赖实探 DB+缓存）、`GET /health/live`（存活）。
- 默认账号 `admin` / `Admin@123` ＋ 动态码。
- 测试连真实库：`TestPg.ConnectionString` → `127.0.0.1`（与 compose 一致）；库 `huluyeye_admin` ／ `huluyeye_admin_test`。

## 常用机制速查

- 配置链：`appsettings.json` → `Business/Configuration/{Module}.json` → `{Module}.{Environment}.json` → 环境变量 → 命令行（后者优先；环境变量用双下划线代替冒号）。
- 缓存实现由 `Cache:Type` 决定（Redis ／ Memory）；Redis 连接串为空时回退 Memory。
- 错误码：`AppException.Of(ErrorCodes.X, args)`；响应 `{code, errCode, msg}`，`msg` 按 `Accept-Language` 渲染。
- SqlSugar 契约：仓储经 `ISqlSugarRepository<T>`，事务统一 `[UnitOfWork]`／`UseTranAsync`。
- 命名空间遮蔽：本仓存在 `AdminCore.Core.Services.Enum` 等段，BCL 同名类型需全限定。

## 安全红线

仓库内凭据一律视为本地开发占位；禁止将占位密钥带入生产；禁止在日志／响应中输出连接串密码与异常堆栈原文。
