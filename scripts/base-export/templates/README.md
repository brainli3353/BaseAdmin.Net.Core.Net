# BaseAdminCore — 基础 Admin 框架（.NET 10）

基于原生 ASP.NET Core MVC、零 Furion 的通用后台管理框架，参考 AdminNetPro 的结构与核心能力，业务代码统一收敛到 `Business` 层。业务项目通过 fork 本仓获得框架能力，并用 `upstream` 同步框架升级。

## 技术栈

- **.NET 10** / ASP.NET Core MVC / C#
- **SqlSugar** ORM（PostgreSQL，CodeFirst 增量建表与种子）
- **JWT** 原生 JwtBearer 鉴权
- **Vue 3 + Element Plus + vxe-table** 管理前端（Vite 构建，由 Web.Entry 同源托管）

## 快速开始

### 后端

```bash
dotnet restore src/AdminCore.slnx
dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj
dotnet run --project src/Web.Entry
```

数据库连接、JWT 密钥、OTP 等配置通过 `appsettings.json` 或环境变量注入；密钥不得提交到仓库。初始管理员由种子创建（账号 `admin`），初始密码在 `src/Core/SeedData/SysUserSeed.cs` 中配置，首次登录后请立即修改。

### 前端

```bash
cd web
pnpm install
pnpm dev
pnpm typecheck
pnpm build
```

### 测试

```bash
dotnet test src/Test/AdminCore.Test.csproj
```

部分集成测试依赖真实 PostgreSQL 与 Redis，运行前请按测试项目说明准备环境。

## 目录结构

```text
src/
├── Core/        # 框架核心：SqlSugar、实体、仓储、雪花 Id、系统服务、种子
├── Web.Core/    # Web 支撑：鉴权、统一响应、异常中间件、审计
├── Web.Entry/   # 入口：Program、Controllers、配置
├── Business/    # 业务预留层（业务项目在此扩展）
└── Test/        # 单元/集成测试
web/             # Vue 3 管理前端
thirdparty/      # 第三方件
docs/            # 框架公共文档
```

## 文档

| 文档 | 内容 |
|---|---|
| `docs/框架索引.md` | 文件地图、机制装配点、常量表：东西在哪里 |
| `docs/框架使用指南.md` | 业务开发 cookbook：怎么用 |
| `docs/框架开发参考手册.md` | 六域功能点的代码级参考：为什么这样设计 |
| `docs/baseadmin设计精华参考.md` | 零 Furion 版设计来源与目录规范 |
| `docs/开发规范.md` | **业务开发纪律与验收要求**：约定 1–14（业务侧唯一约定源） |

内部立项、研究、验收、环境与过程台账不属于本仓内容。

## 框架自检脚本与发布面

本仓是**发布快照**，只含业务开发所需的代码与文档；**维护侧自检脚本不在发布面**——例如错误码覆盖自检 `scripts/error-code-coverage.mjs`、数据库完整性审计 SQL、导出/同步脚本等。
业务仓如需复跑这些门：向框架维护方索取，或按 `docs/开发规范.md` 约定 10（错误码三步）／约定 12（验证门）自行复核。

## 作为业务项目的基础仓

```bash
# 业务仓内
git remote add upstream <BaseAdminCore 仓库地址>
git fetch upstream
git merge upstream/main
```

约定：

0. **先读 `docs/开发规范.md`**——业务侧唯一约定源（框架文档里出现的「约定 #N」均指它）。
1. 框架能力只在本仓演进；业务仓通过 `upstream` 同步，避免私有分叉长期漂移。
2. 业务代码写入 `Business`，不要污染 `Core` / `Web.Core`。
3. 框架缺陷优先向本仓提交修复，再同步回业务仓。
4. 提交前至少通过后端构建与前端 `typecheck` / `build`。

## 许可证

发布前由维护者补充 `LICENSE`。
