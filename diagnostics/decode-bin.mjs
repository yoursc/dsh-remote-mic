/**
 * 把 probe_ble.py audio 录下的原始 ADPCM 帧解码成可播放的 WAV。
 *
 * 用法：
 *   node decode-bin.mjs [bin文件路径]
 * 不传路径时，自动在 diagnostics/ 与仓库根目录下找最新的 audio-*.bin。
 *
 * bin 格式：重复的 [2字节 little-endian 长度][payload]
 * 解码用的是 js/adpcm.js 与 js/wav.js —— 与浏览器插件完全同一份代码，
 * 所以这里听到的声音就是将来 dsh 里会得到的声音。
 */

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs'
import { join, dirname, basename } from 'node:path'
import { AdpcmDecoder } from './js/adpcm.js'
import { buildWav } from './js/wav.js'

const HERE = new URL('.', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')
const ROOT = new URL('../', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')
// probe_ble.py 跑在哪个目录，audio-*.bin 就落在哪个目录
const SEARCH_DIRS = [HERE, ROOT]

function pickBin() {
  const all = []
  for (const dir of SEARCH_DIRS) {
    let names = []
    try { names = readdirSync(dir) } catch { continue }
    for (const f of names) {
      if (f.startsWith('audio-') && f.endsWith('.bin')) {
        const p = join(dir, f)
        all.push({ p, t: statSync(p).mtimeMs })
      }
    }
  }
  all.sort((a, b) => b.t - a.t)
  return all.length ? all[0].p : null
}

function parseFrames(buf) {
  const frames = []
  let off = 0
  while (off + 2 <= buf.length) {
    const len = buf.readUInt16LE(off)
    off += 2
    if (len === 0 || off + len > buf.length) break
    frames.push(new Uint8Array(buf.subarray(off, off + len)))
    off += len
  }
  return frames
}

const binPath = process.argv[2] || pickBin()
if (!binPath) {
  console.error('没找到 audio-*.bin。先跑 probe_ble.py audio <MAC>。')
  process.exit(1)
}

const buf = readFileSync(binPath)
const frames = parseFrames(buf)
if (!frames.length) {
  console.error('解析出 0 帧，文件格式不对。')
  process.exit(1)
}

const sizes = {}
for (const f of frames) sizes[f.length] = (sizes[f.length] || 0) + 1

// 解码：状态跨帧连续（这是 ATVV 与普通 ADPCM 的关键差别）
const dec = new AdpcmDecoder()
const chunks = frames.map((f) => dec.decodeFrame(f))

// 增益要按 RMS 定，不能按峰值定。
// 实测这批录音里有极少数瞬态尖峰（触顶样本仅 0.004%），若按峰值算增益会被它们
// 拉低到 ×0.89 —— 反而更小声。AGENTS.md §7 坑 7 给的经验值就是 ×5，本脚本用
// RMS 归一到 -18 dBFS 来得到同样的量级，上限 ×10 兜底。
const raw = buildWav(chunks, 16000, 1)
const rmsLinear = 32768 * Math.pow(10, raw.stats.rmsDbfs / 20)
const targetRms = 32768 * Math.pow(10, -18 / 20)
let autoGain = rmsLinear > 1 ? Math.min(10, targetRms / rmsLinear) : 1
if (autoGain < 1) autoGain = 1
const final = autoGain > 1.02 ? buildWav(chunks, 16000, autoGain) : raw

// 输出写在输入 bin 旁边，不固定到某个目录
const outDir = dirname(binPath)
const outPath = join(outDir, basename(binPath, '.bin') + '.wav')
writeFileSync(outPath, Buffer.from(final.buffer))
// 固定名字，方便直接拿"最新一次录音"去做 ASR 验证，不用每次翻时间戳
writeFileSync(join(outDir, 'latest.wav'), Buffer.from(final.buffer))

// 粗判有没有内容：按 100ms 分窗看能量
const all = new Int16Array(chunks.reduce((n, c) => n + c.length, 0))
let o = 0
for (const c of chunks) {
  all.set(c, o)
  o += c.length
}
const win = 1600
let active = 0
let windows = 0
for (let i = 0; i + win <= all.length; i += win) {
  let s = 0
  for (let j = i; j < i + win; j++) s += all[j] * all[j]
  const rms = Math.sqrt(s / win)
  windows++
  if (rms > 150) active++
}

const pct = (n, d) => (d ? ((n / d) * 100).toFixed(1) : '0.0')
console.log('='.repeat(66))
console.log(`源文件      ${basename(binPath)}`)
console.log(`帧数        ${frames.length}   帧大小分布 ${JSON.stringify(sizes)}`)
console.log(`解码样本    ${all.length}  → ${(all.length / 16000).toFixed(2)} 秒`)
console.log('-'.repeat(66))
console.log(`原始峰值    ${raw.stats.peakDbfs.toFixed(1)} dBFS   RMS ${raw.stats.rmsDbfs.toFixed(1)} dBFS`)
console.log(`自动增益    ×${autoGain.toFixed(2)}  （按 RMS 归一到 -18 dBFS，上限 ×10）`)
console.log(`输出峰值    ${final.stats.peakDbfs.toFixed(1)} dBFS   RMS ${final.stats.rmsDbfs.toFixed(1)} dBFS`)
console.log(`削波样本    ${final.stats.clipped}  (${pct(final.stats.clipped, all.length)}%)`)
console.log('-'.repeat(66))
console.log(`有声窗口    ${active}/${windows} 窗 (${pct(active, windows)}%)  ← 低于 20% 说明基本是静音`)
console.log('='.repeat(66))
console.log(`已写出      ${outPath}`)
