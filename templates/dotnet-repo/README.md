# dotnet-repo 新仓脚手架（模板目录）

> **用途**：从框架仓（BaseAdminCore）fork 出下游业务仓（IntelCore／FlowCore 类）时，把本目录**整体复制到新仓根**，即自带「项目地图模板＋环境自检 doctor 骨架」。
> **凭据纪律**：本目录所有件只写凭据**来源**，**永不写凭据值**；默认口令一律标注「**仅开发默认，生产必改**」。

## 接入三步

1. **复制本目录到新仓根**：
   ```bash
   cp -r <框架仓>/templates/dotnet-repo/* <新仓根>/
   ```
   得到：`docs/项目地图_模板.md`（项目地图模板）、`scripts/doctor.sh`（环境自检骨架）、本说明。
2. **填 `docs/项目地图_模板.md` 的占位符**：复制为 `docs/项目地图.md`，按「占位符清单」逐格替换为本仓真实值；凭据栏只填**来源**（文件／环境变量／机器），不填值。
3. **跑 `scripts/doctor.sh`**：
   ```bash
   bash scripts/doctor.sh
   ```
   空环境下应全 `SKIP` 且 `exit 0`（骨架阶段不误红）；接入真实资源后逐项转 `PASS`。

## doctor.sh 的 env 变量表

| 变量 | 含义 | 缺省 | 行为 |
|---|---|---|---|
| `BASE_URL` | 应用基址 | `http://127.0.0.1:${PORT}` | **仅显式配置**才探测；未配置 => SKIP；不可达 => SKIP；非 200 => FAIL |
| `PORT` | 应用监听端口 | `5090` | **仅显式配置**才判端口占用，否则 SKIP |
| `LOGIN_URL` | 登录端点 | `${BASE_URL}/api/auth/login` | BASE_URL 未配 => SKIP；不可达 => SKIP；5xx => FAIL |
| `DB_HOST` | 数据库主机 | 空 | 空 => SKIP；TCP 不通 => SKIP |
| `DB_PORT` | 数据库端口 | `5432` | 配合 `DB_HOST` TCP 探测 |
| `REDIS_HOST` | 缓存主机 | 空 | 空 => SKIP；TCP 不通 => SKIP |
| `REDIS_PORT` | 缓存端口 | `6379` | 配合 `REDIS_HOST` TCP 探测 |
| `OTP_ENABLED` | OTP 全局开关（来源，不填值语义） | 空 | 与 `OTP_ISSUER` 均空 => SKIP |
| `OTP_ISSUER` | OTP 认证器 issuer | 空 | 同上 |
| `DOCTOR_STRICT` | `1`=存在 FAIL 时 exit 1 | `0` | 默认骨架阶段恒 `exit 0` |
| `CURL_TIMEOUT` | HTTP 超时（秒） | `5` | — |
| `TCP_TIMEOUT` | TCP 超时（秒） | `3` | — |

例：`BASE_URL=http://127.0.0.1:5091 DB_HOST=127.0.0.1 PORT=5091 bash scripts/doctor.sh`

## 检查项（六项）

① 应用 health（`<BASE_URL>/health`）② 登录端点可达 ③ 数据库可达（TCP 探测）④ Redis／缓存档可达（TCP 探测）⑤ OTP 配置状态 ⑥ 端口占用。

> **状态语义**：`PASS` 正常｜`FAIL` 已配置但结果不符预期（默认不影响退出码）｜`SKIP` **外部资源未配置/不可达**，骨架阶段不判失败。
> **边界**：这是**骨架**，只做轻量探测（curl／TCP），不含业务连通性、鉴权流、数据一致性断言——接入方按需补实。
> **更新责任**：`docs/项目地图_模板.md` 的**源**在框架仓 `docs/项目地图_模板.md`；框架模板更新时，同步本目录副本（副本文件头已标注来源）。