/**
 * 诊断页 —— **proto 1 接缝观察器**。
 *
 * 它现在只做三件事，别的都删了（2026-09-30 大改）：
 *   ① 连 local-mic，把接缝上的每条消息如实显示出来（这是"不靠猜"的落点）
 *   ② 采集一段音频 → 生成 WAV → 当场试听 / 下载（"听得清"才是设备可用的最终标准）
 *   ③ **在线**跑一遍 §14 的 C→L 契约（离线夹具在 Node 里跑，这里跑真的 local-mic）
 *
 * 已删除：「连接遥控器」直连（路径 A 判死，且音频已有 local-mic 一条路）、
 * fixture 导出/回放（proto 1 接缝上不再有原始帧）、浏览器能力前置检查。
 * 删的理由都一样：**留着会让人以为音频还有第二条来路**。
 */

import { WsTransport, PROTO } from './transport.js'
import { buildWav } from './wav.js'
import { SAMPLE_RATE, FRAME_DURATION_MS } from './atvv-consts.js'

const $ = (id) => document.getElementById(id)

/** capture 的 reason 是**机读**枚举（PROTOCOL.md §8.2），人读要翻译一遍。 */
const REASON_TEXT = {
  released: '用户松手 —— 音频完整',
  timeout: '开麦后一直没有音频 —— 别送转写',
  disconnected: '采集途中连接断开 —— 部分可用，应提示不完整',
  aborted: '被设备掐断或 local-mic 主动终止 —— 之前几秒仍可用',
}

const state = {
  transport: null,
  pcmChunks: [],
  frameTimes: [],
  frames: 0,
  bytes: 0,
  samples: 0,
  startT: 0,
  stopT: 0,
  streaming: false,
  ticker: null,
  objectUrl: null,
  /** 协议自测的一个"等待某条消息"的槽位 */
  waiter: null,
}

/* ---------------- 输出 ---------------- */

function log(msg, cls = '') {
  const row = document.createElement('div')
  row.className = `log-row ${cls}`
  const ts = new Date().toLocaleTimeString('zh-CN', { hour12: false })
  row.textContent = `${ts}  ${msg}`
  const box = $('log')
  box.appendChild(row)
  // 上限必须有：任何刷屏都会把主线程拖住，而主线程一卡，
  // local-mic 的背压就开始丢最旧的音频帧（PROTOCOL.md §13）。
  while (box.childNodes.length > 400) box.removeChild(box.firstChild)
  box.scrollTop = box.scrollHeight
}

function setText(id, value) {
  const el = $(id)
  if (el) el.textContent = value
}

function pill(id, text, kind = '') {
  const el = $(id)
  if (!el) return
  el.textContent = text
  el.className = 'pill' + (kind ? ' ' + kind : '')
}

/** 接缝消息流：原样显示 local-mic 发来的每一条（这就是"观察器"的本体）。 */
function appendMessageRow(text) {
  const box = $('msg-log')
  const row = document.createElement('div')
  row.className = 'msg-row'

  let op = '?'
  let brief = text
  try {
    const o = JSON.parse(text)
    op = o.op ?? '?'
    brief = JSON.stringify(o)
  } catch {
    /* 非 JSON 就原样显示 */
  }

  const ts = new Date().toLocaleTimeString('zh-CN', { hour12: false })
  row.innerHTML = `<span class="t">${ts}</span><span class="op">${op}</span><span class="hex"></span>`
  row.querySelector('.hex').textContent = brief
  box.appendChild(row)
  while (box.childNodes.length > 200) box.removeChild(box.firstChild)
  box.scrollTop = box.scrollHeight
}

/* ---------------- F5：遥控器的语音键就是浏览器刷新键 ----------------
 *
 * 已实测坐实（AGENTS.md §5.1.1）：按下语音键 = 裸 F5，页面会刷新、WebSocket 断开。
 * 这里拦住它。**只拦裸 F5** —— 带修饰键的（Ctrl+F5 强刷、Ctrl+R）都是用户自己的操作，
 * 拦掉等于把刷新页面的能力也拿走了。
 */
