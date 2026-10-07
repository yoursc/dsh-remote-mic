# DSH-SEAMS.md — deepseek-harness 平台接入点（插件端手册）

> **写 dsh 插件时查的手册。** 架构定案与纪律见 [`../AGENTS.md`](../AGENTS.md)，
> 进度见 [`../DEV.md`](../DEV.md)，线缆协议见 [`PROTOCOL.md`](./PROTOCOL.md)。
> 来源：deepseek-harness 仓库源码实证（2026-09-28/29 调研）。
>
> **过程 / 历史见**（纪律 17）：[`notes/插件热加载纪律-修改意见-2026-10-02.md`](./notes/插件热加载纪律-修改意见-2026-10-02.md)（§7.9 改动前置清单的来源）、
> [`notes/RC003-dsh插件可行性-调研-2026-09-28.md`](./notes/RC003-dsh插件可行性-调研-2026-09-28.md)（平台接入点可行性调研）；
> 真机证据与排查过程见 [`PITFALLS.md`](./PITFALLS.md)；未决问题见 [`notes/插件设计待定问题-调研-2026-10-02.md`](./notes/插件设计待定问题-调研-2026-10-02.md)（待落定）。

---

## 1. 转写怎么调（Client → Host，源码实证）

`packages/experimental/client-ui-voice-input/src/client/mount.ts` 就是现成答案：

```ts
export const inject = ['remote', 'slots', 'locale', 'pluginNavigation']

const actions: VoiceInputInjected = {
  transcribe: async (request, signal) => await ctx.remote.speech.transcribe(request, signal),
  configure:  async (patch)   => { const r = await ctx.remote.speech.configure(patch);  if (!r.ok) throw r.error },
  prepare:    async (id, opt) => { const r = await ctx.remote.speech.prepare(id, opt);  if (!r.ok) throw r.error },
  ...
}

// Remote 挂载：把 Host 侧的 speech 命名空间挂到 Client 的 ctx.remote 上
const disposeRemote = await ctx.remote.$mount(contribution)
const ui = ctx.inject(['remote.speech', 'slots', 'locale', 'pluginNavigation'], registerUi)
```

要点：

- Remote 命名空间是 **用时挂载**（`ctx.remote.$mount(contribution)`），不是永久全局
- `prepare` / `configure` 返回 `Result` 风格（`{ ok }`），要手动拆包
- `transcribe(request, signal)` 的 `request` 就是 **WAV 字节**——正好是 local-mic 输出的 PCM16 组装产物

### 1.1 官方 bundle 模板（照抄这个结构）

`packages/experimental/voice-input-bundle` 就是"一个功能如何同时包含 Host 和 Client"的现成模板：

```json
// voice-input-bundle/package.json（节选）
"dependencies": {
  "@deepseek-ai/dsh-experimental-speech-to-text":             "workspace:*",  // Host：seam 定义
  "@deepseek-ai/dsh-experimental-speech-to-text-sensevoice":  "workspace:*",  // Host：本地推理
  "@deepseek-ai/dsh-experimental-api-speech-to-text":         "workspace:*",  // Host：Remote 控制器
  "@deepseek-ai/dsh-experimental-client-ui-voice-input":      "workspace:*"   // Client：UI
},
"dsh": { "bundle": { "patch": "./cordis.patch.yml" } }
```

**我们的对应结构**（v1 只需要 client 部分）：

```
dsh-remote-mic/
├── client 部分：连 ws://127.0.0.1:8787 + 调 remote.speech + insertText 注入
├── bundle：package.json（声明依赖现有 speech 包）
└── （v1 不需要 host 部分——直接吃服务端已有的 speech 能力）
```

> 只有当将来需要在 Host 侧加自己的 seam（比如 v2 的 `ctx.approval`）时才写 host 部分。

### 1.2 Remote 桥接的已知机制

- **Host 侧**：用 `@Remote` 装饰方法暴露（支持 `mode: 'stream'` 做流式）
- **Client 侧**：`ctx.inject(['remote.<名字>', ...])` 取得客户端
  - 实证：`client-ui-voice-input/src/client/mount.ts` 里 `ctx.inject(['remote.speech', 'slots', 'locale', 'pluginNavigation'], registerUi)`
