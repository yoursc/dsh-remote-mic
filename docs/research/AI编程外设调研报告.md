# Web Coding 外设行情调研报告

调研时间：2026 年 9 月 28 日
调研范围：面向 AI 编程 / Agent Coding 场景的物理输入与控制外设
重点关注：Windows 平台可用性（你的环境）

---

## 一、先给结论：你的判断成立，而且这个品类刚刚成形

你的直觉是对的——**瓶颈从"打字"转移到了"批准"**。

行业里现在有个更准确的说法：**AI 硬件外设 = "审批延迟削减工具"（physical approval-latency reduction tool）**。

用 Agent 写代码时的真实工作结构已经变成：

```
描述需求（自然语言） → 等待输出 → 查看 Diff → 确认/中断 → 切到下一个 agent 巡视
```

其中"打字"只剩下第一段，而后面四段是**监督和决策**。所以只解决"说话替代键盘"是不够的，真正缺的是一个能同时干三件事的东西：

| 层 | 要解决的问题 | 对应物理器件 |
|---|---|---|
| 1. 拾音层 | 麦克风离嘴够近、识别不糊 | 遥控器顶部麦 / 手持麦 / 领夹麦 / 桌面麦 |
| 2. 触发层 | 决定"什么时候开始听你说话" | PTT（Push-to-Talk）按键 / 脚踏 |
| 3. 确认层 | Approve / Reject / Interrupt / 撤销 | OK 键、返回键、方向环、专用键帽 |
| 4. 巡视层 | 不用切窗口就知道 N 个 agent 跑得怎样 | RGB 状态键 / 副屏 / 手机 App / LED 点阵 |

现在市面上所有产品，本质上都是在这四层里选不同的组合。**只做第 1、2 层的（脚踏、录音笔、免驱按钮）是不完整的，那正是你说的"话梅县笑话"产品的升级版。**

一句话：**单 agent 时代一个快捷键够用；多 agent 并行时代才需要一块面板。** 你现在用 OpenCode / Codex 这类工具，正处在第二条分水岭上。

---

## 二、市场全景：三类玩家，三种打法

### 玩家 A：平台方（OpenAI）—— 做"状态可见性" + 锁生态

#### OpenAI Codex Micro（¥1630 / $230）

- **发布时间**：2026 年 7 月 15 日，与加拿大键盘厂 Work Louder 合作，**48 小时内售罄**，二级市场炒到 $1000
- **这是 OpenAI 的第一件硬件产品**
- **硬件**：13 颗矮轴机械键 + 1 个旋钮 + 1 个摇杆 + 顶部电容触控条，CNC 铝 + 半透 PC，USB-C / 蓝牙双模，可选 clicky（POM）或静音（POK）轴，寿命 5000 万次、40g 触发压力、2.8mm 行程。整机 2.6 lbs，比同尺寸 Razer Nostromo（0.65 lbs）重得多
- **四层全都做了**：
  - 顶部 6 颗半透 RGB "Agent Keys"：**白=空闲 / 蓝=思考中 / 绿=有新响应 / 琥珀=等你确认 / 红=报错**，按下直接切到对应线程
  - 机身四周还会有弧线光带：**海绿色=麦克风收音中**，白色蛇形动画=处理语音 prompt 中，纯白=处理完成
  - 旋钮 = 实时调节 Reasoning Level（推理算力档位），不是选模型
  - 摇杆 = 弹径向菜单（PR Review / Debug / Refactor）
  - 6 个控制层 × 全部按键旋钮摇杆 = 最多 **120 个可编程动作**
  - 专用 Push-to-Talk 按键
- **附赠 32 颗图标键帽**，包含两个很说明问题的：**YOLO**（跳过所有审批，实际跑的命令是 `--dangerously-bypass-approvals-and-sandbox`）和 **YEET**（暂存→提交→推送→自动开 PR 并写好说明）
- **关键限制（必须知道）**：六颗 Agent 灯的 RGB 状态**必须依赖 ChatGPT 桌面 App 做客户端桥接**。如果你只用 Codex CLI / VS Code 扩展 / JetBrains 插件 / 浏览器版，**那六颗灯是不亮的**，它就退化成一块普通宏键盘
- **平台**：macOS / Windows / Linux（Linux 为社区支持）
- **性价比**：本质是 Work Louder Creator Micro 2（$174）的贴皮，$230 = $174 + 约 $50 的图标键帽，溢价全在 OpenAI logo 上