function isDeviceRefreshKey(e) {
  if (e.ctrlKey || e.shiftKey || e.altKey || e.metaKey) return false
  return e.key === 'F5' || e.code === 'F5' || e.code === 'BrowserRefresh'
}

function installBlockRefresh() {
  let repeatCount = 0

  window.addEventListener('keydown', (e) => {
    const box = $('block-refresh')
    const blocked = isDeviceRefreshKey(e) && box && box.checked
    if (blocked) e.preventDefault()

    // 按住语音键时系统会**自动重复**发 F5（同一秒几十次）。逐条记日志
    // ⇒ 每秒几十个 DOM 节点 ⇒ 主线程被拖住 ⇒ local-mic 背压丢音频帧。
    // 所以重复事件照常拦截，但不记日志，松手后合并报一次。
    if (e.repeat) {
      repeatCount++
      return
    }

    if (repeatCount > 0) {
      log(`（上次按住期间系统自动重复了 ${repeatCount} 次，已合并计数）`)
      repeatCount = 0
    }

    try {
      sessionStorage.setItem(
        'lastKeydown',
        JSON.stringify({ t: Date.now(), key: e.key, code: e.code, keyCode: e.keyCode }),
      )
    } catch {
      /* 隐私模式下写不了，不影响主流程 */
    }

    if (blocked) {
      log(`按下语音键 = ${e.key}（已拦截，页面不会刷新）。你自己要刷新请按 Ctrl+F5。`, 'good')
    } else {
      log(`keydown：key=${e.key} code=${e.code} keyCode=${e.keyCode}`)
    }
  })

  window.addEventListener('beforeunload', () => {
    log('⚠ 页面即将卸载（刷新或导航）—— WebSocket 会随之中断。', 'err')
  })

  // 上一次刷新前的按键记录：刷新会清空内存日志，这是唯一的回头路
  try {
    const raw = sessionStorage.getItem('lastKeydown')
    if (raw) {
      const r = JSON.parse(raw)
      log(`上次刷新前最后一次按键：key=${r.key} code=${r.code} keyCode=${r.keyCode}`, 'warn')
    }
  } catch {
    /* 忽略 */
  }
}

/* ---------------- 端口记忆 ---------------- */

const URL_KEY = 'dsh-remote-mic.wsUrl'

function installPortMemory() {
  const input = $('ws-url')
  try {
    const saved = localStorage.getItem(URL_KEY)
    if (saved) input.value = saved
  } catch {
    /* 忽略 */
  }
}

/* ---------------- 消息等待（协议自测用） ---------------- */

function expectMessage(match, timeoutMs = 2000) {
  return new Promise((resolve) => {
    const w = {}
    const timer = setTimeout(() => {
      if (state.waiter === w) state.waiter = null
      resolve(null)
    }, timeoutMs)
    w.match = match
    w.resolve = (obj) => {
      clearTimeout(timer)
      resolve(obj)
    }
    state.waiter = w
  })
}

function feedWaiter(obj) {
  const w = state.waiter
  if (!w) return
  if (w.match(obj)) {
    state.waiter = null
    w.resolve(obj)
  }
}

/* ---------------- 连接 ---------------- */

