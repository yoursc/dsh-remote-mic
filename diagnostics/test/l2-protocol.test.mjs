/**
 * L2 · 协议一致性夹具（TS 侧） —— PROTOCOL.md §14。
 *
 * 与 local-mic 的 --selftest 跑**同一份** conformance/protocol-vectors.txt，
 * 但只挑 L→C 方向：客户端如何处置 local-mic 发来的消息，是客户端自己的责任。
 * （C→L 与 BUILD 由 C# 侧覆盖，见 Protocol.cs —— 两端各跑各的。）
 *
 * 为什么值得单独写：只测一端等于另一端完全没人守。诊断页落后整整一个 proto
 * 却无人察觉，就是这么来的 —— 直到联调才发现把 PCM 当 ADPCM 解。
 *
 * 跑：node diagnostics/test/l2-protocol.test.mjs
 */

import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { classifyMessage, decodeAudioFrame, PROTO } from '../js/transport.js'

const here = dirname(fileURLToPath(import.meta.url))
const vectorsPath = join(here, '..', '..', 'conformance', 'protocol-vectors.txt')

/** 夹具里的 `hex:02 <480 bytes>` —— 造一整帧（kind 字节 + n 字节载荷）。 */
function makeFrame(kind, n) {
  const buf = new Uint8Array(1 + n)
  buf[0] = kind
  for (let i = 0; i < n; i++) buf[1 + i] = i & 0xff
  return buf
}

/** 把一条 L→C 夹具的实际处置结果，翻译成夹具里写的期望值词汇。 */
function verdictOf(spec) {
  if (spec.startsWith('hex:')) {
    const m = /^hex:([0-9a-f]{2})\s*<(\d+)\s*bytes>$/i.exec(spec)
    assert.ok(m, `夹具写法不认识：${spec}`)
    const kind = parseInt(m[1], 16)
    const n = parseInt(m[2], 10)

    if (kind !== 0x02) return '忽略' // proto 1 未定义的 kind：忽略 + 告警

    const pcm = decodeAudioFrame(makeFrame(kind, n))
    if (n === 0 && pcm === null) return '拒绝：空帧'
    if (pcm === null) return '拒绝：长度非偶数'
    return `样本:${pcm.length}`
  }

  const obj = JSON.parse(spec)
  const v = classifyMessage(obj)

  if (v.kind === 'reject') return `拒绝：${v.code}`
  if (v.kind === 'ignore') return '忽略'
  if (obj.op === 'capture' && obj.phase === 'end') {
    return v.transcribe ? '接受：送转写' : '接受：不送转写'
  }
  return '接受'
}

const lines = readFileSync(vectorsPath, 'utf8').split('\n')

let total = 0
let failed = 0

for (const raw of lines) {
  const line = raw.trim()
  if (!line || line.startsWith('#')) continue

  const arrow = line.indexOf('=>')
  if (arrow < 0) continue

  const left = line.slice(0, arrow).trim()
  const expect = line.slice(arrow + 2).trim()

  // 只跑自己这一端
  if (!left.startsWith('L→C')) continue

  const spec = left.slice(3).trim()
  total++

  let actual
  try {
    actual = verdictOf(spec)
  } catch (e) {
    actual = `抛出异常：${e.message}`
  }

  if (actual === expect) {
    console.log(`  OK    ${left.slice(0, 62).padEnd(64)} ${expect}`)
  } else {
    failed++
    console.log(`  失败  ${left.slice(0, 62).padEnd(64)} 期望 ${expect} 实际 ${actual}`)
  }
}

console.log()
if (failed === 0) {
  console.log(`协议夹具（TS 侧）通过：${total}/${total} 条与 PROTOCOL.md §14 一致（proto ${PROTO}）`)
} else {
  console.log(`协议夹具（TS 侧）失败：${failed}/${total} 条不通过`)
  process.exit(1)
}
