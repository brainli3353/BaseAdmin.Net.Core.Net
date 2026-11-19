-- ─────────────────────────────────────────────────────────────
-- 清理 sys_notice_user 的孤儿接收行（2026-10-27 W3 卫生清理·㉖④ 授权内）
--
-- 【来源】巡检脚本扩面（`8b9d235`）后第一次看见：旧版 ② 段只有 4 项，跑绿曾显得"库很干净"。
-- 【范围·实读 2026-10-27 13:5x】待删 **736** 行（`NoticeId` 或 `UserId` 引用不到主行的并集）。
--   ⚠ **三者关系（执行后实测）**：`sys_notice_user` 表总行数 **740** ＝ 孤儿 **736** ＋ 正常 **4**；细分 orphan_notice **736**、orphan_user **684**，
--     而 684 **全部落在 736 之内**（同一批行既丢了通知行也丢了用户行）⇒ 待删并集＝736，**删后应剩 4 行正常行**。
--   ⚠ **本文件一度把"740"当作未复核旧口径"纠正"成 736——那个纠正是错的**：740 是**表总行数**、736 是**孤儿数**，
--     两者都对、量的是不同东西。**教训：纠正一个数之前，先确认它量的是什么。**脚本仍以第 ① 步实打行数为准。
--
-- 【三条前置检查——2026-10-27 13:43 实读做齐，缺一不得执行】
--   1) 谁依赖它：`BatchVNoticeOrphanTest` **自己造孤儿**（加两条公告→手工 `IsDelete=true` 软删其一
--      →验查询过滤孤儿行），**不依赖存量孤儿** ⇒ 清掉不破坏该测试；
--   2) 会不会清了再生：736 行 `CreateTime` **全部落在 10-06 00:18~00:21 三分钟内**＝一次跑批产物，
--      且该用例尾部有 `CleanupAsync(keepId, orphanId)` 自清理 ⇒ 不是每轮累积；
--   3) 执行时机：**并发测试全部结束后**（跑批中清理可能清到在用数据）。
--
-- 【回滚（同库同表，一条语句）】
--   INSERT INTO sys_notice_user SELECT * FROM sys_notice_user_bak_20261027;
--   ⚠ 本脚本用 `CREATE TABLE IF NOT EXISTS`：**重复执行不会向后补备份**，故本脚本按"一次性执行"设计；
--     若需二次清理，先人工改名/删除旧备份表再跑，否则恢复点不完整。
--
-- 【用法】psql -h 172.19.0.2 -U huluyeye -d huluyeye_admin -f scripts/db-cleanup-notice-orphans.sql
--         跑前先看第 ① 步打出的 `to_clean` 行数是否与 **736** 相符；不符＝现状已变，**停手重查**。
-- ─────────────────────────────────────────────────────────────

BEGIN;

-- ① 先数一遍要删多少（人工核对量级：预期 **736**；不符＝现状已变，停手重查）
SELECT count(*) AS to_clean
FROM sys_notice_user nu
LEFT JOIN sys_notice n ON n.id = nu."NoticeId"
LEFT JOIN sys_user   u ON u.id = nu."UserId"
WHERE n.id IS NULL OR u.id IS NULL;

-- ② 落回滚表（**备份必须在删除之前**，且与删除用同一个条件式，防止两边口径漂移）
CREATE TABLE IF NOT EXISTS sys_notice_user_bak_20261027 AS
SELECT nu.*
FROM sys_notice_user nu
LEFT JOIN sys_notice n ON n.id = nu."NoticeId"
LEFT JOIN sys_user   u ON u.id = nu."UserId"
WHERE n.id IS NULL OR u.id IS NULL;

SELECT count(*) AS bak_rows FROM sys_notice_user_bak_20261027;

-- ③ 删除（用 ctid 定位，避免"两表同 id"类误删；条件式与 ② 逐字相同）
DELETE FROM sys_notice_user
WHERE ctid IN (
    SELECT nu.ctid
    FROM sys_notice_user nu
    LEFT JOIN sys_notice n ON n.id = nu."NoticeId"
    LEFT JOIN sys_user   u ON u.id = nu."UserId"
    WHERE n.id IS NULL OR u.id IS NULL
);

-- ④ 复验：两行都必须归零，否则说明还有别的孤儿来源（**此时应 ROLLBACK 而不是 COMMIT**）
SELECT count(*) AS remaining_orphans
FROM sys_notice_user nu
LEFT JOIN sys_notice n ON n.id = nu."NoticeId"
LEFT JOIN sys_user   u ON u.id = nu."UserId"
WHERE n.id IS NULL OR u.id IS NULL;

SELECT count(*) AS sys_notice_user_rows_now FROM sys_notice_user;

-- 确认 ④ 的 remaining_orphans＝0 后手动提交（**故本脚本刻意不自动 COMMIT**）
-- COMMIT;
-- ROLLBACK;
