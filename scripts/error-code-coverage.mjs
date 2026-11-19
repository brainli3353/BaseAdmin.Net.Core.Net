#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
// 错误码覆盖审计脚本(批 S-G 纵深·B5a1 臂交付,只读零依赖)
//
// 用途:解析 src/Core/Exceptions/ErrorCodes.cs 全部稳定错误码常量,扫描 src/Test/*.cs
//      的引用(ErrorCodes.<Const> 与字面 "<域.语义>" 双口径),输出「已覆盖/未覆盖」
//      Markdown 清单 + 覆盖率。B5a2(B 域臂)经 `--domain=<域>` 复用本脚本。
//
// 口径(与派工 brief 一致):
//   - 已覆盖 = src/Test 任意 *.cs 中出现 `ErrorCodes.<Const>` 或字面 "<码值>"
//     (断言/触发/资源键断言均算——审计口径是"测试对该码有任何钉住",触发路径
//      深度由人工/臂内复核,脚本只做清单归集);
//   - 未覆盖清单附带「生产引用数」(src/Core + src/Web.Core 的 ErrorCodes.<Const>
//     出现次数):生产 0 引用 = 预留稳定码(无抛出点,无法自然触发),与
//     "有抛出点但测试没钉住"的真缺口分开列,便于按风险排序。
//
// 用法:
//   node scripts/error-code-coverage.mjs                # 全量(所有域)
//   node scripts/error-code-coverage.mjs --domain=A     # 只看 A 域(Auth/User/OTP/Password/File/Import/Export/OpenAccess)
//   node scripts/error-code-coverage.mjs --domain=A,C   # 多域合并
//
// 只读保证:不写任何文件,报告走 stdout;诊断/错误走 stderr;退出码 0=正常。
// ─────────────────────────────────────────────────────────────────────────────
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..');

// ── 域映射(码值首段前缀 → 域)。字母编号沿用 S-G 六臂分域:A 臂在本文件内定案;
//    其余域为按 ErrorCodes.cs 分组注释的合理归类,B5a2 若有出入只改本表,不改逻辑。 ──
const DOMAIN_BY_PREFIX = {
  auth: 'A', user: 'A', import: 'A', file: 'A', open_access: 'A', // A 域:鉴权/用户/OTP/密码/导入/文件/开放接口
  role: 'B', field_scope: 'B', menu: 'B', config: 'B', org: 'B', pos: 'B', tenant: 'B', // B 域:RBAC/组织/租户
  dict: 'C', serial: 'C', enum: 'C', column_custom: 'C', dynamic_query: 'C', // C 域:数据·工具
  job: 'D', print: 'D', notice: 'D', message: 'D', log: 'D', // D 域:业务功能
  database: 'E', tenant_db: 'E', code_gen: 'E', common: 'E', // E 域:基础设施·通用
};
// A 域特例:common.open_api_* 语义属开放接口(OpenAccess),归 A(E 域的 common 里剔除)。
const A_DOMAIN_PREFIX_OVERRIDES = (code) => code === 'common.open_api_scheme_missing' || code === 'common.open_api_auth_failed' ? 'A' : null;

const DOMAIN_TITLES = {
  A: 'A 域:Auth / User / OTP / Password / File / Import / Export / OpenAccess',
  B: 'B 域:RBAC / 组织 / 租户 / 字段权限 / 菜单 / 配置',
  C: 'C 域:字典 / 流水号 / 枚举 / 列设置 / 动态查询',
  D: 'D 域:任务 / 打印 / 公告 / 消息 / 日志',
  E: 'E 域:文件外基础设施 / 通用',
};

// ── 参数 ──
const args = process.argv.slice(2);
let domainFilter = null;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--domain') domainFilter = args[++i] ?? null;
  else if (args[i].startsWith('--domain=')) domainFilter = args[i].slice('--domain='.length);
}
const knownDomains = new Set(Object.values(DOMAIN_BY_PREFIX));
knownDomains.add('all');
if (domainFilter) {
  const wanted = domainFilter.split(',').map((s) => s.trim().toUpperCase()).filter(Boolean);
  const unknown = wanted.filter((d) => d !== 'ALL' && !knownDomains.has(d));
  if (unknown.length > 0) {
    console.error(`未知域: ${unknown.join(', ')}。可用域: ${[...knownDomains].sort().join(', ')}`);
    process.exit(1);
  }
  domainFilter = wanted;
}

