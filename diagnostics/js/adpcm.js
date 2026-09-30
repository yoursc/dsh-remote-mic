/**
 * IMA ADPCM (DVI4) 解码器。
 *
 * ATVV 音频帧规格（AGENTS.md §4.5）：
 *   - 16 kHz / 单声道 / 4-bit IMA ADPCM
 *   - 每帧 120 字节裸 payload → 240 samples（无 per-frame header）
 *   - 每字节内 **高 4 bit 先**
 *   - 解码状态 predictor / step_index **跨帧连续**，初始值由 CONTROL 的 0x0a DecoderSync 提供
 *
 * 最后一条是与普通 WAV 里的 ADPCM 最大的差别，也是最容易被写错的地方：
 * 一旦每帧重置状态，声音会变成连续爆音。
 */

const STEP_TABLE = [
  7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
  19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
  50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
  130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
  337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
  876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
  2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
  5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
  15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
]

const INDEX_TABLE = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8]

/** 返回一个 nibble 解码后的 [样本值, 新的 step index] */
function decodeNibble(code, predictor, stepIndex) {
  const step = STEP_TABLE[stepIndex]
  let diff = step >> 3
  if (code & 4) diff += step
  if (code & 2) diff += step >> 1
  if (code & 1) diff += step >> 2

  let sample = code & 8 ? predictor - diff : predictor + diff
  if (sample > 32767) sample = 32767
  else if (sample < -32768) sample = -32768

  let si = stepIndex + INDEX_TABLE[code]
  if (si < 0) si = 0
  else if (si > 88) si = 88

  return [sample, si]
}

export class AdpcmDecoder {
  constructor() {
    this.resetCount = 0
    this.frames = 0
    this.samples = 0
    this.reset(0, 0)
  }

  /** 重置解码状态。 Typically 由 DecoderSync 事件驱动。 */
  reset(predictor = 0, stepIndex = 0) {
    this.predictor = predictor > 32767 ? 32767 : predictor < -32768 ? -32768 : predictor
    this.stepIndex = stepIndex < 0 ? 0 : stepIndex > 88 ? 88 : stepIndex
    this.resetCount++
  }

  /** 解码一帧裸 payload，返回 Int16Array（长度 = bytes.length * 2） */
  decodeFrame(bytes) {
    const out = new Int16Array(bytes.length * 2)
    let predictor = this.predictor
    let stepIndex = this.stepIndex
    let o = 0

    for (let i = 0; i < bytes.length; i++) {
      const b = bytes[i]
      let r = decodeNibble((b >> 4) & 0x0f, predictor, stepIndex)
      predictor = r[0]
      stepIndex = r[1]
      out[o++] = predictor

      r = decodeNibble(b & 0x0f, predictor, stepIndex)
      predictor = r[0]
      stepIndex = r[1]
      out[o++] = predictor
    }

    // 状态留给下一帧
    this.predictor = predictor
    this.stepIndex = stepIndex
    this.frames++
    this.samples += out.length
    return out
  }
}

export { STEP_TABLE, INDEX_TABLE, decodeNibble }