- **UI 挂载点**：`ctx.slots.inject('conversation.input.activity', ...)` —— 麦克风 UI 就挂在这个 slot（位于模型选择器与发送按钮之间）

---

## 2. 文字注入（Client 侧）

```ts
// 实证自 VoiceInput.tsx
const span = inputActions.captureInsertion()      // 录音前捕获选区快照
const ok = inputActions.insertText(text, span)    // 注入，返回是否成功
if (!ok) { setPending(text); feedback(t('conflict')); return }  // 失败时保留文字
```

**注意两点**：

1. **注入有版本校验**——`span` 是录音前捕获的，若期间用户切换 Session 或选区已变，注入会被拒绝
2. **失败要保留文字**（官方做法是 `setPending` 显式待插入），不要丢弃

---

## 3. 语音输入 seam（`ctx.speechToText`，v1 用）

```ts
register(provider: SpeechProvider): () => Promise<void>
listProviders(): readonly SpeechProviderInfo[]
resolve(request: SpeechRequest): SpeechSpec
async transcribe(spec: SpeechSpec, signal: AbortSignal): Promise<Transcript>
snapshot(): SpeechSnapshot
async *follow(caller: AbortSignal): AsyncIterable<SpeechSnapshot>
async configure(patch: SpeechSelectionPatch): Promise<void>
prepare(id, options?): void
async cancelPreparation(id): Promise<void>
```

```ts
interface SpeechInput { audio: Uint8Array; language: string }   // WAV 字节
interface Transcript { text: string; audioSeconds: number; inferenceSeconds: number }
interface SpeechProvider {
  info: SpeechProviderInfo          // location: 'host-local' | 'cloud'
  preparation?: SpeechPreparation
  transcribe(input: SpeechInput, signal: AbortSignal): Promise<Transcript>
}
```

**三个必须记住的约束**：

1. **音频格式**：官方 `./wave` 校验"规范的 **16 kHz 单声道 PCM16 WAV**"。与 local-mic 输出的 PCM16 一致，零转换。
2. **只支持完整录音转写，明确不做流式**。原文："服务没有流式识别或语音合成方法。"
3. **音频是临时数据**，不写入 Session 事件；只有用户之后的普通提交才记录文字。

**现有 Provider**：`sensevoice-local`（SenseVoice 本地推理，中文友好）。v1 可以直接用它，不必自己接 whisper。

### 3.1 已定案：v1 用「松手一次性」，实时留到 v1.1

**用户明确的 v1 交互形态**（2026-09-29）：

- **松开后不自动发送**——发送由**另一个按钮**触发
  ⇒ 插件侧必须把「转写注入」与「发送执行」拆成两个动作，**别把发送绑在松手事件上**
- **v1 放弃"按住期间出字"**：按住时只显示波形/时长反馈，松手后转写一次
  ⇒ 零边界错误、只跑一次推理、质量最好，且不依赖 RTF 实测

**"实时"为什么没那么容易**（结论保留，供 v1.1 参考）：

| # | 事实 | 后果 |
|---|---|---|
| 1 | seam 只支持整段转写，明确不做流式 | 不存在"边录边出"的原生能力 |
| 2 | 每次 `transcribe(WAV)` 收完整 WAV、返回整段文字，**无时间戳、无部分结果** | 多段之间只能纯追加，**做不了单字修正** |
| 3 | 每段独立跑一次推理 | "实时"的物理下限 = **RTF**（推理耗时 ÷ 音频时长），**该值尚未实测** |

⇒ 真要实时，只有「按依据切段 + 增量转写」这一条路。切段依据**必须是静音/能量 VAD**，
不能用固定时长 —— 中文端到端模型对半截词会强行解码成"合理文字"
（"人工智" → "然"），固定窗口**每段都会碰一次**边界错误。

**v1.1 复活实时的两条前置**：

1. 实测 RTF。**不需要遥控器**：在 dsh 网页端用自带语音输入录一段话掐表即可
2. 确认注入可多次进行 —— `insertText()` 是快照 + 版本校验，增量注入要每次重新
   `captureInsertion()`；失败时**必须保留 pending 文本**，否则文字静默消失

**切段归插件，不归 local-mic**：它属于"用户要什么粒度的文字"，是交互逻辑而非硬件细节。
**不阻塞协议**：接缝给的是 PCM16 连续样本，插件按自己的采样数切片即可，**协议无需配合**。

