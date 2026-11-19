# CHANGELOG

> **版本惯例（轻量 semver，G36·2026-10-25 起）**：`v<主>.<次>.<修订>`——**主**＝破坏性（实体基类/鉴权/响应契约级变更）；**次**＝批次功能（批 X 系列，向下兼容）；**修订**＝缺陷修复/口径微调。tag 随发版打（`git tag -a vX.Y.Z -m "..."` 并 push），**下游 fork 消费方式**＝`git log --oneline v旧..v新` ＋ `git diff v旧..v新 -- src web docs`（按兴趣目录裁剪）；本文件只记人话摘要，权威明细在代码与 `docs/框架功能清单.md` 变更留痕表。
> 对外清洁快照（base-export）与本版本线并行：Snapshot 是**发布物**，tag 是**开发线锚**，二者以本文件日期行对齐。

## v1.3.0 — 未发布（S-G 纵深期·安全止血与数据底座首批，2026-10-26）

### Breaking（破坏性·行为收紧，升级方需注意）
- **平台级配置写侧收口为仅超管**（`112b274`，用户裁定 ㉖）：`SysConfigService` 的 `AddAsync`／`UpdateAsync`／`DeleteAsync` 现**先过超管闸**（判定单点 `UserManager.IsSuperAdmin ⇐ Claim AccountType==999`），非超管调用**一律抛 `common.forbidden`**——**原先"被授予 `sysConfig/add|update|delete` 菜单权限的普通角色（含租户内角色）也能改平台系统设置"的行为不再成立**。
  - **为什么算破坏性而不是 bug 修复**：这是安全语义收紧。`sys_config` 现有 4 行（`sys_oplog`／`sys_upgrade`／`sys_otp_enabled`／`sys_single_login`）**全是平台行为开关**，其中登录 OTP 在 `AuthService.cs:79` 的注释原文即「登录 OTP 全局开关」——**交给租户改＝让租户能自行关掉平台强制的 2FA**。
  - **升级方迁移指引**：① 确需"某角色可改配置"→ 用超管账号操作，或**另建业务配置表**承载业务设置，**不要往 `sys_config` 塞**；② 收口粒度＝**按表**（整表平台域），刻意**不做按行"哪些键可下放"的规则引擎**（避免过度设计）；③ 前端"参数管理"页面对非超管仍可见（可见性由既有菜单授权控制），写入会得到 `common.forbidden`，**本批不改前端**。
  - ⚠ **本节同时是 D6.2 的落地**：纵深期原述"CHANGELOG 补 Breaking 段"由此补齐（此前只有 v1.0.0 有一个「### Changed（破坏性/口径）」小节，v1.1–v1.3 各版无 Breaking 段）。

### Breaking（续·2026-10-30）
- **声明式种子写库语义由"主键 upsert"改为"缺行插入、已有跳过"**（上游缺陷上报《种子冲突语义缺陷》第一步）：`[IncreSeed]`＋`ISqlSugarEntitySeedData<T>`（含租户库 `InitTenantSeedData`）每次启动不再按主键整行覆写既有行——种子只做初始引导，**运行期/租户修改跨部署存活**（下游实证：`sys_otp_enabled` 被管理员改 False 后一次常规部署重启即被静默覆回 True，登录强制 OTP、管理面自动化失联）。
  - **升级方迁移指引**：此前靠种子 upsert 自动完成的"存量库结构演进"（如菜单 Pid 重挂、配置默认值变更）**升级后不再自动生效**；须等第二步版本化升级件（版本表＋有序脚本＋升级前备份＋失败即停，计划书 `docs/业务/规划/计划_版本化升级件机制_2026-10-30.md`，待评审）落地后按升级件显式执行。纯新增行仍由种子自动补插，无需干预。
  - **验证**：`BatchSeedInsertOnlyTest` 2/2（真实 PG：菜单种子行运行期改名重启不覆写／缺行插入·已有跳过·删行再补插）；全量回归 **647/647**（10.2985 分钟，基线 645→647）；严格门 0W/0E。

