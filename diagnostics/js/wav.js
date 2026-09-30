/**
 * 把解码后的 PCM16 组装成规范的 16 kHz 单声道 WAV。
 *
 * dsh 语音链路只认「规范的 16 kHz 单声道 PCM16 WAV」（AGENTS.md §6.1），
 * ATVV 解码输出正好就是，所以这里是零转换，只补 44 字节头。
 */

/**
 * @param {Int16Array[]} chunks 按到达顺序排列的 PCM 分片
 * @param {number} sampleRate
 * @param {number} gain 线性增益。AGENTS.md §7 坑 7：RC003 解码后峰值约 -18 dBFS，需约 ×5
 * @returns {{ buffer: ArrayBuffer, stats: object }}
 */
export function buildWav(chunks, sampleRate = 16000, gain = 1) {
  let total = 0
  for (const c of chunks) total += c.length

  const dataBytes = total * 2
  const ab = new ArrayBuffer(44 + dataBytes)
  const v = new DataView(ab)

  ascii(v, 0, 'RIFF')
  v.setUint32(4, 36 + dataBytes, true)
  ascii(v, 8, 'WAVE')

  ascii(v, 12, 'fmt ')
  v.setUint32(16, 16, true)
  v.setUint16(20, 1, true)          // PCM
  v.setUint16(22, 1, true)          // mono
  v.setUint32(24, sampleRate, true)
  v.setUint32(28, sampleRate * 2, true) // byteRate = rate * channels * bytesPerSample
  v.setUint16(32, 2, true)          // blockAlign
  v.setUint16(34, 16, true)         // bitsPerSample

  ascii(v, 36, 'data')
  v.setUint32(40, dataBytes, true)

  let off = 44
  let clipped = 0
  let peak = 0
  let sumSq = 0

  for (const c of chunks) {
    for (let i = 0; i < c.length; i++) {
      let s = c[i] * gain
      if (s > 32767) {
        s = 32767
        clipped++
      } else if (s < -32768) {
        s = -32768
        clipped++
      }
      v.setInt16(off, s, true)
      off += 2
      const a = s < 0 ? -s : s
      if (a > peak) peak = a
      sumSq += s * s
    }
  }

  const rms = total ? Math.sqrt(sumSq / total) : 0

  return {
    buffer: ab,
    stats: {
      samples: total,
      dataBytes,
      seconds: sampleRate ? total / sampleRate : 0,
      peakDbfs: dbfs(peak),
      rmsDbfs: dbfs(rms),
      clipped,
      gain,
    },
  }
}

function dbfs(x) {
  return x > 0 ? 20 * Math.log10(x / 32768) : -Infinity
}

function ascii(view, offset, text) {
  for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i))
}