> 点评：这是最有说服力的行业信号——**一家头部模型公司认为，值得为"人怎么启动和监督 AI"专门出硬件**。但对你来说它的核心卖点（RGB 状态灯）恰好绑定 ChatGPT 桌面端，而你主要用 OpenCode / Codex，价值打折不少。

### 玩家 B：配件厂商 —— 做通用 HID，跨工具通吃

#### 1. Ulanzi AU05 VibeKey（¥299 京东 / $61.59 海外）

**最值得关注的一个，因为它是目前唯一把"麦 + 键"塞进一个手持小设备的量产产品。**

- 铝合金 CNC，84g，11.2 × 3 × 2.8 cm，带挂绳
- 3 颗可自定义键 + 1 个多功能旋钮 + **内置麦克风**
- Gateron 矮轴热插拔 + DDL 十字轴键帽（可自己换手感）
- 300mAh，USB-C 充电约 4 小时，Work 模式下每天用 3 小时可撑 5 天
- 通过 USB dongle 连接；配置软件 Ulanzi Studio V3.2.0+
- 支持 Windows 10+ / macOS 12+
- **三个预设模式**：AI Coding（语音输入 / 新会话 / 取消 / 确认 / 拒绝 / 打开侧栏）、Everyday（搜索 / 撤销 / 截图 / 听写 / 复制 / 粘贴）、Meeting（笔记 / 翻页 / 离开 / 静音 / 摄像头 / 举手）
- 官方列明兼容：ChatGPT、Claude、Codex、Typeless
- 注意：**它本身不做语音识别**，它就是个"带麦克风的硬件快捷键控制器"，识别要交给 Typeless / Wispr Flow 这类软件

> 点评：形态上最接近你描述的"手持：按住说话 + 确认/拒绝/撤销"。$61.59 的海外定价非常激进，国内京东 ¥299。

#### 2. Elgato Stream Deck Pedal（¥640 / $89.99）

- 三踏板，金属底座，弹簧可更换调节踩踏力度
- 官方明确主打之一：**一路专给 Push-to-Talk**
- Smart Profiles：切 App 自动切换三踏板的功能定义
- Elgato Marketplace 插件生态
- **问题**：脚踩上手程成本高；脚踏的定位逻辑和"手上有个东西"是完全不同的交互习惯

#### 3. Wispr Pedal（¥710~1410 / $99~199）

- 单踏板，无线蓝牙，出厂预映射给 Wispr Flow，含 1 年 Flow Pro
- 也可作为标准 HID 重映射到任意快捷键
- **只有一踩，没有第二第三个动作**——只有触发层，没有确认层

#### 4. 通用 USB 脚踏（¥140~210 / $19~30）

- iKKEGOL、PCsensor 等品牌，单击一次按键
- 硬件是三轮 Changable Spring 的 Kinesis Savant Elite2（$189，2026 年综合最佳，5-10 年寿命）是这条线的天花板
- 适合做"先验证习惯是否成立"的最低成本门票

### 玩家 C：社区 / 开源 —— 改造存量硬件，成本几乎为零，但依赖项目活着

#### 1. 小米蓝牙遥控器 2 Pro（RC003）+ SayAll —— **你已经买的那条路**

这条在中文圈已经非常成熟了（少数派 2026-09-22 一篇详细教程、什么值得买也有评测）。

**硬件：¥50~60**
- 蓝牙 5.4 BLE 直连，无需接收器
- 内置近场人声麦克风 + 独立居中的语音键
- 300mAh，Type-C 充电，整机约 61g
- 虽然写⑼ ABS 工程塑料 + 金属喷砂喷涂，但手感远超几十元定位
- 按键清脆、回弹利落，OK 键有下凹弧度，方向环四向有凸起刻度，**可以完全盲操**