---

## 4. 审批控制（`ctx.approval`，v2，现在只记录不做）

```ts
'approval/request'(
  this: Scoped<Agent>,
  req: ApprovalRequestEvent,
  next: () => Promise<ApprovalOutcome>,
): Promise<ApprovalOutcome>

type ApprovalOutcome = 'allowed-once' | 'rejected' | 'cancelled' | 'unavailable'
```

**机制**：cordis 瀑布式（waterfall）事件。**返回一个结果即认领该 agent 的决定；调用 `next()` 则委派给下一个回答者。** `Scoped<Agent>` 可只对特定 agent 接管。

**审计事件**（v2 做状态显示时用）：`approval/asked` 与 `approval/decided`，同 `id` 配对，asked 必然跟一个 decided。

### 4.1 ⚠️ 最大的设计风险：fail-closed

> "without an available answerer, the request **fails closed**"
> "抛出异常的监听器会使问题以 `unavailable` 关闭"

遥控器没电、蓝牙断连、超时、插件崩溃 → **agent 停在审批那步不动**。必须设计：超时转 `next()` 交回默认 UI；**监听器绝不能抛异常**（必须 catch 后转 next）。

## 5. 其他相关 seam（共 33 个）

- `ctx.userQuestions` —— `tool-ask-user` 消费，另一个需要人回答的点
- `ctx.sessionTelemetry` / `ctx.jobs` —— v2 状态巡视可用
- `ctx.commands`（core，非 seam）—— 注册面向人的命令

---

## 6. deepseek-harness 仓库速查

- 仓库：`https://github.com/deepseek-ai/deepseek-harness`
- **默认分支是 `master`，不是 main**（抓 raw 文件用错分支会 404）
- MIT / TypeScript

**关键文档**：

- `docs/capability-seams.zh.md`（60 KB，33 个 seam 的完整清单与消费关系）
- `docs/subsystems/voice-input.zh.md`（语音输入子系统，含 `ctx.speechController` / `ctx.speechToText` 完整 API）
- `docs/cordis-primer.zh.md`、`docs/cordis-api/`（cordis 插件容器与事件派发模式）

**关键源码**：

- `packages/interaction/user-approval/src/types.ts`（审批接口）
- `packages/experimental/speech-to-text/src/types.ts`（语音接口）
- `packages/experimental/api-speech-to-text/src/index.ts`（Host 侧 Remote 控制器，**我们写桥接要抄的模板**）
- `packages/experimental/client-ui-voice-input/src/client/mount.ts`（Client 侧挂载与 Remote 注入）
- `packages/experimental/client-ui-voice-input/src/client/VoiceInput.tsx`（`insertText` 用法实证）
- `packages/experimental/voice-input-bundle/package.json`（bundle 组合模板）

---

## 7. 我们的插件怎么接（2026-10-01 定案，附本机实证）

> 前几节是官方文档与源码转述；**本节是我们自己的决定**，每条都注明取证来源。

### 7.1 形态：纯 client 插件，不写 Host 部分

本机 `~/.dsh/profiles/desktop/package.json` 的 `dsh.profile.bundles` **已含**
`@deepseek-ai/dsh-experimental-voice-input-bundle`，而它的 `cordis.patch.yml` 已经 `insert` 了
`speech-to-text` / `speech-to-text-sensevoice` / `api-speech-to-text` / `ui-voice-input`。
SenseVoice 模型也已就位（`dataRoot` = `dshHomePath('speech-to-text','sensevoice')`
→ `model.int8.onnx` 228 MB + `tokens.txt`，另有 `silero_vad.onnx`）。

⇒ **`remote.speech` 已被别人挂载好：我们只消费、不 `$mount`，不需要 Host 包。**

⚠️ **反直觉但关键**：`remote.speech` **不是** dsh 的默认服务——`dsh-api-gateway` 与
`dsh-api-remotes` 里 `speech` 出现 0 次，它是实验性插件经 `ctx.remote.$mount()` 贡献的。
**但我们仍然必须用它**：按定案 A1，转写在 Host，**local-mic 只出 PCM16 音频、不出文字**，
浏览器侧拿到文字的唯一路径就是 `remote.speech.transcribe()`。
说"可以跳过 remote.speech 直接把音频变文字"的人，是不了解本项目链路。

