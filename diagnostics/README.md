# diagnostics —— proto 1 接缝诊断页

独立于 dsh 的本机验证工具。**它不是 dsh，也不需要 dsh**——跑在 localhost 上的静态页面。

现在有两个用途：

1. **诊断页**：连 local-mic，显示接缝消息、试听录到的音频、在线跑一遍协议契约
2. **离线测试的家**：ADPCM 黄金向量（L0）、会话状态机（L1）、协议夹具的 TS 侧（L2）。
   这些**不依赖任何 dsh API**，也不需要硬件 —— 改 local-mic 时它们是唯一的对照物

## 跑起来

```bash
cd diagnostics
python -m http.server 8000
```

然后打开 `http://localhost:8000`。

> 不能双击 `index.html` 打开。ES module 在 `file://` 下会被 CORS 拦掉。

## 只有一条路：local-mic

**本页不连蓝牙。** 音频只有一条来路：

```
遥控器 ──BLE/ATVV──► local-mic ──WebSocket(proto 1)──► 本页
                     ↑ 会话、解码全在这一侧
```

本页是「**接缝观察器**」，三件事：显示接缝消息、试听录到的音频、在线跑协议契约。
`js/session.js` 与 `js/adpcm.js` 仍留在仓库里，但**只服务于离线测试**（L0 / L1），
**不在生产路径上**，别照它们的读数判断线上行为。

### local-mic 怎么起

```bash
local-mic/bin/Release/local-mic.exe     # 托盘程序，双击也行
```

端口默认 **8787**，可在 local-mic 主窗口底部改。本页填过的地址会被记住（localStorage），
下次自动填回 —— 改端口后两边不用反复手打。

## 开工前必做

**删除 RC003-MS 的蓝牙配对并重新配对一次。** 首次配对链路送达率只有 55%，
重配对恢复到 98.7%（AGENTS.md §3）。不做这一步，第一次测出断断续续会误判成代码问题。

## 页面上验证什么

按顺序看：

| # | 看哪里 | 期望 | 不成立意味着 |
|---|---|---|---|
| 1 | 连接后 `proto` 徽标与设备行 | `proto 1` + 型号/电量/已配对 | local-mic 没跑，或端口不一致 |
| 2 | 按住录音键 → 接缝消息流出现 `capture` | `phase:"start"` | PTT 语义拿不到，方案要重评 |
| 3 | 松手 → 又一条 `capture` | `phase:"end"` 且 `reason:"released"` | 看 reason 是哪个，别急着怪代码 |
| 4 | 音频统计 | 帧流连续率高、中断为「无」 | 真有中断才去查蓝牙 |
| 5 | 播放器自动载入，点播放 | 听得清自己说的话 | 见下方"读懂读数" |

**⚠ 按下录音键时页面不会刷新** —— 那是本页拦下的（录音键在 Windows 上就是 F5）。
若它真刷新了，说明拦截没生效，先解决这个，别的都测不了。

## 读懂读数

- **帧流连续率** = `实际帧数 ÷ 首末帧之间应到的帧数`。**无中断时它必然是 100%** ——
  这个指标只衡量"帧流有没有断"，不含设计内的延迟。
  > ⚠️ 别用「音频时长 ÷ 采集窗口时长」当送达率。窗口两端天然没有音频：
  > 按下后设备开流要 ~180 ms，松手后 local-mic 要等静默 600 ms + 收尾 30 ms 才判定结束。
  > 真机 2026-09-30 拿它算出 80%，把一条满分的链路误判成丢包。这两段延迟现在单独显示为
  > 「开流等待 / 收尾等待」。
- **帧流中断**：相邻帧间隔 > 2.5 帧（37.5 ms）才计一次。**这才是真丢帧。**
- **削波**：> 1% 说明增益过高，调回 1×。PCM 直接来自 local-mic，电平本就不低 ——
  别拿"归一到 −18 dBFS"这类给人耳听的制作电平当目标，那会把大量样本削掉。
