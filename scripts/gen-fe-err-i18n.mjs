#!/usr/bin/env node
/**
 * 一次性生成脚本(B6a·前端 err 词表补齐):后端错误码 → 前端 i18n `err.*` 段。
 *
 * 数据流(只读后端源,不写 src/**):
 *   1. src/Core/Exceptions/ErrorCodes.cs 提取全部 `public const string X = "域.语义";` 常量值;
 *   2. src/Core/Resources/Lang.zh-CN.json / Lang.en.json 取同键译文;
 *   3. 生成前端键 `err.<错误码去点=域_语义>`,合并进 web/src/i18n/zh-CN.ts / en-US.ts 的 err 段。
 *
 * 合并规则(幂等,可重复跑):
 *   - 前端 err 段**既有键保留既有译文**(允许前端措辞与后端微调共存,不回写覆盖);
 *   - 缺失键从后端 Lang 同键补入;后端也缺译文的键回落"域.语义"原文并计入 missingFallbacks;
 *   - 前端 err 段中不属于本次常量集的键视为孤儿删除(修 `field_scope_not_configurable` 类漂移);
 *   - 键按字母序重排(与既有段排序一致;键序对 vue-i18n 无语义)。
 *
 * 用法:
 *   node scripts/gen-fe-err-i18n.mjs            # 合并写回
 *   node scripts/gen-fe-err-i18n.mjs --check    # 只读校验:不改文件,不一致则退出码 1
 */
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const CODES_CS = path.join(repoRoot, 'src/Core/Exceptions/ErrorCodes.cs')
const LANG_FILES = {
	'zh-CN': path.join(repoRoot, 'src/Core/Resources/Lang.zh-CN.json'),
	'en-US': path.join(repoRoot, 'src/Core/Resources/Lang.en.json')
}
const FE_I18N_FILES = {
	'zh-CN': path.join(repoRoot, 'web/src/i18n/zh-CN.ts'),
	'en-US': path.join(repoRoot, 'web/src/i18n/en-US.ts')
}

const checkOnly = process.argv.includes('--check')

/** 1. 提取错误码常量值(形如 public const string X = "a.b";) */
function extractCodes(csText) {
	const codes = [...csText.matchAll(/public const string \w+ = "([^"]+)";/g)].map(m => m[1])
	const dup = codes.filter((c, i) => codes.indexOf(c) !== i)
	if (dup.length) throw new Error(`ErrorCodes.cs 常量值重复: ${[...new Set(dup)].join(', ')}`)
	return codes
}

/** 2. 解析前端 i18n 文件的 err 段(以 `\terr: {` 起、以独占一行的 `\t}` 止),返回 {start,end,closeHasComma,entries} */
function parseErrSection(tsText, file) {
	const startMatch = tsText.match(/^\terr: \{\n/m)
	if (!startMatch) throw new Error(`${file}: 未找到 err 段起点`)
	const start = startMatch.index + startMatch[0].length
	let i = start
	const entries = new Map() // feKey -> value(字符串,保留原样)
	while (true) {
		const lineEnd = tsText.indexOf('\n', i)
		if (lineEnd === -1) throw new Error(`${file}: err 段未闭合`)
		const line = tsText.slice(i, lineEnd)
		const trimmed = line.trim()
		if (trimmed === '}' || trimmed === '},') {
			return { start, end: lineEnd, closeHasComma: trimmed === '},', entries }
		}
		const m = line.match(/^\t\t([A-Za-z0-9_]+): ('.*'),?$/)
		if (!m) throw new Error(`${file}: err 段内无法解析的行: ${JSON.stringify(line)}`)
		// m[2]=带引号字面量;剥引号并反转义(与 toTsString 对偶;当前词表无 \/' 字符,兜底防未来漂移)
		entries.set(m[1], m[2].replace(/^'(.*)'$/, '$1').replace(/\\(['\\])/g, '$1'))
		i = lineEnd + 1
	}
}

/** 3. TS 单引号字符串字面量(值含 ' 或 \ 时转义;当前后端译文无此字符,兜底防未来漂移) */
function toTsString(v) {
	return `'${v.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`
}

/** 逐语言合并,返回统计 */
function mergeFor(lang, codes, langMap, tsText) {
	const file = FE_I18N_FILES[lang]
	const section = parseErrSection(tsText, file)
	const feKeys = new Set(codes.map(c => c.replaceAll('.', '_')))

	const before = section.entries.size
	const added = []
	const missingFallbacks = []
	const finalEntries = new Map()

	for (const code of [...codes].sort()) {
		const feKey = code.replaceAll('.', '_')
		if (section.entries.has(feKey)) {
			finalEntries.set(feKey, section.entries.get(feKey)) // 既有键:保留前端既有译文
			continue
		}
		const backend = langMap[code]
		if (backend !== undefined) {
			finalEntries.set(feKey, backend)
		} else {
			finalEntries.set(feKey, code) // 回落:域.语义 原文
			missingFallbacks.push(code)
		}
		added.push(feKey)
	}
	const orphans = [...section.entries.keys()].filter(k => !feKeys.has(k))

	const stats = { file: path.relative(repoRoot, file), before, after: finalEntries.size, added: added.length, orphans, missingFallbacks }

	if (checkOnly) {
		const ok = before === finalEntries.size && orphans.length === 0 && added.length === 0
		if (!ok) throw new Error(`${file}: 与目标状态不一致(应有 ${finalEntries.size} 键,实际 ${before};孤儿 ${orphans.length};待补 ${added.length})`)
		return stats
	}

	// 重建 err 段:全键字母序;段闭合无尾逗号(现文件 err 为末段;若未来移位,按实测闭合形态回写)
	const lines = [...finalEntries.entries()].map(([k, v]) => `\t\t${k}: ${toTsString(v)},`).join('\n')
	// 去掉最后一行的尾逗号,保持与既有段(末键无逗号)一致的书写形态
	const body = lines.replace(/,$/, '')
	const replacement = `\terr: {\n${body}\n\t${section.closeHasComma ? '},' : '}'}`
	const next = tsText.slice(0, section.start - `\terr: {\n`.length) + replacement + tsText.slice(section.end)
	// 保嵌入上下文:起点回退到 `\terr: {` 行首已含于上方 slice 计算 —— 直接以行首替换重算
	fs.writeFileSync(file, next, 'utf8')
	return stats
}

/* ── 主流程 ─────────────────────────────────────────── */
const csText = fs.readFileSync(CODES_CS, 'utf8')
const codes = extractCodes(csText)

const statsByLang = {}
for (const lang of Object.keys(FE_I18N_FILES)) {
	const langMap = JSON.parse(fs.readFileSync(LANG_FILES[lang], 'utf8'))
	const tsText = fs.readFileSync(FE_I18N_FILES[lang], 'utf8')
	statsByLang[lang] = mergeFor(lang, codes, langMap, tsText)
}

const summary = { errorCodesTotal: codes.length, ...statsByLang }
console.log(JSON.stringify(summary, null, 2))