### 7.2 UI：挂 `conversation.input.right`，与官方麦克风并存

槽位 kind 实测（`dsh-client-ui-conversation/lib/client.js` 的 children 表）：

| 槽位 | kind | 结论 |
|---|---|---|
| `conversation.input.activity` | **single** | 官方麦克风已占用 ⇒ **同优先级注册会抛异常** |
| `conversation.input.right` | **list** | ✅ **我们挂这里** |
| `conversation.input.left` | list | 备选 |

⚠️ 按 kind 有必填项：**`list` 必须给 `id`**（`keyed` 要 `key`、`chain` 要 `select`）。

**为什么保留官方麦克风而不是禁用它**（2026-10-01 用户拍板）：
**两个入口语义不同，不是两个麦克风** ——

- 官方麦克风 = **软件触发**：点一下开始、再点一下停，时长由用户掌控
- 我们的按钮 = **硬件 PTT**：遥控器按住即开麦、松手即出字，时长被固件 **~5.7 s 窗口**卡死

⇒ 视觉必须一眼可区分：遥控器那个做成**按住型**外观，录音中显示**实时时长**并在临近上限时提示。
做成"两个长得一样的麦克风"是错误表达；同理，`activity` 被官方占着时不要去抢。

### 7.3 接线

```
接缝 capture{phase:"start"} → span = inputActions.captureInsertion()   ← 快照取在这里
  ↓ 收 kind=0x02 音频帧，累积 PCM16 s16le / 16 kHz / mono
接缝 capture{phase:"end"}   → 补 16 kHz 单声道 PCM16 WAV 头
  ↓
await ctx.remote.speech.transcribe({ audio, language }, signal)
  ↓
inputActions.insertText(text, span) ? 成功→收工 : 存入 pending + 可见提示
```

**照抄官方的两个要点**（`client-ui-voice-input/lib/client.js` 实证）：

1. **快照在"开始录音"那一刻取**，一路持有到注入，不是注入前才取
2. 注入冲突后的重试按钮**必须重新取快照**：
   `insertText(pending, inputActions.captureInsertion())`。
   ⚠️ `setPending` **不是 dsh 的 API**，是插件自己的 `useState`——但这个模式要抄。

### 7.4 就绪感知：订阅，不轮询

用 `ctx.remote.speech.follow(signal)` 订阅（AsyncIterable\<SpeechSnapshot\>）拿 provider 目录与
`maxDurationSeconds` / `maxAudioBytes`，而不是一次性 `listProviders()`——
官方实现就是这么做的，ASR 未就绪能提前知道，UI 才不会在用户说完话之后才报"转写失败"。

### 7.5 F5 拦截：用户开关 + 随连接状态自动开合

官方 UI **完全不拦 F5**（只监听 `Escape` 取消），所以这块我们独占、**没有冲突**。
但全局拦截等于**用户可能无法刷新 dsh**，所以 2026-10-01 的定案是"仅接缝就绪时接管、平时交还"。
**2026-10-02 按用户要求改成可手动控制的开关**（更好用、也更可验），当前实现口径：

| 项 | 口径 |
|---|---|
| 拦什么 | **只拦裸 `F5`**（`e.key === 'F5' \|\| e.code === 'F5' \|\| e.code === 'BrowserRefresh'`） |
| 放行什么 | `Ctrl+R` / `Ctrl+F5` / `Shift+F5` / 任何带修饰键的组合 —— 用户自己的刷新永远可用 |
| 谁决定接管 | `state.interceptRefresh`，由**详情页开关**控制；默认关（没连接之前不夺走 F5） |
| 自动开合 | 按协议 §8.1 的 `state` **取值边沿**：`connected` ⇒ 开；其它取值 / 未知（fail-safe）⇒ 关；接缝 WS 断开 ⇒ 关 |
| 手动拨 | 写浏览器 localStorage，换机器、**没带遥控器时也能调试**；下一次状态变化会覆盖它（刻意如此） |
| 挂法 | `window.addEventListener('keydown', handler, true)`；只 `preventDefault()`，不 `stopPropagation` |
| 可见性 | 详情页有开关 + `设备状态` 行（机读枚举原样显示，便于人眼确认自动开合是否真的生效） |