### Added
- **发布仓新增《开发规范》**（`docs/开发规范.md`）：业务侧**约定 1–14**（业务落点／编码 BOM·LF／异步／实体种子增量／凭据／目录命名／框架上游／最小改动／增量更新／异常与错误码／复用优先／验证门／文档同批／全栈闭环）——解决发布仓内指南与索引共 **28 处「约定 #N」引用无处可查**的问题（约定源原本只存在于内部开发件、不随发布仓分发）；**编号与框架文档既有引用严格一致**，故无需改指南正文。同批更新导出模板 `README.md`／`docs/README.md`（文档表、选读表、四份权威文档边界、编号约定）与 `allowlist.txt`；设计精华参考两处内部文档引用改为自包含。
- **审计日志出口脱敏**（S-G B1a）：`LogDataRedactor`（敏感属性名/`otpauth://` 值遮蔽＋4096 截断）接入 `OpLogFilter` 的 RequestParam/ReturnResult——修复「登录响应 TOTP secret 落审计库、改密/重置密钥入参明文、SysMenu/Page 整包 26KB 入日志」三类泄露；`BatchSGSecurityExitTest` 5 测
- **分页上限单点**（S-G B1c）：`PageConst.MaxPageSize=200`，全仓 22 处分页调用统一 `Math.Clamp`（含 `SysJobService` 2 处裸 `input.PageSize` 由用例抓出补齐）＋代码生成器模板同步；`BatchSGPageSizeClampTest` 3 测（真实 PG 钳制读数＋源码防回潮扫描）
- **数据底座二级索引**（S-G B2a）：17 条 `[SugarIndex]`（日志族 7 表 `CreateTime DESC`、sys_user Account/OrgId/Status、sys_role_menu/sys_user_role 关联列、sys_menu/sys_org Pid、sys_notice_user UserId）——dev 库 `pg_indexes` 49→65；`sys_job_log ORDER BY "CreateTime" DESC` 由 Seq Scan 全表 6.3ms → **Index Scan 0.134ms**
- **唯一性底座**（S-G B2b·用户批准红线①）：`SchemaIntegrityInitializer` 启动幂等建 **6 条部分唯一索引**（`WHERE "IsDelete"=false`，软删行不占键；`sys_menu.Permission` 不建全局唯一——域规则是"同父判重"，全局唯一实测过度约束已移除）＋先审计后建＋跳过清单；`DbConflictMapper` 把 23505 映射为 `common.conflict`（不再 500）；`scripts/db-integrity-audit.sql` 审计脚本；`BatchSGUniqueConstraintTest` 3 测
- **机构数据权限真正生效**（S-G D1.3·用户批准口径）：新增无租户机构基类 `EntityBaseOrg`；`SysFile`→`EntityBaseOrg`、`SysNotice`/`SysPrint`→`EntityTenantBaseData`；`SqlSugarFilter` 门槛收敛为 `IOrgIdFilter`；存量回填脚本 `scripts/db-backfill-createorgid.sql`（dev 实测：sys_file 7 行补到 1 行、sys_notice 16 行补到 4 行，余者创建人已删＝fail-closed）；`BatchSGOrgScopeTest` 5 测
- **运行面（S-G B4a）**：`TraceIdMiddleware` 让 `X-Trace-Id` 覆盖全部响应（含错误），`SysLogOp` 增 `TraceId` 列与异常档同值串联；`/health` 改**就绪探针**（实探 DB+缓存，2s，失败 503，不透传异常原文）＋新增 `/health/live` 静态存活探针；慢请求告警（`Logging:SlowRequestMs` 缺省 1000）；`BatchSGTraceHealthTest` 12 测（TestServer 真管线验响应头）
- **HTTP 入站契约冒烟（S-G B4b）**：`WebApplicationFactory<Program>` 夹具（`Microsoft.AspNetCore.Mvc.Testing` 10.0.12）＋5 条契约（health 200／匿名 401×2／登录非法 body 400 双口径／未知路由 404）——此类"层间组合缺陷"从此进回归，不再只靠 /tmp 人肉 e2e
- **前端质量（S-G B6a）**：`err` 词表 **40→163/163**（`scripts/gen-fe-err-i18n.mjs` 幂等生成＋`--check`，与后端 `ErrorCodes`/`Lang` 单源）＋修孤儿键；G25 六件套业务视图采用 **2→9/27**（sysServer 手写 `.ba-empty` 换 `BaEmpty`＋`BaSkeleton` 等 8 视图）；`NOTICE_TYPE_LABELS` 运行时中文改走 i18n
- **分页原语收口（S-G B5b）**：新增 `IPageInput` 契约（20 个 `Page*Input` record 实现，公开构造/JSON 形态零变化）＋`PageQueryExtensions`（`NormalizePaging`/`ToPageAsync`×2/`ToPageListAsync(IPageInput)`）；**21 处分页调用点**收口，服务层 `Math.Clamp(input.PageSize` 归零；`BatchSGPageQueryTest` 9 测（含"新 `Page*Input` 漏接接口即红"契约扫描）
- **错误码覆盖（S-G B5a）**：A 域（Auth/User/OTP/Password/File/Import/Export/OpenAccess）**新钉 30 条**（32 测，逐条 `ErrorCodes.X` 精确断言）；新增只读审计脚本 `scripts/error-code-coverage.mjs`（163 常量 × 测试引用，输出覆盖率＋"真缺口/预留码"分类）——覆盖率 **42/163→82/163（50.3%）**；父层小片补 7 条（config/字典/菜单校验分支，`BatchSGErrCodeCoverageExtraTest` 3 测）→ **89/163（54.6%）**，余量清单入 G40
- **删用户级联补全**（S-G B1b）：`DeleteAsync` 增清 `sys_user_otp`/`sys_user_password_record`；`SysMenuController.Delete` 补 `[UnitOfWork]`；`BatchSGConsistencyTest` 2 测
- **文件日志行加请求关联标识 `ctx`**（纵深期 W6·初版 `85e1702`，本批修正实现）：`FileLoggerProvider` 每行输出 `[ctx=<id>]`，**取值＝`HttpContext.TraceIdentifier`**，与本仓既有三面（响应头 `X-Trace-Id`／`SysLogOp.TraceId`／`SysLogEx.TraceId`）**同值**。为什么需要：文件日志是**全局单通道追加**，并发请求行必然交错，此前只能靠时间戳猜归属。**真机验**：响应头 `X-Trace-Id: 0HNPJUJRTV8UK:00000001` ⇔ 日志行 `[ctx=0HNPJUJRTV8UK:00000001]`。⚠ **初版取 `Activity.Current?.TraceId` 已被真机证伪**：默认无 `ActivityListener` 订阅 ⇒ Kestrel 不为请求创建 Activity ⇒ 请求管道内 `Activity.Current == null`，**真实请求根本不打 ctx**（当时看到的 ctx 来自启动期），且 32 位 hex 与响应头的 `ConnectionId:RequestNumber` **不同值不同形** ⇒ **教训：编译过＋"日志里能看到 ctx"都不算验收，必须验"两端是同一个 id"**。Activity 现仅作后台线程回落；两者都取不到则整段不输出。⚠ 下游若有解析日志行的正则需适配新段；⚠ 实现细节：provider 由 `AddProvider(实例)` 改**工厂注册**才能注入 `IHttpContextAccessor`（`AddHttpContextAccessor` 内部 TryAddSingleton 幂等；本 provider 的 `Dispose` 已 `_disposed` 幂等，容器与 LoggerFactory 双 Dispose 安全）。

