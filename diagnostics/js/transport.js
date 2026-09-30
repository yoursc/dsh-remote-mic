/**
 * local-mic 的 WebSocket 客户端 —— **proto 1**（PROTOCOL.md）。
 *
 * 这个文件只做一件事：把接缝上的字节翻译成消息与 PCM。
 * **刻意不包含**任何 ATVV、BLE、ADPCM 的东西 —— 那些全部在 local-mic 里（原则 1）。
 *
 * 曾经这里还有两个实现（WebBluetoothTransport 直连遥控器、ReplayTransport 回放），
 * 2026-09-30 删除：路径 A 早已判死（AGENTS.md §2.6），留下的只是"看起来还能用"的
 * 死代码 —— 它会让人误以为音频有第二条来路，而实际上只有 local-mic 一条。
 * 需要它们的历史版本，看 git。
 */

/** 本客户端实现的协议版本（PROTOCOL.md §6）。必须与 local-mic 的 ProtoVersion 一致。 */
export const PROTO = 1

/** 二进制帧的 kind（§10）。proto 1 只定义这一个。 */
export const AUDIO_KIND = 0x02

/** 已知的 capture reason（§8.2）。未知 reason 一律**不送转写** —— 宁丢字勿错注入。 */
const REASONS_SEND = new Set(['released', 'disconnected', 'aborted'])

/**
 * 判定 local-mic 发来的一条消息该如何处置。
 *
 * 抽成**纯函数**是为了让 §14 的 TS 侧夹具跑的真是生产逻辑 ——
 * 若判定埋在 onmessage 回调里，就只能靠联调发现错误，那和没有夹具一样。
 *（C# 侧的 Protocol.Handshake 是同一个决定，两边对称。）
 *
 * @returns {{kind: 'accept'|'ignore'|'reject', code?: string, transcribe?: boolean}}
 */
export function classifyMessage(obj) {
  if (!obj || typeof obj !== 'object' || typeof obj.op !== 'string') {
    return { kind: 'reject', code: 'malformed' }
  }

  switch (obj.op) {
    case 'ready':
      // proto 缺失或类型不对都视为 0 ⇒ 明确拒绝（§6：显式拒绝优于静默错解）
      if (typeof obj.proto !== 'number' || obj.proto !== PROTO) {
        return { kind: 'reject', code: 'proto_mismatch' }
      }
      return { kind: 'accept' }

    case 'device':
    case 'state':
      return { kind: 'accept' }

    case 'capture':
      if (obj.phase === 'start') return { kind: 'accept' }
      if (obj.phase === 'end') {
        // end 必须带 reason：否则客户端会把掉线也当成「用户松手」⇒ 拿半截音频转写
        if (typeof obj.reason !== 'string' || obj.reason.length === 0) {
          return { kind: 'reject', code: 'missing_reason' }
        }
        return { kind: 'accept', transcribe: REASONS_SEND.has(obj.reason) }
      }
      return { kind: 'reject', code: 'malformed' }

    case 'error':
      // 未知 code 不得拒绝：按 retryable 处理（§9）
      return { kind: 'accept' }

    default:
      // 不变量 8：真·未知 op 静默忽略，为将来只增字段留空间
      return { kind: 'ignore' }
  }
}

/**
 * 解出一个音频帧的 PCM16 样本。载荷为空或长度为奇数 ⇒ 返回 null（§10）。
 * 同样抽成纯函数，好让夹具直接测它。
 *
 * @param {Uint8Array} buf 含 kind 字节的整帧
 * @returns {Int16Array|null}
 */
export function decodeAudioFrame(buf) {
  if (!(buf instanceof Uint8Array) || buf.length < 1) return null
  if (buf[0] !== AUDIO_KIND) return null

  const payload = buf.subarray(1)
  if (payload.length === 0) return null          // 空帧无意义，直接丢
  if (payload.length % 2 !== 0) return null

  // s16le：手动组装，不依赖宿主机字节序
  const pcm = new Int16Array(payload.length / 2)
  for (let i = 0; i < pcm.length; i++) {
    pcm[i] = payload[i * 2] | (payload[i * 2 + 1] << 8)
  }
  return pcm
}

export class WsTransport {
  /**
   * @param {string} url
   * @param {object} handlers onReady / onDevice / onState / onCapture / onError / onPcm / onWarn / onRawText
   */
  constructor(url = 'ws://127.0.0.1:8787', handlers = {}) {
    this.kind = 'ws-local-mic'
    this.url = url
    this.ws = null
    this.handlers = handlers
    this.ready = null
    this.device = null
  }