**软件：SayAll（`HD838A/remote-mic-app`）**
- 遥控器语音键走的是专用语音数据包，系统不会认它当麦克风 —— SayAll 干的事就是：
  1. 在系统里生成一个虚拟音频输入设备（macOS 下为 `MiRemoteV 2ch`，从 BlackHole 源码独立派生，可与已装的 BlackHole 共存）
  2. 按住语音键时接收音频实时推流到虚拟麦，松手结束
- SwiftUI 开发，GPL-3.0，CPU 占用 <0.5%，内存约 50MB
- 项目 2026 年 7 月底建仓，一个月 880+ stars

**Windows 路径（重要，你是 Windows）**
- `getsayall/remote-mic-app-windows`：Rust + Tauri 2 + Vue 3，当前公网版本 **v0.2.2 预览版**
- 支持 RC001 和 RC003
- Windows 预览版默认把语音快捷键映射为 **Ctrl + Win**
- **语音链路需要额外配置 VB-CABLE**（虚拟音频线）
- 要求 Windows 10 1809（build 17763）+ x64
- **无 Authenticode 签名，首次运行会被 SmartScreen 拦截**
- 另一条社区路线：`zhaozhuque/MIC-RC003-Windows`

**配套的语音内核（Windows 可用）**
- **微信输入法（WeType）**：中文识别率和标点断句好，原生支持"按住快捷键说话"（把它设成 Ctrl+Win）
- **豆包输入法**：同样兼容
- **Typeless**（$12/月年付；免费版 4000 词/周）：全平台覆盖，100+ 语言自动检测，意图理解而非逐字转录，会自动去口头禅、修口误
- **Wispr Flow**（$12/月年付；免费版 2000 词/周）：**代码语法识别最强**（独立测试 97.2%），自带上下文感知，和 Cursor / Windsurf / Claude Code 深度集成

**四层里它能做到的工作量（少数派作者的实际映射）**

| 遥控器按键 | 发送快捷键 | 动作 |
|---|---|---|
| 方向左 ◀ | `Ctrl+Alt+Left` | 上一个 Tab |
| 方向右 ▶ | `Ctrl+Alt+Right` | 下一个 Tab |
| 方向上 ▲ | `Ctrl+Alt+Up` | 上一个 Workspace |
| 方向下 ▼ | `Ctrl+Alt+Down` | 下一个 Workspace |
| 中心 OK | `Enter` | 确认 / 放行 |
| 返回键 ⮌ | `Ctrl+C` / `Esc` | 中断 / 取消 |
| 语音键 🎙 | `Fn`（长按 PTT） | 按住口述 Prompt，松手上屏 |

> **这台 ¥60 的遥控器，同时覆盖了拾音层、触发层、确认层三层。** 缺的只有第 4 层（agent 状态巡视），但那层可以用软件免费补——见下文 AgentDeck。

**已知坑（省你时间）**
1. SayAll 新手引导在部分 macOS 会卡住 → 终端执行两行 defaults write，重启即可（原文有命令）
2. 微信输入法 Mac 版录不进独立 Fn → 改用冷门组合键（如 `Ctrl+Option+V`），两处都设成同一个
3. **SayAll 里给方向键映射自定义快捷键时，务必清空"双击"绑定，并勾选"允许连续快速按"** —— 否则连按第二下会被防连击保护吃掉
4. 台式机无蓝牙时，务必买 BT 5.0+ 适配器，BT 4.0 连按方向键会明显丢键
5. Windows 配对时确认设备被识别为"键盘"类别，否则部分键无响应

#### 2. AgentDeck —— 免费补上第 4 层（OpenCode 用户尤其在意）

这是本次调研里对你最对口的一个开源项目，**原生支持 OpenCode**。