### Changed
- **提交即对齐发布仓（2026-10-26 用户定规·常设）**：新增 `scripts/base-export/sync-base.py`（零依赖 python3，等价 `export-base.ps1` Snapshot＋`sanitize.ps1`＋`verify-base.ps1`）——每批提交 push 后同批导出干净快照到发布仓 `BaseAdminCore` 并推送（非 force、verify 不过即拒、打印回滚点）；规则见 `CLAUDE.md` 约定16
- **分页防回潮门控获批（2026-10-26 用户批准·红线⑤）**：源码扫描从 `BatchSGPageSizeClampTest` 拆为零库独立类 `BatchSGPageGateTest`，登记进 `CLAUDE.md` 零库档预设（`~BatchSGPageGate`），飞检零库可跑

### Fixed
- **匿名面收口**（S-G B1a）：`DemoController` 类级 `[Authorize]`——修复匿名可跨租户枚举 `sys_user`（account/realName）
- **越权收口**（S-G B3a）：`SysUserService.ResetPwd`/`GrantRoleAsync` 补超管（999）守卫（次级管理员持端点即可重置超管口令/改其角色链＝提权）；`SysNoticeService.GetDetailAsync` 补「发布人∪收件人」判定（原仅登录即可按 id 读任意公告/草稿，IDOR）；复用既有错误码 `user.super_admin_readonly`/`notice.not_found`；`BatchSGSecurityDeepTest` 4 测（含 admin 口令哈希/角色行数未动断言）

## v1.2.0 — 2026-10-25（开发线 tag `v1.2.0`@`079ccba`；发布仓快照 `3cd2633`）

