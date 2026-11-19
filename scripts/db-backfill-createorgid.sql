-- ============================================================================
-- D1.3 S-G 纵深·机构数据权限纳管存量回填脚本(sys_file / sys_notice / sys_print)
-- ============================================================================
-- 背景:D1.3 前 SqlSugarFilter 的类型门槛只收继承 EntityBaseData/EntityTenantBaseData 的实体,
-- 仓内业务表无一命中 ⇒ DataScope=本部门(3)/本部门及以下(2)/自定义(5) 对生产实体等价全部,
-- 三表存量行 CreateOrgId 均为 NULL(机制未生效,写入侧 AOP 也不会填)。
-- D1.3 纳管后:sys_file(EntityBaseOrg)/sys_notice/sys_print(EntityTenantBaseData) 的
-- CreateOrgId 参与机构范围行级过滤 + 插入 AOP 自动填。
--
-- fail-closed 语义(必读):
--   机构范围过滤器对 CreateOrgId IS NULL 的行一律不可见(空值不落入任何可见机构集,见 SqlSugarFilter
--   BuildCondition 的 Contains(机构集) 条件)。因此回填不到的行(创建人已物理删除/创建人 OrgId 为空)
--   在非-All 数据范围的用户视角下将【永久不可见】——这是刻意的 fail-closed 安全语义,不是数据丢失;
--   需要找回时由管理员显式修复该行 CreateOrgId(或给相关角色改授 DataScope=All)。
--
-- 运行时机:
--   部署 D1.3 版本【之前】,由运维/开发【手工执行或挂入迁移脚本流程】——【不得】挂应用启动
--   (启动挂回填会把部署失败点引入健康检查路径,且启动竞态下未回填完成即有用户在线可见性抖动)。
--   执行前先跑下方「执行前计数」留存基线,执行后跑「执行后计数」核对。
--
-- 前置条件(D1.3 实测):三表存量结构【无 CreateOrgId 列】(纳管前 CodeFirst 从未为这三表建过该列)——
--   须先以新版应用启动或测试 InitTables 走 CodeFirst 增量建列(幂等),确认列存在后再跑本脚本
--   (可用 \d sys_file 或下方基线计数语句自检:列不存在时基线语句即报 column does not exist)。
--
-- 幂等性:UPDATE 仅命中 CreateOrgId IS NULL 且 CreateUserId IS NOT NULL 的行;
--   可回填行回填后不再命中;遗留行(creator 缺失/机构空)每次重跑仍命中但写入同值 NULL
--   (数据不变,D1.3 dev 实测:复跑 UPDATE 行数=遗留行数) ⇒ 可安全重复执行。
-- 回填口径:按创建人(sys_user.id = 本表 CreateUserId)的用户所属机构(OrgId)回填;
--   创建人缺失(已物理删除)或创建人 OrgId 为空的行保持 NULL(输出计数,人工裁决)。
-- ============================================================================

-- 执行前计数(基线留存,输出:总行数 / CreateOrgId 为空行数 / 其中创建人可解析行数)
SELECT 'sys_file'  AS tbl, COUNT(*) AS total,
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL) AS null_org,
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL) AS backfillable
FROM sys_file
UNION ALL
SELECT 'sys_notice', COUNT(*),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL)
FROM sys_notice
UNION ALL
SELECT 'sys_print', COUNT(*),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL)
FROM sys_print;

-- 1/3 sys_file
UPDATE sys_file SET "CreateOrgId" = (SELECT u."OrgId" FROM sys_user u WHERE u.id = sys_file."CreateUserId")
WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL;

-- 2/3 sys_notice
UPDATE sys_notice SET "CreateOrgId" = (SELECT u."OrgId" FROM sys_user u WHERE u.id = sys_notice."CreateUserId")
WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL;

-- 3/3 sys_print
UPDATE sys_print SET "CreateOrgId" = (SELECT u."OrgId" FROM sys_user u WHERE u.id = sys_print."CreateUserId")
WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL;

-- 执行后计数(验收核对:backfillable 应为 0;若 null_org > 0 = creator 缺失/机构为空的遗留行,人工裁决)
SELECT 'sys_file'  AS tbl,
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL) AS null_org_after,
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL) AS backfillable_after
FROM sys_file
UNION ALL
SELECT 'sys_notice',
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL)
FROM sys_notice
UNION ALL
SELECT 'sys_print',
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL),
       COUNT(*) FILTER (WHERE "CreateOrgId" IS NULL AND "CreateUserId" IS NOT NULL)
FROM sys_print;