async function connectLocalMic() {
  const url = $('ws-url').value.trim()
  try {
    localStorage.setItem(URL_KEY, url)
  } catch {
    /* 忽略 */
  }
  $('btn-local-mic').disabled = true

  try {
    log(`正在连接 local-mic：${url}`)

    const transport = new WsTransport(url, {
      onRawText(text) {
        appendMessageRow(text)
        let obj
        try {
          obj = JSON.parse(text)
        } catch {
          return
        }
        feedWaiter(obj)
        if (obj.op === 'error') {
          log(
            `error：${obj.code}（retryable=${obj.retryable}）${obj.message ? ' —— ' + obj.message : ''}`,
            obj.retryable ? 'warn' : 'err',
          )
          if (obj.retryable) log('retryable=true ⇒ local-mic 会自己重试，客户端应当安静等待，别弹错误框（§9.2）。')
        }
      },

      onReady(ready) {
        pill('proto-pill', `proto ${ready.proto}`, ready.proto === PROTO ? 'ok' : 'bad')
        setText('ready-detail', JSON.stringify(ready))
        log(`握手完成：proto=${ready.proto} audio=${ready.audio} caps=${JSON.stringify(ready.caps ?? {})}`, 'good')
        if (ready.proto !== PROTO) log(`⚠ local-mic 的 proto（${ready.proto}）与本页（${PROTO}）不一致。`, 'warn')
      },

      onDevice(d) {
        setText('dev-name', d.name ?? d.id ?? '(未知)')
        setText('dev-detail', `型号 ${d.model ?? '—'}｜电量 ${d.battery ?? '—'}｜已配对 ${d.paired ? '是' : '否'}`)
        log(`设备：${d.name ?? d.id}（${d.model ?? '—'}，电量 ${d.battery ?? '—'}，配对 ${d.paired ? '是' : '否'}）`)
      },

      onState(s) {
        pill('state-pill', s.state, s.state === 'error' ? 'bad' : s.state === 'connected' ? 'ok' : '')
        if (s.detail) log(`state：${s.state} —— ${s.detail}`)
      },

      onCapture(c, transcribe) {
        if (c.phase === 'start') {
          state.streaming = true
          state.startT = Date.now()
          state.stopT = 0
          state.pcmChunks = []
          state.frameTimes = []
          state.frames = 0
          state.bytes = 0
          state.samples = 0
          pill('state-pill', 'capturing', 'live')
          log(`采集开始（source=${c.source ?? '—'}）`)
          startTicker()
          return
        }

        if (c.phase === 'end') {
          state.streaming = false
          state.stopT = Date.now()
          pill('state-pill', 'idle')
          stopTicker()
          render()
          log(`采集结束：reason=${c.reason} —— ${REASON_TEXT[c.reason] ?? '未知 reason'}`, 'good')
          if (transcribe === false) log('这段按协议不该送转写（reason 不属于「音频可用」的那几类）。', 'warn')

          // 结束就自动载入播放器 —— 否则用户面对一个空播放器，
          // 以为"录了但不能播"（真机 2026-09-30 的反馈就是这个）。
          const stats = loadPlayer()
          if (stats) summarize(stats)
        }
      },

      onPcm(pcm) {
        state.pcmChunks.push(pcm)
        state.frames++
        state.bytes += pcm.length * 2
        state.samples += pcm.length
        // 与 startT / stopT 同一个时钟（Date.now），否则算不出差值
        state.frameTimes.push(Date.now())
      },

      onWarn(m) {
        log(`⚠ ${m}`, 'warn')
      },
    })

    state.transport = transport
    await transport.connect((e) => {
      log(`连接断开：code=${e?.code ?? '—'} wasClean=${e?.wasClean ?? '—'}`, 'warn')
      pill('state-pill', 'disconnected', 'bad')
      $('btn-disconnect').disabled = true
      $('btn-local-mic').disabled = false
      $('btn-probe').disabled = true
      $('btn-probe-bad').disabled = true
    })

    $('btn-disconnect').disabled = false
    $('btn-probe').disabled = false
    $('btn-probe-bad').disabled = false
    log('已连上 local-mic。按住遥控器语音键说话，松手后会自动载入播放器。', 'good')
  } catch (e) {
    log(`连不上 local-mic：${e.message}`, 'err')
    log('确认 local-mic 已在运行（托盘里的 DSH 遥控麦克风），且端口与上面填的一致。', 'warn')
    $('btn-local-mic').disabled = false
  }
}

