-- ─────────────────────────────────────────────────────────────────────────────
-- 批 S-G B2b · 数据底座唯一性审计(只读,可重复跑;PSG 用法:
--   PGPASSWORD=*** psql -h localhost -U huluyeye -d huluyeye_admin -f scripts/db-integrity-audit.sql)
-- 判读:① ② 两个结果集"重复/孤儿计数全 0"= 健康;③ 输出已落地的部分唯一索引定义(建后核对面)。
-- 口径与 SchemaIntegrityInitializer 一致:唯一键按未删行("IsDelete"=false)统计,
-- sys_menu 另排除 NULL/空白 Permission(与索引 WHERE 同式)。
-- 边界:GROUP BY 把 NULL TenantId 视作同组(偏保守,宁可误报不漏报);键列存量无 NULL。
-- ─────────────────────────────────────────────────────────────────────────────

-- ① 各唯一键候选的重复计数(未删行内同键成组数;>0 即该索引建不了,须先清存量)
SELECT 'u_sys_user_account'          AS index_name, count(*) AS dup_groups FROM (SELECT 1 FROM sys_user         WHERE "IsDelete"=false GROUP BY "Account","TenantId" HAVING count(*)>1) t
UNION ALL SELECT 'u_sys_role_code',          count(*) FROM (SELECT 1 FROM sys_role        WHERE "IsDelete"=false GROUP BY "Code","TenantId"     HAVING count(*)>1) t
UNION ALL SELECT 'u_sys_pos_code',           count(*) FROM (SELECT 1 FROM sys_pos         WHERE "IsDelete"=false GROUP BY "Code","TenantId"     HAVING count(*)>1) t
UNION ALL SELECT 'u_sys_dict_type_code',     count(*) FROM (SELECT 1 FROM sys_dict_type   WHERE "IsDelete"=false GROUP BY "Code","TenantId"     HAVING count(*)>1) t
UNION ALL SELECT 'u_sys_config_code',        count(*) FROM (SELECT 1 FROM sys_config      WHERE "IsDelete"=false GROUP BY "Code"                HAVING count(*)>1) t
UNION ALL SELECT 'u_sys_open_access_accesskey', count(*) FROM (SELECT 1 FROM sys_open_access WHERE "IsDelete"=false GROUP BY "AccessKey"          HAVING count(*)>1) t;
-- ⚠ 结尾分号不可省（2026-10-26 实修）：本段原缺分号 ⇒ ①② 两段被 psql 连成一条语句直接语法报错，
--    即"重复计数"与"孤儿计数"两个结果集**自脚本写下起从未真跑过**。
--    当时的落地验收靠 ③ 段（索引定义 6 行齐）——而唯一索引"能建成"本身即等价于当时无重复，
--    所以数据未被误判、底座确实就位，但这两段作为巡检工具是哑的（假绿风险：跑它的人会把"无输出"读成"健康"）。

-- ② 授权关系表孤儿计数(引用了不存在的主行 = 脏数据;>0 须排查清理)
SELECT 'sys_role_menu.orphan_role' AS check_item, count(*) AS orphans FROM sys_role_menu rm LEFT JOIN sys_role r ON r.id = rm."RoleId" WHERE r.id IS NULL
UNION ALL SELECT 'sys_role_menu.orphan_menu', count(*) FROM sys_role_menu rm LEFT JOIN sys_menu m ON m.id = rm."MenuId" WHERE m.id IS NULL
UNION ALL SELECT 'sys_user_role.orphan_user', count(*) FROM sys_user_role ur LEFT JOIN sys_user u ON u.id = ur."UserId" WHERE u.id IS NULL
UNION ALL SELECT 'sys_user_role.orphan_role', count(*) FROM sys_user_role ur LEFT JOIN sys_role r ON r.id = ur."RoleId" WHERE r.id IS NULL
-- ⚠ 以下 4 项系 2026-10-27 W3 扩面**新增**（原 ② 段只有上面 4 条 ⇒ **旧版"跑绿"仅证明 role/menu 两类关系干净，
--    不等于无孤儿**；扩面动因＝实读发现 `sys_notice_user` 740 行中 orphan_notice 736／orphan_user 684，
--    正落在旧版盲区。口径与上面 4 条一致＝引用**物理不存在**的主行（不涉软删语义）。
UNION ALL SELECT 'sys_user_otp.orphan_user', count(*) FROM sys_user_otp o LEFT JOIN sys_user u ON u.id = o."UserId" WHERE u.id IS NULL
UNION ALL SELECT 'sys_user_password_record.orphan_user', count(*) FROM sys_user_password_record p LEFT JOIN sys_user u ON u.id = p."UserId" WHERE u.id IS NULL
UNION ALL SELECT 'sys_notice_user.orphan_notice', count(*) FROM sys_notice_user nu LEFT JOIN sys_notice n ON n.id = nu."NoticeId" WHERE n.id IS NULL
UNION ALL SELECT 'sys_notice_user.orphan_user', count(*) FROM sys_notice_user nu LEFT JOIN sys_user u ON u.id = nu."UserId" WHERE u.id IS NULL;