### Added
- **批 UX 框架级页面标准**（用户点名"以后不能粗糙"）：可用性审单 `docs/可用性审单_框架管理页.md`＝新管理页验收单（达标清单 22/22）；页型模板三型 `web/templates/`（列表/树表/日志查询，关键位带审单条目号注释＋复制试跑负控自证）；指南 §54"管理页从模板起，不从零手搓"；公共件 `formatDateTime`/`BaActionButtons`（BaEmpty 既有）
- 筛选参数族（G38/B1a·B1b）：user/org/pos `Status`、job `Enabled`、role/dictType/dictData `Keyword`、logEx `LogLevel`、logLogin `LoginType`、tenant `Status`（＋BatchUX 十测，基线 **508/508**）
- **sysFile 分页清单端点** `GET /api/sysFile/page`（B1c：FileName 模糊＋时间范围，Output 脱敏无存储路径/存储档；菜单种子 10188）——文件页"刷新即丢"根除（F9 前端全链路）
- **时间范围筛选族（G38/B1d＋F10）**：tenant/notice/openAccess/sysPrint/sysSerial 五域新增 `StartDate/EndDate`（CreateTime 闭区间；通知接收视图同口径）＋五页 daterange 接入；`BatchUXDateRangeTest` 五测。**全量回归基线刷新 508→516/516**（2026-10-26 实跑 39m24s；S-G 纵深期末再刷新 538/538，见下）

### Fixed
- **P0×5**：sysDict 值停用无确认、menu 子树停用无确认、sysSerial 筛选全失效（fetcher 读未接线参数恒 null）＋停用无确认、sysPrint 详情失败保存清空模板（detailLoaded 双拦）
- **渲染缺陷**：menu `#cell_type` 声明插槽无模板＝空白列、role `#cell_sys` 裸 true/false
- **sysServer 权限门**：无权限节点快照/实时卡照发请求→403 连坐；`Promise.all`→`allSettled`＋逐卡判权
- P1≈50 项：全站时间列 `formatDateTime`、长列 tooltip、危险动作确认补齐、per-row 防重（finally 复位）、操作列收纳（前2直出＋更多）、吞错可见、下拉 remote 搜索拆 200 墙、空态/loading 口径

## v1.1.0 — 2026-10-25（开发线 tag `v1.1.0`@`134a2a0`；发布仓快照 `ab92df6`）

### Added
- 命令式启动种子 `IStartupSeed`（G32）：启动生命周期自动执行＋"未执行面"WARNING 噪音化＋`StartupSeed:Enabled/ContinueOnFailure`；指南 §52
- **long id JSON 出口全局字符串化（G33·破坏性已豁免：Read 双兼容，旧客户端无损）**：`AdminJsonOptions` 出口单源（MVC/异常/401/**SignalR**）＋数值语义白名单（DataScope/SizeKb/Elapsed*/*Bytes/Total 保持 number）；前端 id 类型面 `number | string`；Excel/CSV 导出 long 落文本单元格；指南 §53
- 测试分域三档预设（G35）：零库档 ~12s（无 PG 可跑，下游仓友好），入 CLAUDE.md

### Fixed
- 前端 menu 页 5 处 `Number(row.id)` 对新建雪花 id 丢精度（G33b 顺带修复的**现存 bug**）

## v1.0.0 — 2026-10-25

首个版本锚，覆盖 S-F 框架主线（2026-10-06 封段，批 A–BF，81 功能点 ✅68）＋按需/下游批线（批 BG–CPΔ）。

### Added（要点）
- 通用列表导出 `ExportService`＋`[ExportColumn]`（批 CO，指南 §50）
- 通用数据导入 `ImportService`＋`[ImportColumn]`＋全回滚语义，代表端点 `sysPos/import`（批 CP，指南 §51）
- 站点身份配置化 `App:Name` 单一真源（批 AY/G24，指南 §47）
- OTP 开发态全局开关 `sys_otp_enabled`（批 BB，指南 §49）
- 分区加载态六件套 `v-ba-loading`/`BaEmpty`/`BaSkeleton`/`usePolling`/`.ba-inline-error`/`pendingCount`（批 BD/G25，下游别再重造）
- 接口文档框架/业务分区＋OpenAPI JSON 下载（批 BE）；代码生成器基类结构推断（批 BF）
- 项目地图模板＋新仓脚手架 `templates/dotnet-repo`（t674）

### Changed（破坏性/口径）
- **导入空文件口径**：解析后零数据行（含仅表头文件）报 `import.file_empty`，不再回"0 行成功"（批 CPΔ/C5b，2026-10-25 批准）
- 错误码 `域.语义` 稳定面（批 AQ/AR）：业务异常一律 `errCode`＋`msg` 本地化，代码零写死中文

### Known gaps（下游注意）
- G33：long id 仍序列化为 JSON number（>2^53 前端丢精度）——全局 long→string 落地前，业务 long 字段自行 ToString
- G32：imperative 种子不在启动生命周期执行且无未执行面观测——用声明式 `[IncreSeed]`
