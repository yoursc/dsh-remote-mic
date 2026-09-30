/**
 * ATVV 会话状态机。
 *
 * 不碰蓝牙 API —— 只依赖注入的 AtvvTransport。
 *
 * 三条固件怪癖（AGENTS.md §4.6）在这里处理：
 *   1. 固件会跳过 0x08 直接发 0x04，不能把 MicOpenRequested 当必需的中间态
 *   2. 0x00 StreamStopped 会重复到达，必须按 session_id 去重
 *   3. 免费音频窗口约 5.7 秒，必须每 2.5 秒 MicExtend 续期
 */

import { AdpcmDecoder } from './adpcm.js'
import {
  CMD_GET_CAPS,
  micOpen,
  micClose,
  micExtend,
  CTRL,
  CTRL_NAME,
  EXTEND_INTERVAL_MS,
  DEFAULT_FRAME_SIZE,
} from './atvv-consts.js'

export class AtvvSession {
  /**
   * @param {import('./transport.js').AtvvTransport} transport
   * @param {Record<string, (payload?: any) => void>} handlers
   * @param {{ extendIntervalMs?: number }} [options] 注入间隔仅为测试便利，生产用默认值
   */
  constructor(transport, handlers = {}, options = {}) {
    this.transport = transport
    this.h = handlers
    this.extendIntervalMs = options.extendIntervalMs ?? EXTEND_INTERVAL_MS
    this.decoder = new AdpcmDecoder()
    this.state = 'idle'
    this.sessionId = null
    this.caps = null
    this.frameSize = DEFAULT_FRAME_SIZE
    this._extendTimer = null
    this._stopped = new Set()
  }

  get isStreaming() {
    return this.state === 'streaming'
  }

  /**
   * 握手拆成三个独立步骤，每步单独成败。
   *
   * 不合并的理由：GATT 连接成功 ≠ 特征可访问。真机上会出现
   * 「连得上、能枚举特征、但订阅时抛 Authentication failed」——
   * 那是 BLE 链路加密级别不足（ATT 0x05），与 ATVV 协议无关。
   * 合并成一步会把这个关键区别掩盖掉。
   */
  async handshake() {
    // 同步回调（如 _onAudio）与异步回调（如 _onControl）都要能兜住异常
    const swallow = (maybePromise) =>
      Promise.resolve(maybePromise).catch((e) => this._emit('warn', `事件处理异常：${e.message}`))

    const steps = []
    const run = async (name, fn) => {
      try {
        await fn()
        steps.push({ name, ok: true })
      } catch (e) {
        steps.push({ name, ok: false, error: `${e.name || 'Error'}: ${e.message}` })
      }
    }

    // 先订阅再握手，避免「预按住键时软件未就绪」漏掉起始音频（§7 坑 6）
    await run('订阅 CONTROL (0004)', () =>
      this.transport.subscribeControl((b) => swallow(this._onControl(b))),
    )
    await run('订阅 AUDIO (0003)', () =>
      this.transport.subscribeAudio((b) => swallow(this._onAudio(b))),
    )
    await run('写 GetCaps', () => this.transport.write(CMD_GET_CAPS))

    this._emit('handshake', { steps })
    this._emit('state', { state: this.state, reason: 'handshake done' })
    return steps
  }

  /** 清空本次录音缓冲，保留会话与订阅 */
  async reset() {
    this._cancelExtend()
    this.state = 'idle'
    this.sessionId = null
    this._stopped.clear()
    this.decoder.reset(0, 0)
    this.h.onReset?.()
    this._emit('state', { state: this.state, reason: 'reset' })
  }

