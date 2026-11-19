#!/usr/bin/env node
/**
 * 事务边界核查（纵深期 D2.3 · 一次性核查脚本，**不作门控**）
 *
 * 背景：2026-10-26 用户裁定——D2.3 事务静态守卫**暂缓入门**，先出清单逐处判清（违规面降到 ≤5 再议入门）。
 * 本脚本＝该裁定的落点：启发式扫「服务方法体内 ≥N 次写仓储调用、且看不到事务边界」的候选，
 * 输出清单供人工逐处判读；**exit 恒 0**，不接 CI、不接回归。
 *
 * 用法：node scripts/tx-boundary-audit.mjs [--min-writes 2] [--json]
 *
 * 已知误报面（判读时必带这四问，勿直接照单加事务）：
 *  ① `[UnitOfWork]` 挂在**调用侧**（控制器/上层服务）→ 本脚本只看方法自身前置特性，会误报；
 *  ② 批量写＝单语句（`UpdateAsync(list)`/`DeleteAsync(ids)`/`AsUpdateable().SplitReason()`）→ 本身原子，无需事务；
 *  ③ 仓储内部自带事务（`UseTranAsync` 在仓储实现里）→ 服务侧看不到；
 *  ④ 日志/计数/OTP 消费类写入**有意最终一致**（加事务反而拉长锁窗口）——纵深期 D2.3 只点名"业务多写"。
 */
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const argv = process.argv.slice(2);
const ROOT = argv.find((a) => !a.startsWith('--')) ?? '.';
const MIN_WRITES = Number(argv[argv.indexOf('--min-writes') + 1] ?? 2);
const AS_JSON = argv.includes('--json');

// 只认「仓储语义接收者」上的写调用（本仓命名规范：_xxxRep / _xxxRepository / _db），
// 否则 List.Add / DI AddScoped 全算写调用——首版实测误报到 258 条不可用（2026-10-26 实锤）。
const WRITE_RE = /(?:^|[^.\w])(?:_?\w*(?:Rep|Rep\d*|Repository|Repo)\w*|_db)\s*\.\s*(?:Insert|Update|Delete|Add|SaveChange|AsUpdateable|AsInsertable|AsDeleteable|StoredProcedure|ExecuteCommand)\w*\s*(?:<[^>]*>)?\s*\(/g;
const METHOD_RE = /^\s*(?:public|internal|private|protected)\s+(?:async\s+)?(?:static\s+)?[\w<>?,\[\]\s\.]+?\s+(\w+)\s*\(/;
const TX_HINTS = ['UnitOfWork', 'UseTranAsync', 'BeginTran', 'CommitTran'];

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) yield* walk(p);
    else if (name.endsWith('.cs')) yield p;
  }
}

const findings = [];
const targets = ['src/Core/Services', 'src/Web.Core', 'src/Business'].filter((d) => {
  try { return statSync(join(ROOT, d)).isDirectory(); } catch { return false; }
});

for (const base of targets) {
  for (const file of walk(join(ROOT, base))) {
    const lines = readFileSync(file, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const m = METHOD_RE.exec(lines[i]);
      if (!m) continue;
      // 花括号配平取方法体（跳过签名行到首个 {）
      let depth = 0, started = false, j = i, body = [];
      while (j < lines.length) {
        body.push(lines[j]);
        depth += (lines[j].match(/{/g) || []).length - (lines[j].match(/}/g) || []).length;
        if (lines[j].includes('{')) started = true;
        j++;
        if (started && depth <= 0) break;
      }
      const bodyText = body.join('\n');
      if (!bodyText.includes('{')) continue;
      const writes = (bodyText.match(WRITE_RE) || []).length;
      if (writes < MIN_WRITES) continue;
      const attrs = lines.slice(Math.max(0, i - 5), i).join('\n');
      if (TX_HINTS.some((h) => attrs.includes(`[${h}`)) || TX_HINTS.some((h) => bodyText.includes(h))) continue;
      findings.push({
        file: relative(ROOT, file),
        line: i + 1,
        method: m[1],
        writes,
        selfTx: TX_HINTS.some((h) => bodyText.includes(h)),
      });
    }
  }
}

findings.sort((a, b) => b.writes - a.writes || a.file.localeCompare(b.file));

if (AS_JSON) {
  console.log(JSON.stringify({ count: findings.length, findings }, null, 2));
} else {
  console.log(`# 事务边界核查（D2.3·非门控，判读四问见脚本头注释）`);
  console.log(`# 扫描面：${targets.join(' / ')}｜阈值：写调用 ≥${MIN_WRITES} 次\n`);
  for (const f of findings) {
    console.log(`${String(f.writes).padStart(2)} 写  ${f.file}:${f.line}  ${f.method}()`);
  }
  console.log(`\n候选合计：${findings.length} 处`);
  console.log('下一步（逐处判读，勿批量加事务）：');
  console.log('  ① 调用侧是否已有 [UnitOfWork]？ ② 是否单语句批量写？ ③ 仓储内部是否已 UseTranAsync？');
  console.log('  ④ 是否属"日志/计数/OTP 消费"有意最终一致？');
  console.log('  ⇒ 判清后真缺口修在同批；候选降到 ≤5 时再议入门（用户 2026-10-26 裁定口径）。');
}
process.exit(0);