- MIT 协议，`npx @agentdeck/setup` 一键安装
- 一个本地 daemon（端口 9120）汇总 **Claude Code、Codex CLI、OpenCode**（另有实验性 OpenClaw 支持）
- **通过 agent 原生 hooks / SSE / Gateway 事件读状态**，不是解析终端画面，所以准确度高
- 可在面板上：切换会话、回答 YES/NO/ALWAYS、发 Ctrl+C、切 Plan / Accept Edits 模式、触发 REVIEW / COMMIT / 自定义提示词
- **支持的面**：Elgato Stream Deck（含 Mini/XL/Plus）、Ulanzi D200H / D200X、iPhone / iPad / Android 原生 App、ESP32 面板（Round AMOLED、IPS LCD）、7.5" 墨水屏 TRMNL、Ulanzi TC001 / Pixoo64 / Timebox / iDotMatrix LED 点阵、终端 TUI
- **没有任何硬件也能用**：`agentdeck dashboard` 会给你一个终端仪表盘（还有 Braille 渲染的水族箱）
- macOS 15+ / Windows 11，Node.js 22+（Windows 用 `winget install OpenJS.NodeJS`，hooks 是 PowerShell one-liner，无需 bash）
- 还带一个叫 APME 的评测框架，用本地 SQLite 追踪每个 agent 跑的效率，帮你判断哪个模型/配置最划算

> 点评：**这是" ¥230 的 Codex Micro 灯效"的开源等价物，而且跨 agent。** 你的手机/平板完全可以只当监视器和触摸面板用——这恰好呼应你说的"电脑屏幕只是交互界面"。

#### 3. 一堆 Raspberry Pi / DIY 方案

OpenClaw 社区有人用 Pi Zero 2W + 按键 + 麦克风 + 扬声器，做一个"房间级语音助手站"，按下说话松开发送到 agent。适合家里/工位放一个，不适合随身。

---

## 三、随身 / 移动端这条线（你已经有的 YOOOCLAW 就是这里）

你提到的 **YOOOCLAW C·ONE**，我查到了详细的量产信息：

- 信用卡大小、金属机身、磁吸在 iPhone 背面，**¥599 起**（含 ArkClaw 云服务的版本 ¥799）
- 一颗实体胶囊键 + 一条 LED 呼吸灯带
- **长按 AI 键说话 → 松开自动转文字发给 OpenClaw**（你说 PPC 的"对讲机"场景）
- **双击开始录音，再双击结束**，最长 5 小时连续录音；自动转写并生成结构化摘要 + 待办清单 + 关键信息（人名/时间/数字）
- 双全向麦，8-10 人会议室可用
- 还可以接管微信 / 飞书 / 钉钉 / 企微 / 邮件的通知，按优先级用灯带颜色提醒
- 续航极好（实测一个月只充两次电）
- **已知问题**：安卓机型因摄像头模组大，磁吸位置偏下会突出约 5mm；按键在正面，手机扣桌上按压背面会误触录音；目前只支持自家 + ArkClaw；不支持发图/文件

同赛道的其他产品：
- **EinClaw**（杭州，$43 夹式麦克风）：向 OpenClaw 发语音指令，**首批只出货 100 台，两个人用国产零件手搓的**（CNBC 2026-04-27 报道）—— 极端轻资产，说明这事儿门槛极低
- **Plaud Note / Plaud ONE**：名片/卡片录音形态的开创者，偏 transcriber
- **钉钉 DingTalk A1**：裸机 40g，带磁吸套 70g，买了 Tajima 那一去年的抖音爆品
- **TicNote**：裸机 28.77g，带壳 57.7g
- **ClawBuds**（AI 耳夹耳机）：OpenClaw 生态首款硬件，有意思的是它走 **6/9 轴 IMU 头势交互**（点头/摇头/倾斜/转头）+ 本地低功耗唤醒词 + 全双工对话 <300ms，续航 14-18 小时。**用头部动作替代按键，是目前唯一不用手的确认方式**
- **Flic Mic for AI**：Flic（已出货 100 万+ 智能按钮）做的，两个可配置按键，内置麦克风，**仅在按下时收音**，纽扣电池用几年不用充电，官方明确写支持 OpenClaw / Home Assistant / Alexa / Notion / n8n。2026 年限量首发
- **Relay Q**（IFA 2026 发布）：最大区别是它**不做转写，直接把原始音频喂给多模态模型**，并结合当前屏幕上下文、选中文本和长期记忆，理解你说了一半的自我更正和犹豫。手持底座 + 可拆磁性领夹两用。macOS 现已可用，$15/月 Unlimited，**Windows / iOS / Android 要等到 2027 年初**。硬件 early supporter bundle $150

