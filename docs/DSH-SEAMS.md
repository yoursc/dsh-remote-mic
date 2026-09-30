# DSH-SEAMS.md — deepseek-harness 平台接入点（插件端手册）

> **写 dsh 插件时查的手册。** 架构定案与纪律见 [`../AGENTS.md`](../AGENTS.md)，
> 进度见 [`../DEV.md`](../DEV.md)，线缆协议见 [`PROTOCOL.md`](./PROTOCOL.md)。
> 来源：deepseek-harness 仓库源码实证（2026-09-28/29 调研）。

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
