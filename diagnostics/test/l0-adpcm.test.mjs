/**
 * L0 · 解码算法测试（不需要任何硬件）
 *
 * 跑法：
 *   node diagnostics/test/l0-adpcm.test.mjs
 *
 * 覆盖 AGENTS.md §4.5 与 §7 坑 7 里最容易写错的三点：
 *   1. 帧间 predictor / step_index 必须连续（这是 ATVV 与普通 ADPCM 最大的差别）
 *   2. nibble 必须高 4 bit 先
 *   3. 0x0a DecoderSync 必须能重置解码状态
 */

import assert from 'node:assert/strict'
import { AdpcmDecoder, STEP_TABLE, INDEX_TABLE, decodeNibble } from '../js/adpcm.js'
import { buildWav } from '../js/wav.js'

let passed = 0
let failed = 0

function test(name, fn) {
  try {
    fn()
    passed++
    console.log(`  ✓ ${name}`)
  } catch (e) {
    failed++
    console.error(`  ✗ ${name}`)
    console.error(`    ${e.message}`)
  }
}

/* ---------- 参考编码器：必须与解码器完全对称 ---------- */

function encodeNibble(sample, predictor, stepIndex) {
  const step = STEP_TABLE[stepIndex]
  let diff = sample - predictor
  let code = 0
  if (diff < 0) {
    code = 8
    diff = -diff
  }
  if (diff >= step) {
    code |= 4
    diff -= step
  }
  if (diff >= step >> 1) {
    code |= 2
    diff -= step >> 1
  }
  if (diff >= step >> 2) {
    code |= 1
  }
  // 编码器用解码后的重建值推进状态，保证两端同步
  const [recovered, nextIndex] = decodeNibble(code, predictor, stepIndex)
  return [code, recovered, nextIndex]
}

/** @param {Int16Array} pcm @returns {Uint8Array} 高 4 bit 先的 ADPCM 流 */
function encode(pcm) {
  const out = new Uint8Array(Math.ceil(pcm.length / 2))
  let predictor = 0
  let stepIndex = 0
  let o = 0
  for (let i = 0; i < pcm.length; i += 2) {
    const hi = encodeNibble(pcm[i], predictor, stepIndex)
    predictor = hi[1]
    stepIndex = hi[2]
    const lo = i + 1 < pcm.length
      ? encodeNibble(pcm[i + 1], predictor, stepIndex)
      : [0, predictor, stepIndex]
    predictor = lo[1]
    stepIndex = lo[2]
    out[o++] = ((hi[0] & 0x0f) << 4) | (lo[0] & 0x0f)
  }
  return out
}

function sine(freq = 440, seconds = 0.25, rate = 16000, amp = 0.6) {
  const n = Math.floor(rate * seconds)
  const out = new Int16Array(n)
  for (let i = 0; i < n; i++) {
    out[i] = Math.round(Math.sin((2 * Math.PI * freq * i) / rate) * 32767 * amp)
  }
  return out
}

function snrDb(original, decoded, skip = 32) {
  let sigSq = 0
  let errSq = 0
  for (let i = skip; i < original.length; i++) {
    sigSq += original[i] * original[i]
    const d = original[i] - decoded[i]
    errSq += d * d
  }
  return 10 * Math.log10(sigSq / errSq)
}

console.log('\nL0 · ADPCM 解码')

test('表结构完整（89 级 step，16 级 index）', () => {
  assert.equal(STEP_TABLE.length, 89)
  assert.equal(INDEX_TABLE.length, 16)
  assert.equal(STEP_TABLE[0], 7)
  assert.equal(STEP_TABLE[88], 32767)
})

test('解码正弦波后信噪比 > 20 dB', () => {
  const pcm = sine()
  const enc = encode(pcm)
  const dec = new AdpcmDecoder()
  let got = new Int16Array(pcm.length)
  const a = dec.decodeFrame(enc.subarray(0, 120))
  // 拼完整段
  const chunks = []
  let predictor = 0
  let stepIndex = 0
  // 复用同一个 decoder 连续解码整段
  const d2 = new AdpcmDecoder()
  let off = 0
  while (off < enc.length) {
    const slice = enc.subarray(off, Math.min(off + 120, enc.length))
    chunks.push(d2.decodeFrame(slice))
    off += slice.length
  }
  const total = chunks.reduce((s, c) => s + c.length, 0)
  got = new Int16Array(total)
  let p = 0
  for (const c of chunks) {
    got.set(c, p)
    p += c.length
  }
  const db = snrDb(pcm, got)
  assert.ok(db > 20, `信噪比只有 ${db.toFixed(1)} dB`)
})

test('★ 帧边界：一次解完 与 分 120 字节帧解完，结果必须逐字节相同', () => {
  const pcm = sine(300, 0.5)
  const enc = encode(pcm)

  const whole = new AdpcmDecoder()
  const a = whole.decodeFrame(enc)

  const framed = new AdpcmDecoder()
  const parts = []
  let off = 0
  while (off < enc.length) {
    const slice = enc.subarray(off, Math.min(off + 120, enc.length))
    parts.push(framed.decodeFrame(slice))
    off += slice.length
  }
  const b = new Int16Array(a.length)
  let p = 0
  for (const c of parts) {
    b.set(c.subarray(0, Math.min(c.length, b.length - p)), p)
    p += c.length
  }

  assert.deepStrictEqual(Array.from(a), Array.from(b))
})