> 这条线的定位很清楚：**解决"不在桌前"的场景**。YOOOCLAW 和你准备搞的桌面遥控器是互补关系，不是替代——一个管移动/离开工位，一个管坐在桌前但不趴着。你把它俩拼起来，才是完整的"龙虾 + 物理世界"介质。

---

## 四、如果连键都不想按：重度高质点键替代品（供参考，非主流路线）

这条线来自无障碍/RSI 人群，十年积累，能力上限远超上面所有产品，但验证成本高：

- **Talon Voice**（免费开源，Win/Mac/Linux）：整套让你用语音+眼动控制整台电脑。命令式非听写式，`.talon` 文件 + Python 脚本自定义，本地处理不出网，Tobii 4C/5 做眼动鼠标，还能用"pop/hiss"嘴部噪声做点击。学习曲线 1-2 周起步，Josh W. Comeau 用它做全套 hands-free 开发，自评效率约正常状态的 50%
- **Cursorless**（Talon 的扩展）：结构化语义编辑，"chuck line"、"bring funk main"这种，配套才真正强大
- **Serenade**：开源 speech-to-code，比 Talon 温和，**但 2026 年多家评测指出开发基本停滞，不建议新投入**
- **眼动硬件**：Tobii 5 约 ¥1620 / $229（Josh Comeau 用的）
- **Blazing Fast 的现实建议**：几乎没人 100% 无手操作，多数人的实际状态是"语音听写做长文本 + 语音命令做常用动作 + 偶尔用键盘"的混合模式。**这也是对你的提醒——别追求彻底零键盘，追求"手不需要常年在键盘上待命"就够了。**

语音内核软件的横评（Windows 可用性是硬门槛）：

| 工具 | 价格 | Windows | 离线 | 代码识别 | 备注 |
|---|---|---|---|---|---|
| Typeless | ¥85/月（$12 年付） | ✅ | ❌ | 一般 | 100+ 语言自动检测，智能编辑最强，单次 6 分钟上限 |
| Wispr Flow | ¥85~105/月（$12~15） | ✅ | ❌ | ★★★ 最强 | 代码语法独立测试 97.2%，SOC2+HIPAA，免费额度 2000 词/周 |
| SuperWhisper | ~$8.49/月 或 ~$250 买断 | ❌ Mac only | ✅ | 一般 | 技术术语准确率 94% |
| VoiceInk | $19~25 买断 | ❌ Mac only | ✅ | 一般 | GPL v3 开源，可自行编译免费 |
| Voibe | $99 买断 / $4.9 月 | ❌ Mac only | ✅ | ★★ | 唯一支持 Cursor/Windsurf 文件 chip 插入 |
| ByeType | 免费 | ❌ Mac only | 部分 | 基础 | 国人开发，多引擎切换，中文优化 |
| 微信输入法 / 豆包输入法 | 免费 | ✅ | ❌ | 基础 | 中文实用性最强，原生支持"按住快捷键说话" |

**结论：Windows 上真正能打的就是 Typeless 和 Wispr Flow，以及免费的微信/豆包输入法。** 离线党在 Windows 上基本没有像样的选项，这是 Windows 的硬伤。

---

## 五、给你的三条可选路径（按成本排序）

### 路线 A：¥60，已购采用中，现在就能跑起来（推荐先走这条）

```
小米蓝牙遥控器 2 Pro（RC003）  ¥50-60
+ SayAll for Windows (v0.2.2 预览版)   免费
+ VB-CABLE                            免费/捐赠版
+ 微信输入法 or 豆包输入法 or Wispr Flow  免费起
+ AgentDeck（用于第 4 层状态巡视）       免费开源
```
- **覆盖**：拾音 ✅ / PTT ✅ / 确认·中断 ✅ / 巡视 ⚠️（靠 AgentDeck 在手机或副屏上看）
- **风险**：SayAll Windows 是预览版、无签名、依赖 VB-CABLE、项目存活是变量
- **建议**：先用两周验证"我会不会真的养成开口的习惯"。这步错了，后面花 ¥1400 也白搭

### 路线 B：¥300，一步到位买"手持麦 + 确认键"