> ⚠️ **口径已对齐（2026-10-02）**：`AGENTS.md` §3 与 `DEV.md` 原本写"必须拦 F5/Ctrl+R"，
> 已按本节改成"只拦裸 F5"。`Ctrl+R` **不拦**是刻意的 —— 遥控器只发裸 F5，
> 拦 `Ctrl+R` 只会白白夺走用户的刷新出口（诊断页 `diagnostics/js/app.js` 也是这个口径，已真机验证）。
>
> ⚠️ **验证状态**：手动路径已真机验证（开 ⇒ 裸 F5 不刷新；关 ⇒ 正常刷新）；
> **自动路径**（遥控器连上 / 断开时开关自己开合）在 2026-10-02 因遥控器不在手**尚未真机验证**。

### 7.6 打包与安装

插件是**普通 npm 包**（不是 dsh workspace 成员），装进 profile 即被登记：

```bash
dsh plugin --profile desktop add <绝对本地路径>   # 支持本地路径 ⇒ 不必发布即可迭代
```

清单形状（照本机已装的 `dsh-better-sidebar`）：

```json
{
  "dsh": {
    "bundle": { "patch": "./cordis.patch.yml" },
    "client": { "inject": ["@deepseek-ai/dsh-client-ui-slots", "..."], "platform": "web" },
    "manifestVersion": 1
  }
}
```

⚠️ **必须声明 `dsh.bundle.patch`，否则被判定 `not-bundle` 直接拒收。**

### 7.7 ⚠️ 已知风险：混合内容（部署前必须解决）

本机 dsh Web 走 **http**（19387 返回 401 而非 TLS 握手），`ws://127.0.0.1` 可用。
但按 AGENTS.md 的目标拓扑，Web 端最终跑在**远程服务器**上——**一旦它是 https，
浏览器会直接拦截 `ws://127.0.0.1`**（mixed content），整个插件静默失效。
v1 先在 http 本地形态下跑通；**远程部署前必须解决**（候选：local-mic 增加 wss + 自签证书）。

### 7.8 🚫 红线：客户端插件里不放全屏点击拦截层（2026-10-02 真事故）

「浮层点外面关闭」最常见的实现是渲染一层 `position:fixed; inset:0` 的透明 backdrop，
让"点外面"那一下落在它身上。**在本项目里禁止这么做。**

原因：dsh 的客户端热替换（`dsh-client-hmr` 每 500 ms 轮询 bundle → 推 `rebuilt` 帧 →
浏览器 `entries.reload()` → `replace()` 拆旧 fiber 再重装）**不保证把旧 DOM 收干净**。
实测后果：那层全屏 DOM 变成没人管的孤儿节点 ⇒ 它照旧盖住整页，但它的 React 处理器已经不在
⇒ **用户什么都点不了，只有刷新页面能救**（2026-10-02 踩实，当场把插件从 profile 摘除才恢复）。
浮层本身当孤儿最多是"多一块面板"，全屏层当孤儿是"整页报废"——风险量级完全不同。

**替代做法**（2026-10-02 的最终实现，即 §7 里说的方案 A）：**用框架原语，不自造** ——
`useAnchoredPosition`（定位）+ `useDismissOnOutsidePointer`（点外面关）+ `createPortal(panel, document.body)`
+ `Escape` + 点图标切换。面板 portal 到 body 之后不再受输入框容器的 overflow / 层叠上下文影响，
也不再需要"移开鼠标即关"那类自定义逻辑。这套万一全失效，最坏结果也只是一块小浮层留在原地、不挡路
——**任何情况下都不要再用全屏层去"兜底"**。

**改动前四步见 §7.9。**

**附带结论**：浏览器侧的 `reload()` 不可信 —— 改客户端插件后**一律硬刷新页面再验**，
别把"应该会热更新"当作验收条件（宿主侧确实会推 `rebuilt` 帧，但页面是否换上不保证）。

### 7.9 插件改动前置清单（2026-10-02 立；两次事故换来的）

插件的**每一次保存都可能立刻作用在用户眼前的界面上**（`link:` 装机 + 宿主每 500 ms 热推送，
而**浏览器侧是否就地替换并不可靠**，见 §7.8 结尾）。所以改之前必须先走下面四步 ——
这不是形式主义，而是同一天两次事故（§7.8 的全屏层把整页点死；`var open = React.useState(false)`
未解构 ⇒ 浮层永远关不掉、连错三轮）的直接产物。

