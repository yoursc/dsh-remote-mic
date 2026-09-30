/**
 * L1 · 状态机测试（不需要任何硬件）
 *
 * 跑法：
 *   node diagnostics/test/l1-session.test.mjs
 *
 * 覆盖 AGENTS.md §4.6 的三条固件怪癖。全部走 FakeTransport，
 * 顺带证明 AtvvTransport 抽象真的成立——换实现，上层不改。
 */

import assert from 'node:assert/strict'
import { AtvvSession } from '../js/session.js'

let passed = 0
let failed = 0

async function test(name, fn) {
  try {
    await fn()
    passed++
    console.log(`  ✓ ${name}`)
  } catch (e) {
    failed++
    console.error(`  ✗ ${name}`)
    console.error(`    ${e.message}`)
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/** 最小实现，只记录写入的内容。形状与 AtvvTransport 一致。 */
class FakeTransport {
  constructor() {
    this.kind = 'fake'
    this.writes = []
    this.controlCb = null
    this.audioCb = null
  }
  async connect() {
    return { deviceName: 'fake', uuids: [] }
  }
  async write(bytes) {
    this.writes.push(Array.from(bytes))
  }
  async subscribeControl(cb) {
    this.controlCb = cb
  }
  async subscribeAudio(cb) {
    this.audioCb = cb
  }
  async disconnect() {}

  pushControl(bytes) {
    this.controlCb?.(Uint8Array.from(bytes))
  }
  pushAudio(bytes) {
    this.audioCb?.(Uint8Array.from(bytes))
  }
  writesSince(n) {
    return this.writes.slice(n)
  }
}

function setup(extra = {}) {
  const transport = new FakeTransport()
  const events = []
  const h = new Proxy(
    {},
    {
      get: (_t, prop) => (payload) => events.push({ type: String(prop), payload }),
    },
  )
  const session = new AtvvSession(transport, h, extra)
  return { transport, session, events }
}

const has = (events, type) => events.some((e) => e.type === type)

console.log('\nL1 · ATVV 会话状态机')

await test('握手会订阅两条通道并发出 GetCaps', async () => {
  const { transport, session } = setup()
  await session.handshake()
  assert.ok(transport.controlCb, 'CONTROL 未订阅')
  assert.ok(transport.audioCb, 'AUDIO 未订阅')
  assert.deepStrictEqual(transport.writes[0], [0x0a, 0x01, 0x00, 0x00, 0x03, 0x03])
})

await test('MicOpenRequested 会以 MicOpen 回应', async () => {
  const { transport, session } = setup()
  await session.handshake()
  transport.pushControl([0x08, 0x01])
  await sleep(10)
  assert.ok(
    transport.writes.some((w) => w[0] === 0x0c),
    '未发出 MicOpen（0x0c）',
  )
})

await test('★ 固件跳过 0x08 直接发 0x04 时不能卡死', async () => {
  const { transport, session, events } = setup()
  await session.handshake()
  transport.pushControl([0x04, 0x2a])
  await sleep(10)

  assert.equal(session.isStreaming, true, '未进入 streaming')
  assert.equal(session.sessionId, 0x2a)
  assert.ok(has(events, 'onStreamStart'))
})

await test('★ StreamStopped 重复到达只触发一次结束，且只发一次 MicClose', async () => {
  const { transport, session, events } = setup()
  await session.handshake()
  transport.pushControl([0x04, 0x2a])
  await sleep(10)

  transport.pushControl([0x00, 0x2a])
  transport.pushControl([0x00, 0x2a])
  transport.pushControl([0x00, 0x2a])
  await sleep(30)

  const stops = events.filter((e) => e.type === 'onStreamStop')
  assert.equal(stops.length, 1, `结束事件触发了 ${stops.length} 次，应去重`)
  const closes = transport.writes.filter((w) => w[0] === 0x0d)
  assert.equal(closes.length, 1, `MicClose 发了 ${closes.length} 次`)
  assert.deepStrictEqual(closes[0], [0x0d, 0x2a])
  assert.equal(session.isStreaming, false)
})

await test('★ streaming 期间按 2.5 秒节奏续期，结束后停止续期', async () => {
  const { transport, session } = setup({ extendIntervalMs: 20 })
  await session.handshake()

  transport.pushControl([0x04, 0x11])
  await sleep(95)

  const extendsBefore = transport.writes.filter((w) => w[0] === 0x0e)
  assert.ok(extendsBefore.length >= 3, `续期只发了 ${extendsBefore.length} 次`)
  assert.deepStrictEqual(extendsBefore[0], [0x0e, 0x11], '续期必须带上 session id')

  const mark = transport.writes.length
  transport.pushControl([0x00, 0x11])
  await sleep(95)

  const extendsAfter = transport.writesSince(mark).filter((w) => w[0] === 0x0e)
  assert.equal(extendsAfter.length, 0, '停止后仍在续期')
})

await test('未进入 streaming 就收到音频，应给出起始丢帧预警', async () => {
  const { transport, session, events } = setup()
  await session.handshake()
  transport.pushAudio(new Uint8Array(120).fill(0x33))
  await sleep(10)

  const warn = events.filter((e) => e.type === 'onWarn')
  assert.equal(warn.length, 1, '缺少预按住导致的起始丢帧预警（§7 坑 6）')
  assert.match(warn[0].payload, /起始丢帧/)
})

await test('★ 超过 5.7 秒的录音靠续期维持，不被固件掐断', async () => {
  // 固件免费窗口约 5.7 秒；只要续期间隔小于它，长录音就能持续。
  const { session, transport } = setup({ extendIntervalMs: 20 })
  await session.handshake()
  transport.pushControl([0x04, 0x01])
  await sleep(120)
  assert.equal(session.isStreaming, true, '长录音期间应始终保持 streaming')

  transport.pushControl([0x00, 0x01])
  await sleep(20)
  assert.equal(session.isStreaming, false)
})

await test('DecoderSync 重置解码器状态', async () => {
  const { transport, session, events } = setup()
  await session.handshake()
  transport.pushControl([0x0a, 0x00, 0x00, 0x00, 0xfb, 0x2e, 0x14])
  await sleep(10)

  const sync = events.find((e) => e.type === 'onDecoderSync')
  assert.ok(sync, '未解析 DecoderSync')
  assert.equal(sync.payload.predictor, -1234, 'predictor 应为 i16 大端（0xfb2e = -1234）')
  assert.equal(sync.payload.stepIndex, 0x14)
  assert.equal(session.decoder.predictor, -1234)
})

await test('Caps 解析遵循 RC003 固件怪癖（codecs=0 时取 bytes[4] 低两位）', async () => {
  const { transport, session } = setup()
  await session.handshake()
  // version=0x0100，codecs=0，bytes[4] 低两位 = 0b10 → 应判为支持 16 kHz
  transport.pushControl([0x0b, 0x01, 0x00, 0x00, 0x02, 0x00, 0x78])
  await sleep(10)

  assert.equal(session.caps.versionHex, '0x0100')
  assert.equal(session.caps.codecs, 0x02, '未走 [4] 低两位的兜底分支')
  assert.equal(session.caps.supports16k, true)
  assert.equal(session.caps.frameSize, 120)
})

await test('reset 清空会话与解码器，且不再续期', async () => {
  const { transport, session } = setup({ extendIntervalMs: 20 })
  await session.handshake()
  transport.pushControl([0x04, 0x05])
  await sleep(50)
  await session.reset()

  const mark = transport.writes.length
  await sleep(60)
  assert.equal(transport.writesSince(mark).length, 0, 'reset 后仍在写入')
  assert.equal(session.decoder.predictor, 0)
  assert.equal(session.isStreaming, false)
})

console.log(`\n通过 ${passed} 项，失败 ${failed} 项\n`)
process.exit(failed ? 1 : 0)