async function disconnect() {
  try {
    await state.transport?.disconnect()
  } catch (e) {
    log(`断开时出错：${e.message}`, 'warn')
  }
  stopTicker()
  state.streaming = false
  pill('state-pill', 'disconnected')
  $('btn-disconnect').disabled = true
  $('btn-local-mic').disabled = false
  $('btn-probe').disabled = true
  $('btn-probe-bad').disabled = true
  log('已断开。')
}

/* ---------------- 统计与播放 ---------------- */

function startTicker() {
  stopTicker()
  state.ticker = setInterval(render, 200)
}

function stopTicker() {
  if (state.ticker != null) {
    clearInterval(state.ticker)
    state.ticker = null
  }
}

function render() {
  const now = state.streaming ? Date.now() : state.stopT || Date.now()
  const durMs = state.startT ? now - state.startT : 0
  const audioMs = (state.samples / SAMPLE_RATE) * 1000

  setText('st-frames', String(state.frames))
  setText('st-bytes', String(state.bytes))
  setText('st-samples', String(state.samples))
  setText('st-dur', state.samples ? `${durMs} ms（音频 ${audioMs.toFixed(0)} ms）` : `${durMs} ms`)

  // 🔴 送达率**不能**用「音频时长 ÷ 窗口时长」算。
  // 窗口里天然有两端没有音频：按下后设备开流要 ~180 ms，松手后 local-mic 要等
  // 静默 600 ms + 收尾 30 ms 才判定结束 —— 这 0.8 秒是**设计内的延迟**，
  // 拿它当丢帧会把一条满分的链路判成 80 分（真机 2026-09-30 误判过一次）。
  //
  // 正确的量法：只在**首帧到末帧之间**衡量 —— 那段时间本该帧帧不断。
  const ft = state.frameTimes
  if (ft.length >= 2) {
    const span = ft[ft.length - 1] - ft[0]
    const expected = Math.round(span / FRAME_DURATION_MS) + 1
    const rate = Math.min(ft.length / expected, 1)

    let gaps = 0
    let maxGap = 0
    for (let i = 1; i < ft.length; i++) {
      const gap = ft[i] - ft[i - 1]
      if (gap > maxGap) maxGap = gap
      if (gap > FRAME_DURATION_MS * 2.5) gaps++ // 隔了一帧以上才算丢
    }

    setText('st-rate', `${(rate * 100).toFixed(1)}%`)
    const el = $('st-rate')
    el.style.color = gaps === 0 ? '#3b6d11' : rate >= 0.95 ? '#854f0b' : '#a32d2d'
    setText('st-gap', gaps === 0 ? '无' : `${gaps} 处（最大 ${maxGap.toFixed(0)} ms）`)
  }
}

/** 把当前 PCM 组装成 WAV 并载入播放器。返回 stats，没数据返回 null。 */
function loadPlayer() {
  if (!state.pcmChunks.length) {
    log('这一段没有收到音频样本。', 'warn')
    return null
  }

  const gain = Number($('gain').value)
  const { buffer, stats } = buildWav(state.pcmChunks, SAMPLE_RATE, gain)

  setText('st-samples', String(stats.samples))
  setText('st-rms', `${stats.rmsDbfs.toFixed(1)} dBFS`)
  setText('st-peak', `${stats.peakDbfs.toFixed(1)} dBFS`)
  setText('st-clip', String(stats.clipped))

  if (state.objectUrl) URL.revokeObjectURL(state.objectUrl)
  state.objectUrl = URL.createObjectURL(new Blob([buffer], { type: 'audio/wav' }))
  const audio = $('audio')
  audio.src = state.objectUrl
  audio.load()

  return stats
}