test('★ nibble 顺序：高 4 bit 对应更早的样本（手算黄金值）', () => {
  // 单个起始状态 predictor=0 / stepIndex=0，step[0]=7
  // 0x07 → 高 nibble=0：diff = 7>>3 = 0 → 样本 0；低 nibble=7：diff = 0+7+3+1 = 11 → 样本 11
  // 0x70 → 高 nibble=7：diff = 11 → 样本 11；索引推进到 8（step=16）
  //        低 nibble=0：diff = 16>>3 = 2 → 样本 13
  const lo = new AdpcmDecoder().decodeFrame(new Uint8Array([0x07]))
  const hi = new AdpcmDecoder().decodeFrame(new Uint8Array([0x70]))

  assert.deepStrictEqual(Array.from(lo), [0, 11])
  assert.deepStrictEqual(Array.from(hi), [11, 13])
  assert.ok(hi[0] > lo[0], '交换两个 nibble 的位置必须改变第一个样本')
})

test('code=0 不代表零差值（IMA 恒含 step>>3 项）', () => {
  // 这是个容易被误判的点：即使 code 全 0，仍有 step>>3 的差值。
  // 上一条用例里第二个样本 13 ≠ 11 正是这个原因。
  const [sample, nextIndex] = decodeNibble(0, 0, 0)
  assert.equal(nextIndex, 0, '索引已在底部，钳位后仍为 0')
  const d = new AdpcmDecoder()
  d.reset(0, 20)
  const out = d.decodeFrame(new Uint8Array([0x00, 0x00, 0x00, 0x00]))
  let monotonic = true
  for (let i = 1; i < out.length; i++) if (out[i] < out[i - 1]) monotonic = false
  assert.equal(monotonic, true, '连续 0 code 应产生持续上升的重建值')
  assert.equal(sample, 0)
})

test('★ DecoderSync 能重置 predictor / stepIndex', () => {
  const d = new AdpcmDecoder()
  d.decodeFrame(new Uint8Array([0x77, 0x77, 0x77]))
  const before = { p: d.predictor, s: d.stepIndex }
  assert.ok(before.p !== 0 || before.s !== 0, '解码若干帧后状态应已推进')

  d.reset(-1234, 42)
  assert.equal(d.predictor, -1234)
  assert.equal(d.stepIndex, 42)
  assert.equal(d.resetCount, 2, 'reset 次数应累加')
})

test('reset 会对越界入参做钳位', () => {
  const d = new AdpcmDecoder()
  d.reset(99999, -5)
  assert.equal(d.predictor, 32767)
  assert.equal(d.stepIndex, 0)
  d.reset(-99999, 999)
  assert.equal(d.predictor, -32768)
  assert.equal(d.stepIndex, 88)
})

test('一帧 120 字节恰好产出 240 样本（= 15 ms @ 16 kHz）', () => {
  const d = new AdpcmDecoder()
  const out = d.decodeFrame(new Uint8Array(120))
  assert.equal(out.length, 240)
  assert.equal(240 / 16000, 0.015)
})

console.log('\nL0 · WAV 组装')

test('WAV 头为规范的 16 kHz 单声道 PCM16', () => {
  const chunks = [new Int16Array([1, 2, 3, 4])]
  const { buffer, stats } = buildWav(chunks, 16000, 1)
  const v = new DataView(buffer)
  const ascii = (o, n) => String.fromCharCode(...Array.from({ length: n }, (_, i) => v.getUint8(o + i)))

  assert.equal(ascii(0, 4), 'RIFF')
  assert.equal(ascii(8, 4), 'WAVE')
  assert.equal(ascii(12, 4), 'fmt ')
  assert.equal(v.getUint32(16, true), 16, 'fmt chunk 长度')
  assert.equal(v.getUint16(20, true), 1, 'format = PCM')
  assert.equal(v.getUint16(22, true), 1, '单声道')
  assert.equal(v.getUint32(24, true), 16000, '采样率')
  assert.equal(v.getUint32(28, true), 32000, 'byteRate')
  assert.equal(v.getUint16(32, true), 2, 'blockAlign')
  assert.equal(v.getUint16(34, true), 16, '位深')
  assert.equal(ascii(36, 4), 'data')
  assert.equal(v.getUint32(40, true), 8, 'data 长度')
  assert.equal(buffer.byteLength, 44 + 8)
  assert.equal(stats.samples, 4)
})

test('★ 增益不会污染原始 PCM（改了增益还能重导出）', () => {
  const chunks = [new Int16Array([1000, -1000])]
  const r1 = buildWav(chunks, 16000, 1)
  const r2 = buildWav(chunks, 16000, 5)
  assert.deepStrictEqual(Array.from(chunks[0]), [1000, -1000], '源数据必须未被修改')

  const v1 = new DataView(r1.buffer)
  const v2 = new DataView(r2.buffer)
  assert.equal(v1.getInt16(44, true), 1000)
  assert.equal(v2.getInt16(44, true), 5000)
  assert.ok(r2.stats.peakDbfs > r1.stats.peakDbfs, '增益后峰值应更高')
})

test('坑 7：×5 增益把 -18 dBFS 拉到可用区间', () => {
  const amp = 32768 * 10 ** (-18 / 20)
  const chunks = [new Int16Array([Math.round(amp), Math.round(-amp)])]
  const { stats } = buildWav(chunks, 16000, 5)
  assert.ok(Math.abs(stats.peakDbfs - (-4)) < 1.5, `实际峰值 ${stats.peakDbfs.toFixed(1)} dBFS`)
})

test('削波会被计数', () => {
  const chunks = [new Int16Array([30000, -30000])]
  const { stats } = buildWav(chunks, 16000, 5)
  assert.equal(stats.clipped, 2)
})

console.log(`\n通过 ${passed} 项，失败 ${failed} 项\n`)
process.exit(failed ? 1 : 0)