  async _onControl(bytes) {
    const t = Date.now()
    const opcode = bytes[0]
    this._emit('control', { t, opcode, name: CTRL_NAME[opcode] ?? `未知 0x${opcode.toString(16)}`, bytes })

    switch (opcode) {
      case CTRL.CAPS:
        this.caps = parseCaps(bytes)
        this.frameSize = this.caps.frameSize || DEFAULT_FRAME_SIZE
        this._emit('caps', this.caps)
        break

      case CTRL.DECODER_SYNC: {
        // predictor = i16 BE @[4..6]，step_index @[6]
        const predictor = int16BE(bytes, 4)
        const stepIndex = bytes.length > 6 ? bytes[6] : 0
        this.decoder.reset(predictor, stepIndex)
        this._emit('decoderSync', { predictor, stepIndex })
        break
      }

      case CTRL.MIC_OPEN_REQUESTED:
        // 固件正常路径：先请求开麦，主机回应 MicOpen
        await this.transport.write(micOpen())
        break

      case CTRL.STREAM_STARTED:
        this._beginStream(t, bytes)
        break

      case CTRL.STREAM_STOPPED:
        await this._endStream(t, bytes)
        break

      default:
        break
    }
  }

  _beginStream(t, bytes) {
    this.state = 'streaming'
    // session id 的具体偏移未在上游资料中明确归档，先按 bytes[1] 取，
    // 并在 UI 里同时展示原始 hex，便于真机校准。
    this.sessionId = bytes.length > 1 ? bytes[1] : null
    this._stopped.delete(this.sessionId)

    this._emit('streamStart', { t, sessionId: this.sessionId, bytes })
    this._emit('state', { state: this.state, reason: 'StreamStarted' })

    this._cancelExtend()
    this._extendTimer = setInterval(() => {
      this.transport.write(micExtend(this.sessionId)).catch((e) => {
        this._emit('warn', `MicExtend 失败：${e.message}`)
      })
    }, this.extendIntervalMs)
  }

  async _endStream(t, bytes) {
    const sid = bytes.length > 1 ? bytes[1] : this.sessionId

    // Stop 事件会重复到达，必须去重，否则乒乓
    if (sid != null && this._stopped.has(sid)) return
    if (sid != null) this._stopped.add(sid)

    this._cancelExtend()
    this.state = 'idle'
    this._emit('streamStop', { t, sessionId: sid, bytes })
    this._emit('state', { state: this.state, reason: 'StreamStopped' })

    try {
      await this.transport.write(micClose(sid))
    } catch (e) {
      this._emit('warn', `MicClose 失败：${e.message}`)
    }
  }

  _onAudio(bytes) {
    const t = Date.now()

    if (!this.isStreaming) {
      // 固件在 StreamStarted 之前就吐音频 = 起始段丢失的典型征兆（§7 坑 6）
      this._emit('warn', `收到音频帧但状态为 ${this.state}，可能存在预按住导致的起始丢帧`)
    }

    const pcm = this.decoder.decodeFrame(bytes)
    this._emit('audio', { t, bytes, pcm })
  }

  _cancelExtend() {
    if (this._extendTimer != null) {
      clearInterval(this._extendTimer)
      this._extendTimer = null
    }
  }

  _emit(name, payload) {
    const fn = this.h[`on${name[0].toUpperCase()}${name.slice(1)}`]
    fn?.(payload)
  }
}

/**
 * Caps 解析。AGENTS.md §4.4 说明 RC003 有固件怪癖：
 * version 报 0x0100 但 codecs = 0，此时 codec 信息藏在 bytes[4] 的低两位。
 *
 * 字节布局在真机上仍需校准 —— 因此原始 hex 一并返回，UI 会显示出来。
 */
function parseCaps(b) {
  const version = b.length > 2 ? (b[1] << 8) | b[2] : 0
  let codecs = b.length > 3 ? b[3] : 0
  if (version >= 0x0100 && codecs === 0 && b.length > 4) {
    codecs = b[4] & 0x03
  }
  const frameSize = b.length > 6 ? ((b[5] << 8) | b[6]) || DEFAULT_FRAME_SIZE : DEFAULT_FRAME_SIZE

  return {
    version,
    versionHex: `0x${version.toString(16).padStart(4, '0')}`,
    codecs,
    frameSize,
    supports16k: (codecs & 0x02) !== 0,
    raw: hex(b),
  }
}

function int16BE(b, offset) {
  if (b.length < offset + 2) return 0
  const v = (b[offset] << 8) | b[offset + 1]
  return v > 0x7fff ? v - 0x10000 : v
}

function hex(bytes) {
  return Array.from(bytes)
    .map((x) => x.toString(16).padStart(2, '0'))
    .join(' ')
}
