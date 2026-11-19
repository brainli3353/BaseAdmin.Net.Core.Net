# deploy/ — 测试版部署（5090）

> 本件承接 `CLAUDE.md` 部署条的细节（2026-10-29 从注入面下沉）；`CLAUDE.md` 只留"跑哪个脚本＋可自主执行"的判据。

## 目标形态

- **测试版常驻 hulubao（202）**：`http://192.168.1.202:5090`
- systemd 单元 `baseadmin-test`；干净 clone `/data/deploy/baseadmin-test`
- 工具链 `/data/deploy/baseadmin-test-deploy/`
- 部署配置在宿主本地 `test.env`（600，不入仓、不回显）
- **登录**：admin／Admin@123＋动态码（与 dev 同库；dev 侧清 OTP 绑定后首次登录需重新扫码）

## 脚本行为（幂等，失败自动回滚上一版）

pull → 生产配置补丁 → 前端构建 → publish → 原子切换 → 健康门。

## 触发与判据

**触发条件＝本批动过运行面（`src/**` 或 `web/**`）**；纯 `docs/**`／`scripts/**`／快件批**不部署**，但须留痕一句并给判据读数＝`git diff --name-only <5090 当前 sha>..HEAD -- src/ web/` **为空**（`0 个` ⇒ 不部署无损）。不部署≠失联：下一次动运行面的批部署时会一并带上这些文档批。

**授权**（2026-10-27 18:1x 用户裁定）：测试版部署属**常规动作、可自主执行，不再逐次请示**；**发版打 tag 仍逐次请示**。每次部署仍须留痕＋**独立验症状面**（sha／`is-active`／`/health`——脚本输出只算自陈）。

## 操作原文（从 CLAUDE.md 下沉，保留逐字细节）

- **部署**:`ssh hulubao /data/deploy/baseadmin-test-deploy/deploy-test.sh`(约定 15;幂等,输出末行 `[deploy] 完成:<sha> → http://192.168.1.202:5090`)。**直连 ssh(bash)可跑**（MCP `ssh_exec` 受控白名单模板只允许 hostname/ls/docker ps）;**2026-10-27 18:1x 裁定：测试版部署可自主执行、不再逐次请示**；发版打 tag 仍逐次请示。**测试版展示名不在仓里**:宿主 `test.env`(600)的 `APP_NAME`/`APP_FULL_NAME`/`APP_TITLE_SUFFIX` 由脚本 ①b 步幂等注入部署 clone 的 `App.json`(不注入＝用仓内 `App.json` 基础值;`APP_TITLE_SUFFIX=` **显式空**＝标题不要后缀)——改 test.env 后脚本会因"无新提交"跳过 ⇒ 先 `git -C /data/deploy/baseadmin-test checkout -q <上一提交>` 造出新旧差再跑(2026-09-28 实测手法)。- **部署触发判据（2026-10-27 定）**：约定 15 的目的是**让测试版运行行为与 main 同步**，部署是手段不是目的 ⇒ **触发条件＝本批动过运行面（`src/**` 或 `web/**`）**；**纯 `docs/**`／`scripts/**`／快件批不部署**，但须留痕一句并给判据读数＝`git diff --name-only <5090 当前 sha>..HEAD -- src/ web/` **为空**（`0 个` ⇒ 不部署无损）。不部署≠失联：下一次动运行面的批部署时会一并带上这些文档批。
  **部署 clone 的 origin 必须是开发仓 `BaseAdminCore-Dev.git`(2026-10-25 真根因实锤)**——曾误指发布快照仓 `BaseAdminCore.git`(tip＝base-export squash 快照、独立谱系、永不随开发 push 前进)致 5090 停更 7 批且报"无新提交"假象；诊断法＝`git -C /data/deploy/baseadmin-test remote -v`＋`git ls-remote origin main` 对 sha；修复＝`git remote set-url origin http://192.168.1.202:3000/LocalDevelopment/BaseAdminCore-Dev.git`;动这些文件前 `cp -p` 备份、全程不回显。

## 排障要点

- **直连 ssh(bash) 可跑**（MCP `ssh_exec` 受控白名单模板只允许 hostname/ls/docker ps）。
- **测试版展示名不在仓里**：宿主 `test.env`(600) 的 `APP_NAME`/`APP_FULL_NAME`/`APP_TITLE_SUFFIX` 由脚本 ①b 步幂等注入部署 clone 的 `App.json`（不注入＝用仓内 `App.json` 基础值；`APP_TITLE_SUFFIX=` **显式空**＝标题不要后缀）。改 `test.env` 后脚本会因"无新提交"跳过 ⇒ 先 `git -C /data/deploy/baseadmin-test checkout -q <上一提交>` 造出新旧差再跑。