function summarize(stats) {
  const spanMs = state.startT && state.stopT ? state.stopT - state.startT : 0
  const ft = state.frameTimes

  log('──────── 本次录音小结 ────────')
  log(
    `${state.frames} 帧 ｜ ${(stats.samples / SAMPLE_RATE).toFixed(2)} s ｜ ` +
      `峰值 ${stats.peakDbfs.toFixed(1)} dBFS ｜ RMS ${stats.rmsDbfs.toFixed(1)} dBFS ｜ 削波 ${stats.clipped}`,
  )

  // 窗口比音频长的那一段是**设计内的**，拆开说清楚，别再当成丢帧
  if (ft.length >= 2 && state.startT && state.stopT) {
    const headMs = ft[0] - state.startT
    const tailMs = state.stopT - ft[ft.length - 1]
    log(
      `窗口构成：开流等待 ${headMs.toFixed(0)} ms ＋ 音频 ${((stats.samples / SAMPLE_RATE) * 1000).toFixed(0)} ms ` +
        `＋ 收尾等待 ${tailMs.toFixed(0)} ms（窗口共 ${spanMs} ms）`,
    )

    const span = ft[ft.length - 1] - ft[0]
    const expected = Math.round(span / FRAME_DURATION_MS) + 1
    let gaps = 0
    let maxGap = 0
    for (let i = 1; i < ft.length; i++) {
      const gap = ft[i] - ft[i - 1]
      if (gap > maxGap) maxGap = gap
      if (gap > FRAME_DURATION_MS * 2.5) gaps++
    }
    log(
      gaps === 0
        ? `帧流连续：${ft.length} 帧横跨 ${(span / 1000).toFixed(2)} s，无中断 —— 链路是满的。`
        : `⚠ 帧流中断 ${gaps} 处（最大间隔 ${maxGap.toFixed(0)} ms），实际 ${ft.length} 帧 / 应到 ${expected} 帧 —— 这才是真丢帧。`,
      gaps === 0 ? 'good' : 'warn',
    )
  }

  if (stats.clipped > stats.samples * 0.01) {
    log(
      `⚠ 削波 ${stats.clipped} 个样本（${((stats.clipped / stats.samples) * 100).toFixed(1)}%）—— 增益过高。把增益调回 1×。`,
      'warn',
    )
  }
  if (stats.rmsDbfs < -40) {
    log('RMS 过低（接近静音）：可试着把增益调大（注意别削波）。', 'warn')
  }
  log('────── WAV 已载入播放器，点下面的播放键试听 ──────')
}

/* ---------------- 协议自测（在线跑 §14 的 C→L 契约） ----------------
 *
 * 离线夹具（diagnostics/test/l2-protocol.test.mjs）测的是**客户端**如何处置 local-mic 消息；
 * 这里反过来，测**真 local-mic** 如何处置客户端消息 —— 两端各跑一遍，缝才算守住。
 */

async function protocolSelfTest() {
  const t = state.transport
  if (!t) {
    log('先连上 local-mic 再跑协议自测。', 'warn')
    return
  }

  $('btn-probe').disabled = true
  $('btn-probe-bad').disabled = true
  log('════ 协议自测开始（对真 local-mic 发消息，看它怎么处置） ════')
  let pass = 0
  let fail = 0

  const step = async (label, send, match, timeoutMs = 2000) => {
    const p = expectMessage(match, timeoutMs)
    if (!t.sendRaw(send)) {
      fail++
      log(`✗ ${label} —— 发送失败（连接已断？）`, 'err')
      return null
    }
    const got = await p

    if (match === null) {
      if (got === null) {
        pass++
        log(`✓ ${label}`)
      } else {
        fail++
        log(`✗ ${label} —— 不该有响应，却收到 ${JSON.stringify(got)}`, 'err')
      }
      return got
    }

    if (got) {
      pass++
      log(`✓ ${label} → ${JSON.stringify(got)}`, 'good')
    } else {
      fail++
      log(`✗ ${label} —— 超时未收到期望的响应`, 'err')
    }
    return got
  }

  await step('hello{proto:1} ⇒ ready', { op: 'hello', proto: 1, audio: true }, (o) => o.op === 'ready')
  await step(
    'write（已删除的命令通道）⇒ op_not_supported，且连接保持',
    { op: 'write', data: [12, 0] },
    (o) => o.op === 'error' && o.code === 'op_not_supported',
  )
  await step('future_op（真未知 op）⇒ 静默忽略，无响应', { op: 'future_op' }, null, 800)
  await step('audio:false ⇒ ready（退订生效）', { op: 'hello', proto: 1, audio: false }, (o) => o.op === 'ready')
  await step('audio:true ⇒ ready（恢复订阅）', { op: 'hello', proto: 1, audio: true }, (o) => o.op === 'ready')

  setText('probe-result', `${pass}/${pass + fail} 通过`)
  $('probe-result').className = fail === 0 ? 'pill ok' : 'pill bad'
  log(`════ 协议自测结束：${pass}/${pass + fail} 通过 ════`, fail === 0 ? 'good' : 'err')
  log('以上覆盖 C→L 的处置。proto 不匹配、二进制帧格式等负例在离线夹具里（L2 24 条 / C# 19 条）。', 'warn')

  $('btn-probe').disabled = false
  $('btn-probe-bad').disabled = false
}

