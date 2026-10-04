#!/usr/bin/env node
/**
 * 文档相对链接体检 —— 扫全仓 markdown，检查 `](相对路径.md)` 是否真的存在。
 *
 * 跑法：
 *   node scripts/check-doc-links.mjs           # 有问题时退出码 1
 *   node scripts/check-doc-links.mjs --all     # 连"已知但未修"也一并列出
 *
 * 为什么需要它：2026-10-02 做文档结构整理时，顺手扫出 2 条断链
 * （`local-mic/README.md` 多一层、`docs/…` 指向一个从未存在的 MIGRATION.md），
 * 两条都是"写的时候看着对、没人跑过"的典型。这类检查交给机器，别靠人眼。
 *
 * 约定：脚本本身不「修」任何东西，只报告。已知但按约定暂不修的，写进 KNOWN_BROKEN，
 *       并注明原因与日期 —— 一旦它不再是断链，脚本会反过来提醒你把这条删掉。
 */
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { dirname, join, normalize, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const SKIP_DIRS = new Set(['.git', 'node_modules', 'bin', 'obj', 'dist', '.pnpm-store'])

/**
 * 已知但**按约定暂不修**的断链：脚本不把它算作失败，只提示。
 * 修掉之后请删掉对应条目 —— 脚本会提醒。
 */
const KNOWN_BROKEN = [
  {
    file: 'local-mic/README.md',
    link: '../../docs/PROTOCOL.md',
    reason: '多写了一层（实际指向仓库外，应为 ../docs/PROTOCOL.md）；属 Windows 侧维护的文件，2026-10-02 已上报、未改',
  },
]

const args = process.argv.slice(2)
const showAll = args.includes('--all')

/** 递归收集 markdown 文件（跳过构建产物与依赖）。 */
function collectMarkdown(dir) {
  const out = []
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue
    const full = join(dir, entry)
    const st = statSync(full)
    if (st.isDirectory()) out.push(...collectMarkdown(full))
    else if (entry.toLowerCase().endsWith('.md')) out.push(full)
  }
  return out
}

/**
 * 取出一行里所有 markdown 链接目标。
 * 只认相对路径（`./` `../` 或无前缀的非 URL），忽略锚点、`mailto:` 与绝对 URL。
 */
function linkTargets(line) {
  const targets = []
  const re = /\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g
  let m
  while ((m = re.exec(line)) !== null) {
    let target = m[1]
    if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(target)) continue // http: mailto: …
    if (target.startsWith('#')) continue
    target = target.split('#')[0]
    if (target === '') continue
    targets.push(target)
  }
  return targets
}

const files = collectMarkdown(repoRoot).sort()
const broken = []
const known = []
const stale = []
let checked = 0

for (const file of files) {
  const relFile = relative(repoRoot, file)
  const lines = readFileSync(file, 'utf8').split('\n')
  for (const [i, line] of lines.entries()) {
    for (const target of linkTargets(line)) {
      checked += 1
      const resolved = normalize(join(dirname(file), target))
      const exists = existsSync(resolved)
      const knownEntry = KNOWN_BROKEN.find((k) => k.file === relFile && k.link === target)
      if (knownEntry) {
        if (exists) stale.push(knownEntry)
        else known.push({ ...knownEntry, line: i + 1 })
        continue
      }
      if (!exists) broken.push({ file: relFile, line: i + 1, target, resolved: relative(repoRoot, resolved) })
    }
  }
}

console.log(`扫描 ${files.length} 个 markdown 文件、${checked} 个相对链接。`)

if (known.length > 0 || showAll) {
  console.log(`\n⚠️  已知但按约定未修（${known.length}）：`)
  for (const k of known) console.log(`   ${k.file}:${k.line} → ${k.link}\n      ${k.reason}`)
}

if (stale.length > 0) {
  console.log(`\n🧹 KNOWN_BROKEN 里这几条其实已经好了，请从脚本里删掉：`)
  for (const s of stale) console.log(`   ${s.file} → ${s.link}`)
}

if (broken.length > 0) {
  console.log(`\n❌ 断链 ${broken.length} 条：`)
  for (const b of broken) console.log(`   ${b.file}:${b.line}\n      ${b.target}  →  ${b.resolved}`)
  process.exit(1)
}

console.log('\n✅ 无断链。')