// ── ① 解析 ErrorCodes.cs ──
const errorCodesPath = join(repoRoot, 'src', 'Core', 'Exceptions', 'ErrorCodes.cs');
const src = readFileSync(errorCodesPath, 'utf8');
const codes = []; // { constName, code, domain }
for (const m of src.matchAll(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"/g)) {
  const [, constName, code] = m;
  const prefix = code.split('.')[0];
  const domain = A_DOMAIN_PREFIX_OVERRIDES(code) ?? DOMAIN_BY_PREFIX[prefix] ?? '?';
  codes.push({ constName, code, domain });
}
if (codes.length === 0) {
  console.error(`未能从 ${errorCodesPath} 解析出任何错误码常量——解析模式与文件不匹配?`);
  process.exit(1);
}

// ── ② 扫测试引用(src/Test/*.cs)与生产引用(src/Core|src/Web.Core 递归 .cs) ──
function listCsFiles(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) out.push(...listCsFiles(p));
    else if (name.endsWith('.cs')) out.push(p);
  }
  return out;
}
// 相对 repoRoot 的短路径(报告可读)
const rel = (p) => p.slice(repoRoot.length + 1).replaceAll('\\', '/');

const testFiles = listCsFiles(join(repoRoot, 'src', 'Test')).map(rel);
const testTexts = testFiles.map((f) => ({ file: f, text: readFileSync(join(repoRoot, f), 'utf8') }));
const prodFiles = [
  ...listCsFiles(join(repoRoot, 'src', 'Core')),
  ...listCsFiles(join(repoRoot, 'src', 'Web.Core')),
].map(rel);
const prodTexts = prodFiles.map((f) => ({ file: f, text: readFileSync(join(repoRoot, f), 'utf8') }));

function coverageOf(entry) {
  const { constName, code } = entry;
  const constRe = new RegExp(`ErrorCodes\\.${constName}\\b`);
  const litRe = new RegExp(`"${code.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}"`);
  const hits = testTexts.filter((t) => constRe.test(t.text) || litRe.test(t.text)).map((t) => t.file);
  const prodRefs = prodTexts.filter((t) => constRe.test(t.text)).length; // 生产抛出点文件数(按文件去重)
  return { hits, prodRefs };
}

// ── ③ 出报告 ──
const lines = [];
const p = (s = '') => lines.push(s);
p('# 错误码覆盖审计报告');
p();
p(`- 生成: \`node scripts/error-code-coverage.mjs${domainFilter ? ` --domain=${domainFilter.join(',')}` : ''}\` (只读脚本,零依赖)`);
p(`- 错误码单源: \`src/Core/Exceptions/ErrorCodes.cs\` (共 ${codes.length} 常量)`);
p(`- 测试扫描面: \`src/Test/*.cs\` ${testFiles.length} 文件;生产引用面: \`src/Core\`+\`src/Web.Core\` ${prodFiles.length} 文件`);
p(`- 覆盖口径: 测试中出现 \`ErrorCodes.<Const>\` 或字面 \`"<码值>"\` 即计已覆盖(触发深度由臂内复核)`);
if (domainFilter && !domainFilter.includes('ALL')) {
  p(`- 域过滤: ${domainFilter.map((d) => `${d}(${DOMAIN_TITLES[d] ?? '?'})`).join('、')}`);
}
p();