/**
 * 单独一条：proto 不匹配。**会断开连接**，所以不放进上面那串自动序列。
 *
 * §6 的核心安全属性是「显式拒绝优于静默错解」—— 光回一条 error 不够，
 * 必须**同时关闭连接**，否则老客户端会把新格式当旧格式继续解，得到白噪声且全程不报错。
 * 这条只有对真 local-mic 发一个新版本号才能验，离线夹具代替不了。
 */
async function probeProtoMismatch() {
  const t = state.transport
  if (!t) {
    log('先连上 local-mic。', 'warn')
    return
  }

  $('btn-probe-bad').disabled = true
  log('──── 测 proto 不匹配（§6：应明确拒绝**并关闭连接**） ────')

  const ws = t.ws
  const closed = new Promise((resolve) => {
    if (!ws) return resolve(false)
    ws.addEventListener('close', () => resolve(true), { once: true })
    setTimeout(() => resolve(ws.readyState === WebSocket.CLOSED), 3000)
  })

  const p = expectMessage((o) => o.op === 'error' && o.code === 'proto_mismatch', 3000)
  t.sendRaw({ op: 'hello', proto: 99 })
  const err = await p
  const didClose = await closed

  if (err && didClose) {
    log('✓ 收到 proto_mismatch 且连接被关闭 —— 符合 §6。重新点「连接」即可继续。', 'good')
  } else if (err && !didClose) {
    log('⚠ 收到 proto_mismatch，但连接没关 —— §6 要求关闭，否则老客户端会继续错解。', 'warn')
  } else {
    log('✗ 没收到 proto_mismatch —— local-mic 没有按 §6 拒绝。', 'err')
  }

  $('btn-probe-bad').disabled = false
}

/* ---------------- 绑定 ---------------- */

installPortMemory()
installBlockRefresh()

$('btn-local-mic').addEventListener('click', connectLocalMic)
$('btn-disconnect').addEventListener('click', disconnect)
$('btn-probe').addEventListener('click', protocolSelfTest)
$('btn-probe-bad').addEventListener('click', probeProtoMismatch)

$('gain').addEventListener('input', (e) => {
  setText('gain-val', `${e.target.value}×`)
})
$('gain').addEventListener('change', () => {
  if (state.pcmChunks.length) {
    loadPlayer()
    log('已按新增益重新载入播放器。')
  }
})

$('btn-replay').addEventListener('click', () => {
  if (loadPlayer()) log('已载入播放器。')
})

$('btn-wav').addEventListener('click', () => {
  if (!state.pcmChunks.length) {
    log('还没有音频数据 —— 先按住遥控器语音键说句话。', 'warn')
    return
  }
  const gain = Number($('gain').value)
  const { buffer, stats } = buildWav(state.pcmChunks, SAMPLE_RATE, gain)
  const url = URL.createObjectURL(new Blob([buffer], { type: 'audio/wav' }))
  const a = document.createElement('a')
  a.href = url
  a.download = `${Date.now()}-rc003.wav`
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
  log(`已下载 WAV（${stats.seconds.toFixed(2)} s，增益 ${gain}×）。`)
})