-- ③ 已落地索引定义(7 行齐 = 唯一性底座就位;每行 indexdef 须含 UNIQUE 与 WHERE "IsDelete"=false)
SELECT indexname, indexdef FROM pg_indexes
WHERE schemaname = current_schema()
  AND indexname IN ('u_sys_user_account','u_sys_role_code','u_sys_pos_code','u_sys_dict_type_code',
                    'u_sys_config_code','u_sys_open_access_accesskey')
ORDER BY indexname;

-- ④ 全库候选唯一键**自动发现**(2026-10-26 增;只报"存在重复"的键,0 行＝存量干净,可直接上约束)
-- 用途:上任何新唯一约束/部分唯一索引**之前**的存量前置审计——不必先猜列名,由 information_schema 枚举,
--       因此新增表/新增业务键后重跑本段即可自曝,不需要维护清单(①段是"已纳管键"的定点复核,本段是"未纳管键"的敞口扫描)。
-- 语义例外(即使报出也**不该**上唯一约束,按既有裁决放行,勿据此复活):
--   · sys_log_login.Account / sys_log_op.Account —— 日志表,同账号多条记录是设计语义;
--   · sys_menu.Permission —— 「不同父节点复用同名权限键是合法配置」,见 `SchemaIntegrityInitializer.cs:70-72`
--     (2026-10-26 全量回归实证后移除);本段会因 43 行空白 Permission 成组而报它,属扫描器不理解语义。
-- 已知读数（2026-10-26 20:3x 实跑）：**真实业务重复＝0**。首轮曾报 4 项，逐条查证性质是——
--   sys_log_login.Account 28 组／sys_log_op.Account 3 组＝日志表语义（同账号多次记录合法）；
--   sys_menu.Permission 1 组＝31 行**空 Permission** 成组；sys_org.Code 1 组＝76 行**空 Code** 成组。
--   ⇒ 后两者是**空值成组、不是脏数据**：差距清单 D2.1 旧记"dev 库 sys_org.Code 已有 1 组重复"同源于此，
--     把它当"待清重复"去清会误导（真正该讨论的是"机构码是否该必填"，另一个口径问题，本段不裁）。
--   ⇒ 本段口径＝**排除 NULL 与空白**（与 B2b 部分唯一索引的 `WHERE` 同式），只报真信号，
--     避免 4 项固定噪音每次淹掉真信号（首轮我就因输出被截断＋未辨空值，差点把结论下成"业务键有脏重复"）。
SELECT format('select %L as candidate, count(*) as dup_groups from (select 1 from %I where "IsDelete"=false and %I is not null and %I::text <> '''' group by %I having count(*)>1) t having count(*)>0',
       c.table_name || '.' || c.column_name, c.table_name, c.column_name, c.column_name, c.column_name)
FROM information_schema.columns c
WHERE c.table_schema = current_schema()
  AND c.column_name IN ('Code', 'Account', 'AccessKey', 'Permission', 'Key', 'DictType', 'Group')
  AND EXISTS (SELECT 1 FROM information_schema.columns x
              WHERE x.table_name = c.table_name AND x.column_name = 'IsDelete')
ORDER BY 1
\gexec