const inScope = codes.filter((c) => !domainFilter || domainFilter.includes('ALL') || domainFilter.includes(c.domain));
const covered = [];
const uncovered = [];
for (const c of inScope) {
  const { hits, prodRefs } = coverageOf(c);
  (hits.length > 0 ? covered : uncovered).push({ ...c, hits, prodRefs });
}
const rate = inScope.length === 0 ? 0 : ((covered.length / inScope.length) * 100).toFixed(1);
// 受控故障码（2026-10-26 pending-approval-review 裁定选项②）：**有生产抛出点，但触发需要真实环境故障**
// ⇒ 既不该写假测试冒充覆盖，也不该算进"可测而未测"的真缺口反复重估。与"预留码(0 引用)"的区别＝这里确实会抛，
// 只是零副作用触发不了。新增条目必须附可点验的理由（为什么触发不了），否则不加。
const UNTRIGGERABLE = {
  'tenant_db.create_failed': '抛出点 TenantDbRegistry.cs:268 只包真 CREATE DATABASE 失败；而 :254 先 DatabaseExistsAsync 存在即返回 false 不抛 ⇒ 重复库名拿不到失败，正则挡掉的非法名走 db_name_invalid，余下只有权限/磁盘级故障可触发（红线①不可控）',
  'serial.type_duplicated': '抛出点 SysSerialService.cs:224-227（GetTypeList）：判定条件是"`typeof(SysSerialService).Assembly` 内实现 ISerialTypeProvider 的类存在重复 string 常量值"。⇒ ①扫描范围**写死 Core 程序集**，测试程序集里造重复类型扫不到（实读 `:216` 的 Assembly 取数）；②当前 Core 内无重复 ⇒ 该码**不可自然触发**，性质＝启动期防漂移守卫码（有人新注册重复类型时才炸）。**不写假对象伪造触发条件**（2026-10-26 覆盖片实跑后改判）',
  'serial.advance_failed': '抛出点 SysSerialService.cs:289：条件＝持锁临界区内 `AsUpdateable(seq).UpdateColumns(Expy,Seq).Where(u => u.Id == seq.Id)` 的影响行数 <= 0。实读三重前置使"拿到 0"无路可走：① :240-258 已抢分布式锁（同租户同类型串行）；② seq 由**同一临界区内刚从库读出**，不是外部传入的旧 Id；③ 上限校验在 :276-280 之前，超范围走 serial.out_of_range。⇒ 正常路径单条按 Id 的 UPDATE 必返 1，要得 0 只有"锁内该行被外力物理删除"或数据库级故障（红线①不可控）；且 `_rep` 是**具体类** `SqlSugarRepository<SysSerial>`（非接口，实读 :53 构造签名）⇒ 测试侧连"返回 0 的假仓储"都注入不进去。**不造假触发**（2026-10-27 覆盖线收口改判，本条改判后真缺口＝0）',
};
const untriggerable = uncovered.filter((u) => UNTRIGGERABLE[u.code]);
const realGaps = uncovered.filter((u) => u.prodRefs > 0 && !UNTRIGGERABLE[u.code]);
p(`## 覆盖率:**${covered.length} / ${inScope.length} = ${rate}%**${domainFilter ? `(域内)` : ' (全量)'};未覆盖 ${uncovered.length} 条 = 预留码(生产 0 引用) ${uncovered.filter((u) => u.prodRefs === 0).length} 条 ＋ **受控故障码 ${untriggerable.length} 条**（有抛出点但零副作用触发不了） ＋ 真缺口 ${realGaps.length} 条`);
p();

p('## 已覆盖清单');
p();
if (covered.length === 0) p('(无)');
else {
  p('| 域 | 常量 | 码值 | 命中测试文件 |');
  p('|---|---|---|---|');
  for (const c of covered) {
    p(`| ${c.domain} | \`${c.constName}\` | \`${c.code}\` | ${c.hits.map((h) => `\`${h}\``).join('、')} |`);
  }
}
p();

p('## 未覆盖清单');
p();
if (uncovered.length === 0) p('(无——域内全覆盖)');
else {
  p('| 域 | 常量 | 码值 | 生产引用文件数 | 缺口性质 |');
  p('|---|---|---|---|---|');
  for (const c of uncovered) {
    p(`| ${c.domain} | \`${c.constName}\` | \`${c.code}\` | ${c.prodRefs} | ${c.prodRefs === 0 ? '预留码(生产无抛出点,无法自然触发)' : UNTRIGGERABLE[c.code] ? `受控故障码(有抛出点,零副作用触发不了):${UNTRIGGERABLE[c.code]}` : '真缺口(有抛出点,测试未钉住)'} |`);
  }
}
p();

process.stdout.write(lines.join('\n') + '\n');