- **RMS 低于 −40 dBFS** 接近静音，可试着把增益调大（注意别削波）。

## 试听与下载

- **松手后 WAV 会自动载入播放器**，直接点播放即可（不用先点"重新载入"）。
- **下载 WAV** —— 拿去做 ASR 验证用，不依赖 dsh 插件能否挂载。

## 协议自测（页面上那个按钮）

**离线夹具测客户端，在线自测测 local-mic** —— 两端各跑一遍，缝才算守住：

| 覆盖 | 在哪 |
|---|---|
| 客户端如何处置 local-mic 消息（`L→C`） | `test/l2-protocol.test.mjs`（离线，24 条） |
| local-mic 如何处置客户端消息（`C→L`）、消息字段（`BUILD`） | `local-mic --selftest`（离线，19 条） |
| **真 local-mic 收到消息后的实际行为** | 本页「跑一遍 C→L 契约」按钮 |

页面自测发的都是真消息，看的是真行为：`hello` ⇒ `ready`、`write` ⇒
`op_not_supported` **且连接必须保持**、真未知 op 静默忽略、`audio` 订阅开关。
proto 不匹配、二进制帧格式这类需要构造异常输入的场景留在离线夹具里。

## 测试

不需要任何硬件（46 项：L0 12 + L1 10 + L2 24）：

```bash
node test/l0-adpcm.test.mjs    # 解码算法：帧边界状态连续、nibble 序、WAV 头、增益
node test/l1-session.test.mjs  # 状态机：5.7 秒续期、Stop 去重、跳过 0x08、RC003 caps 怪癖
node test/l2-protocol.test.mjs # 协议夹具：跑 conformance/protocol-vectors.txt 的 L→C 半边
```

**L2 是 §14 要求的两端之一**：同一份夹具，C# 侧（`local-mic --selftest`）跑
`C→L` 与 `BUILD`，TS 侧跑 `L→C`。只测一端等于另一端没人守。

## 离线解码工具

```bash
node decode-bin.mjs capture.bin              # 解 local-mic 存出的原始帧
node decode-bin.mjs capture.bin out.wav      # 顺便写 WAV
```

`capture.bin` 是「2 字节 LE 长度 + payload」的重复序列，与 `probe_ble.py`、local-mic 的 `--record` 同格式。
**用它和 C# 侧解码结果做逐字节比对**，是两套解码器防漂移的第二道闸（第一道是 `conformance/` 的黄金向量）。

## 代码结构

```
js/transport.js     ★ proto 1 客户端：WsTransport + 两个纯函数（消息判定 / 帧解码）
js/wav.js           PCM16 → 规范 16 kHz 单声道 WAV
js/app.js           诊断 UI
js/atvv-consts.js   采样率等常量
js/adpcm.js         ⚠ 仅离线测试用（L0），不在生产路径
js/session.js       ⚠ 仅离线测试用（L1），不在生产路径
decode-bin.mjs      离线把 .bin 原始帧解成 WAV（配合 local-mic --record / 调试日志）
probe-ws.mjs        不经 UI 直接打印接缝字节 —— 排查"浏览器报的现象是二手信息"时用它
probe_ble.py        无依赖的蓝牙取证探针（列已配对设备 / 探测 ATVV 特征 / 端到端取音频），
                    绕开 local-mic 的独立观察通道；不参与生产路径
test/               L0 解码 + L1 状态机 + L2 协议夹具，纯 Node，无硬件
```

**`transport.js` 里只有 WsTransport。**

> `js/adpcm.js` 与 `js/session.js` 仍是重要资产：L0 的 12 条黄金向量、L1 的 10 条状态机用例
> 都靠它们。但注意 —— **它们测的是 local-mic 里那份 C# 实现的规格**，
> 自己并不跑在生产路径上。改 local-mic 时，这两套测试是你的对照物。