**一、改动前四步（缺一不可）**

1. **取证**：症状落在哪一层（宿主 / 客户端 / 接缝 / 设备）？页面跑的是哪个 rev？
   有没有现成观察点（`/plugins/events` SSE、控制台报错、`performance.getEntriesByType('resource')`）？
   **在没有证据之前，不许改代码。**
2. **假设**：写明"我认为病因是 X"，并给出**能证伪它的最小观察**；假设与改动一起说给用户听。
3. **失败半径**：这个改动最坏会怎样？会不会波及宿主页面（全屏层 / 全局监听 / 往他人 DOM 注入）？
   **半径无界（全屏元素、全局快捷键、改他人节点）必须先取得用户明确同意。**
4. **单变量 + 回滚**：一次只改一处，用户验过再走下一步；动手前把回滚路径备好
   （刷新页面 / `dsh plugin --profile web remove dsh-remote-mic-plugin` / 备份 profile 清单）。

**二、硬红线**

- **禁止全屏点击拦截层**（backdrop / 遮罩）—— 见 §7.8。
- **禁止在他人页面挂全局监听当"兜底"**：除非说清失败半径，并带明确退出条件。
- **React 状态必须解构**：`var pair = useState(x); var v = pair[0], setV = pair[1];`
  绝不把数组 / 对象当布尔用（`var open = useState(false)` 恒为真，正是那个 bug）。
- **关合类 UI 优先用框架原语**，不自造：`@deepseek-ai/dsh-client-ui-primitives` 的
  `Tooltip(openOnClick)` / `useDismissOnOutsidePointer` / `useAnchoredPosition` / `Switch`；
  官方「会话统计」面板用的就是那一套。
- **改交互之前先问"这会不会影响宿主页面"**；答案为"可能"时，先要授权。

**三、交付前三条自检**

1. `node --check <改动的文件>`；
2. 从**运行中的实例取回真实产物**再跑一遍回归（不能只测工作区文件）；
3. 纯逻辑要有无硬件断言；**接线类**（状态 / 事件 / 槽位注册）要有无浏览器回归 ——
   实证：这类 bug 只有这种测试能抓到（同一条测试对原始提交版本 FAIL ①②，对修复版本全绿）。

**四、热加载的使用纪律**

- 改完先说清"这次要让用户看什么、怎么算通过"；
- **用户正在使用界面时，不在同一轮里连改多处**；
- 一旦出现"关不掉 / 点不动"这类**阻塞性**症状：**先给回滚路径**，再谈定位；
- 每次都提醒用户"改完请硬刷新"，别指望热替换。

> 来源与完整复盘：`docs/notes/插件热加载纪律-修改意见-2026-10-02.md`。

### 7.10 ⚠️ `device.paired` 不可信，不得拿来做门控（2026-10-04 真机）

接缝里的 `device` 消息带 `paired` 字段，**它看起来像"这台设备能不能用"的判据，但不是**。

2026-10-04 实测，**三个方向各错一次**：

| 情形 | `paired` 报的 | 真实情况 |
|---|---|---|
| 正常连接中（能读电量 84%） | `false` | ❌ 明明是配对的 |
| 配对已在系统设置里删除（可能） | `true` | ❌ 已删（见 `PITFALLS.md` #32） |
| 同一台设备、不同轮次恢复后 | 一轮 `false`、一轮 `true` | ❌ **连自报都不稳定** |

**规则**：

- ✅ 判"能不能用"**只看 `state`** —— `connected` 才是真可用。
- ⚠ `paired` 只能当作**展示性字段**，且 UI 必须能容忍它是错的（显示「—」，或干脆不显示）。
  ⛔ **绝不要写「`paired === false` 就禁用录音 / 提示去配对」这类门控**——必然误判。
- ⚠ `battery` 在断连时是**保留的陈旧值**（不是实时读数），同样不能当"设备在线"的证据。
  协议 §8.1「客户端据 `state` 判电量可信」的设计是对的，照它做。

> 当前实现背景：`paired` 仍来自 WinRT 的 `IsPaired`，**但这不是可靠判据**。
> 目标实现按 [`STATE-MODEL.md` §5.1](./STATE-MODEL.md) 由判定态派生；在判定层落地前，客户端不得依赖该字段。
> 详见 `PITFALLS.md` #34。
