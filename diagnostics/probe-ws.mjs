/**
 * 接缝探针 —— 连 local-mic，把收到的每条消息**如实**打出来。
 *
 * 为什么需要它：浏览器那一侧看到的是"已解析后的结果"（比如"未知 kind 0x75"），
 * 那是二手信息。要看接缝上真正的字节，得有个不经过 UI 的观察点。
 * 2026-09-30 就是靠它坐实了「local-mic 漏发 kind 字节」——
 * 客户端报的 0x75 其实是 PCM 的第一个采样值。
 *
 * 跑：node probe-ws.mjs [ws://127.0.0.1:18787]
 *     然后按住遥控器语音键说句话，松手。
 */

const url = process.argv[2] ?? 'ws://127.0.0.1:8787'

if (typeof WebSocket === 'undefined') {
  console.error('这个 Node 版本没有全局 WebSocket，需要 Node 22+')
  process.exit(2)
}

const ws = new WebSocket(url)
ws.binaryType = 'arraybuffer'

let audioFrames = 0
let samples = 0

ws.onopen = () => {
  console.log(`已连上 ${url}`)
  console.log('→ {"op":"hello","proto":1,"audio":true}')
  ws.send(JSON.stringify({ op: 'hello', proto: 1, audio: true }))
  console.log('\n按住遥控器语音键说话，松手后看统计。Ctrl+C 退出。\n')
}

ws.onmessage = (e) => {
  if (typeof e.data === 'string') {
    console.log(`文本  ${e.data.slice(0, 160)}`)
    return
  }

  const buf = new Uint8Array(e.data)
  const head = Array.from(buf.slice(0, 8))
    .map((x) => x.toString(16).padStart(2, '0'))
    .join(' ')

  audioFrames++
  const pcmBytes = buf.length - 1
  if (buf[0] === 0x02 && pcmBytes % 2 === 0) samples += pcmBytes / 2

  console.log(
    `二进制 ${String(buf.length).padStart(4)}B  ${head}  ` +
      `kind=0x${buf[0].toString(16).padStart(2, '0')}  pcm=${pcmBytes}B` +
      (buf[0] === 0x02 ? '' : '   ← kind 不是 0x02，客户端会整帧丢弃'),
  )
}

ws.onclose = (e) => {
  console.log(`\n连接关闭：code=${e.code} wasClean=${e.wasClean}`)
  console.log(`共收到 ${audioFrames} 个二进制帧，其中合法 PCM 样本 ${samples} 个（${(samples / 16000).toFixed(2)} 秒）`)
}

ws.onerror = () => {
  console.error(`连不上 ${url} —— 确认 local-mic 已启动，端口一致`)
  process.exit(1)
}