  static supported() {
    return typeof WebSocket !== 'undefined'
  }

  /** 连上并发 hello；resolve 的是 `ready` 消息。 */
  async connect(onDisconnect) {
    const ws = new WebSocket(this.url)
    ws.binaryType = 'arraybuffer'
    this.ws = ws

    let settle
    const ready = new Promise((res, rej) => {
      settle = { res, rej }
    })
    const timer = setTimeout(
      () =>
        settle.rej(
          new Error('等待 local-mic 的 ready 超时（15 秒）—— 确认 local-mic 已启动，且端口与这里填的一致'),
        ),
      15000,
    )

    ws.onopen = () => this.sendHello(true)
    ws.onmessage = (e) => {
      if (typeof e.data === 'string') this._onText(e.data, settle, timer)
      else this._onBinary(e.data)
    }
    ws.onclose = (e) => {
      clearTimeout(timer)
      settle.rej(new Error('local-mic 关闭了连接'))
      // 把 close 的 code / wasClean 交给调用方：正常关闭与异常断开的成因完全不同
      onDisconnect?.(e)
    }
    ws.onerror = () => {
      clearTimeout(timer)
      settle.rej(new Error(`连不上 ${this.url} —— 确认 local-mic 已启动`))
    }

    return await ready
  }

  /**
   * hello 是唯一的 C→L 消息，且**幂等**：想改订阅就再发一次（§4）。
   * 订阅状态是 per-connection 的，断了要重新声明 —— 没有历史状态要记。
   */
  sendHello(audio = true) {
    this.ws?.send(JSON.stringify({ op: 'hello', proto: PROTO, audio }))
  }

  /** 发任意一条 C→L 消息 —— 协议自测用（观察 local-mic 如何处置）。 */
  sendRaw(obj) {
    if (!this.ws || this.ws.readyState !== 1) return false
    this.ws.send(typeof obj === 'string' ? obj : JSON.stringify(obj))
    return true
  }

  _onText(text, settle, timer) {
    this.handlers.onRawText?.(text)

    let obj
    try {
      obj = JSON.parse(text)
    } catch {
      this._warn('收到非 JSON 文本，已忽略')
      return
    }

    const verdict = classifyMessage(obj)
    if (verdict.kind === 'reject') {
      this._warn(`收到不合规的 ${obj.op ?? '(无 op)'}，已丢弃：${verdict.code}`)
      return
    }
    if (verdict.kind === 'ignore') return

    switch (obj.op) {
      case 'ready':
        clearTimeout(timer)
        this.ready = obj
        settle.res(obj)
        this.handlers.onReady?.(obj)
        break

      case 'device':
        this.device = obj
        this.handlers.onDevice?.(obj)
        break

      case 'state':
        this.handlers.onState?.(obj)
        break

      case 'capture':
        // verdict.transcribe 只在 end 上出现：客户端据此决定送不送转写
        this.handlers.onCapture?.(obj, verdict.transcribe)
        break

      case 'error':
        this.handlers.onError?.(obj)
        // proto_mismatch 之后 local-mic 会直接关闭连接（§6：明确拒绝优于静默错解），
        // 所以这里必须把它变成 connect() 的失败原因，否则用户只看到"连接已关闭"。
        if (obj.code === 'proto_mismatch') {
          clearTimeout(timer)
          settle.rej(new Error(obj.message || '协议版本不匹配'))
        }
        break

      default:
        break
    }
  }

  _onBinary(data) {
    const buf = new Uint8Array(data)
    if (buf.length === 0) return

    const pcm = decodeAudioFrame(buf)
    if (pcm) {
      this.handlers.onPcm?.(pcm)
      return
    }

    if (buf[0] === AUDIO_KIND) {
      this._warn(`0x02 载荷长度为奇数（${buf.length - 1}）或为空，整帧丢弃`)
      return
    }
    // proto 1 只定义 0x02；其他 kind 忽略并告警一次（§10）
    this._warn(`未知二进制 kind 0x${buf[0].toString(16).padStart(2, '0')}，已忽略`)
  }

  _warn(msg) {
    this.handlers.onWarn?.(msg)
  }

  async disconnect() {
    this.ws?.close()
  }
}
