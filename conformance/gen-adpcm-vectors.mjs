/**
 * 生成 ADPCM 黄金测试向量 —— local-mic(C#) 与 diagnostics(Node) 共用的单一数据源。
 *
 * 为什么要这份文件：
 *   local-mic 为了能在自检窗口里播放录音，必须自带一份 ADPCM 解码器。于是同一个算法
 *   存在两份实现（diagnostics/js/adpcm.js 与 local-mic/src/Adpcm.cs）。两份实现各写各的测试，就会悄悄漂移。
 *   这里把 Node 侧（已被 L0 的 12 项测试覆盖）产出的真值固化下来，C# 侧自检逐字节比对，
 *   只要有一边改错立刻红。
 *
 * 跑法：
 *   node conformance/gen-adpcm-vectors.mjs
 */

import { writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { AdpcmDecoder } from '../diagnostics/js/adpcm.js'

const here = dirname(fileURLToPath(import.meta.url))

/** 固定模式的伪随机，保证每次生成完全一致 */
function pseudoBytes(n, seed) {
  let s = seed
  const out = new Uint8Array(n)
  for (let i = 0; i < n; i++) {
    s = (s * 1103515245 + 12345) & 0x7fffffff
    out[i] = (s >>> 16) & 0xff
  }
  return out
}

const cases = [
  // 手算锚点：这两个值在 L0 里也被独立断言，是最可靠的交叉验证点
  { note: '手算锚点 高nibble=0 → 0，低nibble=7 → 11', bytes: new Uint8Array([0x07]) },
  { note: '手算锚点 高nibble=7 → 11，索引推进后再解 0 → 13', bytes: new Uint8Array([0x70]) },
  { note: 'code 全 0 仍有 step>>3 项', bytes: new Uint8Array([0x00]) },
  { note: '最大步进', bytes: new Uint8Array([0x77]) },
  { note: '连续 8 个 nibble 同向', bytes: new Uint8Array([0x77, 0x77, 0x77, 0x77]) },
  { note: '负向步进', bytes: new Uint8Array([0x08, 0x08, 0x08]) },
  { note: '整帧 120 字节全 0（= 240 样本）', bytes: new Uint8Array(120) },
  { note: '整帧 120 字节全 0xFF', bytes: new Uint8Array(120).fill(0xff) },
  { note: '伪随机 60 字节', bytes: pseudoBytes(60, 20260929) },
  { note: '非零起始状态 stepIndex=20', state: { p: 0, i: 20 }, bytes: new Uint8Array([0x00, 0x00, 0x00, 0x00]) },
  { note: '非零起始状态 predictor=-1234 index=42', state: { p: -1234, i: 42 }, bytes: new Uint8Array([0x00, 0x77]) },
  { note: '越界入参应被钳位后再解', state: { p: 99999, i: -5 }, bytes: new Uint8Array([0x7f, 0x00]) },
]

const hex = (u8) => Array.from(u8, (b) => b.toString(16).padStart(2, '0')).join('')

const lines = [
  '# ADPCM 黄金测试向量（单一数据源）',
  '#',
  '# 由 conformance/gen-adpcm-vectors.mjs 用 Node 侧 js/adpcm.js 生成。',
  '# Node 侧 test/l0-adpcm.test.mjs 与 C# 侧 --selftest 读同一份，防止两套实现漂移。',
  '#',
  '# 格式： [p=<predictor>,i=<stepIndex>|] <hex bytes> -> <s0>,<s1>,...',
  '# 方括号内可省略，省略表示 predictor=0, stepIndex=0。',
  '# 注意：IMA ADPCM 帧间状态必须连续传递，这是 ATVV 与普通 ADPCM 最大的差别。',
  '',
]

for (const c of cases) {
  const d = new AdpcmDecoder()
  if (c.state) d.reset(c.state.p, c.state.i)
  const samples = d.decodeFrame(c.bytes)
  const prefix = c.state ? `p=${c.state.p},i=${c.state.i}|` : ''
  lines.push(`# ${c.note}`)
  lines.push(`${prefix}${hex(c.bytes)} -> ${Array.from(samples).join(',')}`)
  lines.push('')
}

const outPath = join(here, 'adpcm-vectors.txt')
writeFileSync(outPath, lines.join('\n'))
console.log(`已写出 ${cases.length} 条向量 → ${outPath}`)
for (const c of cases) {
  const d = new AdpcmDecoder()
  if (c.state) d.reset(c.state.p, c.state.i)
  const s = d.decodeFrame(c.bytes)
  console.log(`  ${hex(c.bytes).slice(0, 24).padEnd(26)} → 首 6 样本 ${Array.from(s.slice(0, 6)).join(',')}`)
}