```
Ulanzi VibeKey (AU05)  ¥299
+ Wispr Flow Pro  ¥85/月
+ AgentDeck
```
- 相比 RC003 的优势：**开箱即用、有官方 Windows 配置软件**（Ulanzi Studio）、自带麦克风不用折腾 VB-CABLE、键数少反而更容易形成肌肉记忆
- 劣势：3 颗键比遥控器的整套导航键少，"巡视"能力弱；且它仍需依赖第三方语音内核

### 路线 C：¥1600，买行业最完整的形态

```
OpenAI Codex Micro  ¥1630
```
- 只在一种情况下值得：**你已经长期把 Codex 放进了 ChatGPT 桌面端生态**（否则 6 颗状态灯不亮，核心卖点作废）
- 你主要用 OpenCode / Codex，我认为对你性价比不高

---

## 六、我的判断与建议

1. **你的核心需求在目前已经有两个价格极端的交点了**：¥60（RC003）和 ¥299（VibeKey）。中间 ¥300~1600 这个区间是真空地带，没有产品。这说明市场还在早期，也说明**你现在入场的时间点很好**。

2. **别买只有单 PTT 的东西**（脚踏、Wispr Pedal）。它们解决的是第 2 层，而你的痛点在第 3、4 层（确认、撤销、巡视）。这也是为什么 Codex Micro 贵还卖爆——它是目前唯一同时做全四层的官方产品。

3. **第 4 层（多 agent 状态巡视）用软件免费补最划算**：AgentDeck 开源、支持 OpenCode、而且能把你的平板/手机变成状态监视器——这正好顺你的"屏幕只是展示界面"的思路。

4. **YOOOCLAW 继续留着，它和桌面方案互补不冲突**。桌面遥控器管"坐在工位上"，YOOOCLAW 管"离开工位 / 走路 / 开会 / 开车"。唯一缺的一环：目前没法用一个统一的 agent 状态盘把两边串起来，这是个可以自己搭的机会。

5. **关于"手持 + 平板/大屏"这个配置**：值得试。麦克风随身意味着姿态自由，屏幕只做展示和触摸确认，键盘彻底沦为 fallback。缺的不是硬件，是"让多个 agent 的状态离开终端窗口"——AgentDeck 就是干这个的。

6. **最大的变量是噪音环境下的识别率**。办公场景近距离小声说，RC003 的近场麦实测 OK；但如果周围吵，配一个无线领夹麦（很多还不支持 PTT，要注意挑有实体按键的）。

---

## 七、数据来源

- 少数派《60 元打造你的 vibe coding 神器：小米蓝牙遥控器 2 pro》（2026-09-22）
- 字节笔记本《无线麦 SayAll：把小米电视遥控器变成 Mac 的语音输入遥控器》
- Tom's Hardware / PCMag / BizStack / GC IoT：Codex Micro 多篇评测拆解
- codex.danielvaughan.com：《Physical Agent Control Surfaces》
- Ulanzi 官网 AU05 产品页 + 京东旗舰店 + BottleRocket 评测
- Elgato 官方 Explorer 文章《Foot Pedals for AI Prompting》
- BottleRocket《The Best Push-to-Talk Devices for Prompting AI》
- GitHub `puritysb/AgentDeck` + npm `@agentdeck/setup` + OSRTOS 项目页 + 新浪相关报道
- `getsayall/remote-mic-app-windows` 项目（kaiyuanbang 收录）
- 少数派 / 塔猴 / ReviewsTown / New Claw Times：YoooClaw C·ONE 多方评测
- CNBC The China Connection（2026-04-27）：中国 AI 硬件出货、EinClaw
- Android Headlines（IFA 2026）：Relay Q
- codepick.dev《Typeless 及替代者：5 款 AI 语音输入工具深度对比》
- dictationformac.com / hackup.ai / novavoice.app / willowvoice.com：多份 2026 语音输入横评
- joshwcomeau.com《Hands-Free Coding》+ talon.wiki：Talon + 眼动交互
- mic.flic.io、clawbuds.ai 官网

---

*注：本报告中的价格为调研当日公开信息，部分产品（如 Flic Mic、Relay Q）尚未定价或尚未发货，请以官方渠道为准。*
