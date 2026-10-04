# DEV.md — 开发计划与进度

> 本文档回答三个问题：**计划做什么、做到哪了、下一步干什么。每次开发会话后更新。**
> 架构定案与开发纪律在 [`AGENTS.md`](./AGENTS.md)（长期稳定，不随进度变化）。
>
> 最后更新：2026-10-02

---

## v1（当前目标）

**只做一件事：按住遥控器语音键说话 → 文字进 dsh 输入框（不自动发送）。**

链路：按住语音键 → local-mic 采集 + ATVV 会话 + 解码（输出 PCM16）→ `ws://127.0.0.1:8787` → 插件收 PCM16 → `remote.speech.transcribe()`（Host 侧 SenseVoice）→ `insertText()` 注入 → 用户手动触发发送。

| 组件 | 位置 | 状态 |
|---|---|---|
| 本机采集端 | `local-mic/` | ✅ 建成，proto 1，`--selftest` 三段全绿 |
| 线缆协议规范 | `docs/PROTOCOL.md` | ✅ 冻结（proto 1），评审 14 条闭合（非破坏性） |
| 诊断页 | `diagnostics/` | ✅ proto 1 接缝观察器（显示接缝消息 / 试听 / 在线契约） |
| **dsh 插件** | `dsh-plugin/` | ✅ 第 ① 步建成并验收入网：图标 + 点击浮层（点外面/Esc 关）、连 local-mic 显示状态、收 PCM16 组 WAV 可试听（2026-10-02） |

## v1.1（已规划，复活实时转写）

按住期间出字。三条前置：

1. 实测 RTF（推理耗时 ÷ 音频时长）——**不需要遥控器**，在 dsh 网页端用自带语音输入掐表即可
2. 确认 `insertText()` 可多次注入——每次重新 `captureInsertion()`，失败必须保留 pending 文本
3. 切段依据必须是 VAD 静音/能量，不能用固定时长——中文端到端模型对半截词会强行解码成"合理文字"

## v2+（已规划，现在不做）

- **审批控制**：确认 / 中断 / 撤销 agent 动作（`ctx.approval` seam；fail-closed 语义须单独论证）
- 状态巡视：多 agent 状态可见性
- 返回键 / 音量键映射（三键在 Windows 侧零事件，唯一可行解是提权注入，路线见 `docs/HARDWARE.md` 键位章）
- 可自定义 TV 键的利用

---

## 已完成

- [x] 需求确认：v1 = PTT 语音输入；形态 = dsh 插件 + 本机采集端（`local-mic`）；审批后置
- [x] dsh 插件体系调研：33 个 seam，语音与审批接入点已定位
- [x] ATVV 协议规格提取 + RC003 硬件能力边界调研（键位、音质、链路质量）
- [x] C# 采集端建成：单文件 exe（net48，托盘 + 主界面），免安装免提权
- [x] 真机验证：连接、ATVV 三特征齐全、GetCaps 往返成功、型号/固件/序列号/电量可读
- [x] **音频链路达标**：228 帧全 120 字节、零丢帧、8000 B/s（正好理论值）；用户实听合格
- [x] ADPCM 解码器双实现黄金向量锁定（C# `--selftest` 与 Node 侧 12/12 一致）
- [x] 线缆协议 proto 1 规范冻结（`docs/PROTOCOL.md`），评审 14 条全部闭合
- [x] 协议夹具 TS 侧补齐（`l2-protocol.test.mjs`，24 条）
- [x] F5 刷新拦截（2026-09-30）：诊断页 keydown 拦裸 F5（默认开）。
      **2026-10-02 复核更正**：实现一直只拦裸 F5（`diagnostics/js/app.js` 注释写明"带修饰键的是用户自己的操作"），
      早先记成"拦 F5/Ctrl+R"是笔误；插件侧口径见 [DSH-SEAMS §7.5](./docs/DSH-SEAMS.md)
- [x] 音频帧 kind `0x02` 接缝出口修复 + 二进制帧夹具补齐（2026-09-30）
- [x] 仓库采用 **GPL-3.0** 许可（LICENSE 已建，2026-09-30 用户拍板）
- [x] 仓库目录重命名对齐定义（`helper-win`→`local-mic`、`tools/harness`→`diagnostics`、`tools/contract`→`conformance`）
- [x] **修复 `proto_mismatch` 的 error 帧丢失**（2026-10-01）：发送是有界异步队列，而拒绝时立刻关 socket ⇒ error 没落地，
      客户端只看到 1006 且不知道为什么被踢（违反 `PROTOCOL.md` §6 的"先报错再关闭"）。
      改为 `WsServer.SendThenClose`（同步写，写完再关）。取证方式：裸 TCP 抓帧，修复前后各 3 次对照
- [x] 重构后回归验证（2026-10-01）：全量重编译 0 error、自检 12/25/19 全绿、Node 侧 46/46 全绿、
      对**真实 local-mic 进程**跑接缝契约 8/8 全绿（含 proto 不匹配的三个负例与 `write ⇒ op_not_supported` 保连）
- [x] **修复关调试日志时弹 explorer 0xc0000142**（2026-10-01）：低完整性下 `Process.Start("explorer.exe")`
      的子进程继承 Low IL、explorer 初始化即失败，且 Process.Start 本身成功 ⇒ try/catch 拦不住。
      `Shell.OpenFolder` 先查完整性级别，低完整性返回 false，主界面退化为只显示日志路径（PITFALLS #23）
- [x] **任务栏重建自愈**（2026-10-01）：`TrayApp.TaskbarWatcher` 监听系统广播的 `TaskbarCreated`
      ⇒ 重新注册图标。修的是"explorer 重启 / 我们开机自启抢在 explorer 前面 ⇒ 图标永久丢失"，
      而进程活着、WS 在听，用户只会以为托盘坏了（PITFALLS #25）
- [x] **🔴 "托盘不出图标"真因：exe 继承了目录的 Low 强制完整性标签**（2026-10-01）
      进程 IL = min(用户 IL, **文件标签**) ⇒ 双击 / Win+R / 任务管理器怎么启动都是 Low。
      explorer、SmartScreen、360 全是冤枉的（中途误判过，见 PITFALLS #27）。
      修复：`icacls "<仓库目录>" /setintegritylevel (OI)(CI)M /T` + 重新编译。真机已确认托盘正常（PITFALLS #26）
- [x] **排查代码清理**（2026-10-01）：删 `Motw.cs`、`TrayProbe.cs`、`--integrity` 入口、
      主窗口取证区与「解除锁定」按钮、`Integrity` 的父进程查询；保留 `TaskbarWatcher`（真 bug）、
      `RunWithoutTray` 兜底、`Shell.OpenFolder` 的 Low 保护、`ReportFatal`。
      清理后：全量重编译 0 error、自检 12/25/19、对真进程接缝契约 8/8
- [x] **诊断页内置进 local-mic**（2026-10-01，用户定案：内置 / 共用 WS 端口 / **磁盘优先 + 内嵌兜底**）：
      由 **WS 同一端口**提供（握手前按 `Upgrade: websocket` 分流）；托盘菜单开
      `http://127.0.0.1:<端口>/`，页面从 `location.host` 自推接缝地址（端口输入框退休）。
      来源策略：exe 向上 6 层找仓库的 `diagnostics/`，找到就**每次请求现读** ⇒ **改页面刷新浏览器即可，
      不必重编译**；找不到（发布出去的单文件 exe）才用 csproj 内嵌的那份，且**逐个文件各自回退**。
      `--diag-dir <目录>` 可显式指定；响应头 `X-Diag-Source: disk|embedded`、自检末段打印来源与目录
      ⇒ "改了页面没生效"一眼可判（现自检 12/25/19/5）。
      验证：5 个资源全 200 + MIME 正确 + `X-Diag-Source: disk`；改 `index.html` 加标记后刷新即见、
      还原即消失（免重编译实锤）；exe 拷到别处 ⇒ 5/5 全走内嵌；负例 404/403/405 与路径穿越仍全拦；
      WS 契约 3/3（hello⇒ready、proto 2⇒error 后断开、write⇒op_not_supported 保连）
- [x] **修复"状态显示 connected 但按钮不动"**（2026-10-01，Firefox 真机）：ready 的 15 秒超时原来从
      `new WebSocket()` 起算，Firefox 建连拖 20 秒+ ⇒ 超时先判死（catch 恢复了「连接」按钮），
      socket 随后迟到连上、状态打出 connected，但更新按钮的成功路径已走不到。
      修：超时改在 `onopen` 起算 + 按钮收敛到唯一真相源 `setConnUI()`（onOpen/onClose 驱动）+
      `settle` 只认第一次 + 判死尝试 `disconnect()` 收尸（防僵尸连接双份音频）；
      建连超 3 秒先提示一句（Bug 1662694），别让人干等。页面走磁盘那份，刷新浏览器即生效（PITFALLS #29）

- [x] **插件装入本机 DSH（2026-10-02）**：`dsh plugin --profile web add /workspace/dsh-remote-mic/plugin`
      ⇒ profile 记为 `link:`（`~/.dsh/profiles/web/package.json` 依赖 + `dsh.profile.bundles` 末尾追加
      `dsh-remote-mic-plugin`，`node_modules` 建同名软链 ⇒ 改插件源码不必重装）。
      安装输出正常（pnpm `Packages: -2` 只清陈旧条目，装后原有 5 个插件软链均仍可解析）；
      回滚 = `dsh plugin --profile web remove dsh-remote-mic-plugin`。
      实证三条（都不需要人按遥控器）：

      ① 组合正确：临时端口另起一个 `dsh web` 实例，启动清单里出现该 client 模块行
      `{"id":"dsh-remote-mic-plugin","url":"plugins/??dsh-remote-mic-plugin/client.js&rev=419ca220481f",…}`，启动日志无报错；
      ② bundle 可服务：该 URL 取回 HTTP 200 / 17203 B，含 `__ModuleLoader__`、`conversation.input.right`、
      `plugins.bundle.config`、`ws://127.0.0.1:<port>`，`node --check` 通过；
      ③ **运行中的 3080 实例同样直接取得到这个 bundle**（HTTP 200 / 19471 B，同上校验通过）
      ⇒ HMR 按 `package.json` 的 `dsh.profile.bundles` 变化**已热加载**：没重启、没断当前会话。
      ⚠️ 仍缺：人工在浏览器里确认图标出现 + 连上本机 local-mic（见「下一步」）。

- [x] **浮层交互改成点击语义 + 修「界面不刷新」（2026-10-02，用户反馈）**：
      原实现是悬停展开 / 移开延迟 180 ms 收 —— 但浮层是 root 的子节点且在图标正上方，
      鼠标往上移会落进浮层内部 ⇒ 永远等不到 `mouseleave`，表现为「浮层不消失」。
      改为：**点图标开/关，点浮层外关，Esc 关**，删掉全部 mouseenter/leave/focus/blur 逻辑；
      「点外面」监听从冒泡改**捕获阶段**（`pointerdown` + capture）——dsh 内部处理器若在冒泡阶段
      `stopPropagation`，冒泡监听收不到那一下。
      同时修掉一个更隐蔽的 bug：`Seam.emit` 原来把**同一个** `this.state` 引用交给订阅者，
      React 的 `useState` 用 `Object.is` 比较 ⇒ 直接 bail out，图标颜色与浮层字段根本不刷新
      （只有点开浮层时才顺带渲染一次，所以之前看着是「有时对有时不对」）。改为每次发一份新快照。
      用户回访说仍然「一直显示」，我误判成事件相位问题，加了一层**全屏透明 backdrop** 兜底 —— 结果更糟：
      热替换后这层 DOM 成了没人管的孤儿，**整个宿主页面都点不动了**（2026-10-02 用户实测）。
      已彻底删掉全屏层，关闭改为四条路径并用：点浮层外（document 捕获阶段的 `pointerdown` + `click`）/
      指针离开「图标 + 浮层」400 ms 宽限 / Esc / 再点一次图标；浮层右下角留一行灰字
      「点外面 / 移开鼠标 / Esc 关闭」，兼作版本标记。

      ⇒ **硬规则：客户端插件里绝不放全屏点击拦截层**（backdrop / 遮罩）——见 DSH-SEAMS §7.8。
      ⇒ 事故处置：当场 `dsh plugin --profile web remove dsh-remote-mic-plugin` 把插件摘掉，
        profile 已回到装机前的 8 bundle 原状，用户刷新页面即恢复可用；修好的源码留在工作区，
        随时一条命令重装（见「下一步」）。

- [x] **插件目录改名 `plugin/` → `dsh-plugin/`（2026-10-02）**：项目名即 `dsh-remote-mic`，插件归到
      `dsh-remote-mic/dsh-plugin`。改名会断 profile 里的 `link:`，故同步 `remove` + `add` 新路径
      （profile 清单装前已备份）。实证：软链指向新路径、线上 bundle HTTP 200、接线测试 15/15；旧 `plugin/` 已不存在。
- [x] **浮层「关不掉」真因定位与修复（2026-10-02）**：真因是 `var open = React.useState(false)` ——
      拿到的是**数组**（恒为真）⇒ 浮层一挂载就永远渲染；而 `setOpen` **从未定义** ⇒ 所有关闭路径
      （点外面 / Esc / 移开鼠标 / 以及中途加的那层全屏遮罩）都在调用一个不存在的函数，异常被吞、静默失败。
      前几轮误判为"悬停语义 / 事件相位 / 热替换孤儿"，都没查状态对本身 —— 教训见「待办 1」那份修改意见。
      修复 = 解构状态对 + 按方案 A 换成框架原语（`useAnchoredPosition` + `useDismissOnOutsidePointer` +
      `createPortal(document.body)` + Esc），删掉自写的 document 监听与"移开鼠标即关"；补一条无浏览器
      接线测试（15 条断言，喂**原始提交版本**会 FAIL ①②，证明它能抓到这类 bug）。
- [x] **采集路径打通（2026-10-02）**：按 PROTOCOL §10 解析 1 字节 `kind=0x02` + PCM16 载荷、
      `start..end` 之间才累积（不变量 7）、时长一律按 32 B/ms 从**字节数**换算；收段时补 44 字节 WAV 头
      ⇒ 浮层「上次录音 x.x s · y KB」+「试听」（用户实听确认）。`pcmToWav` 另有 17 条纯函数断言

- [x] **文档结构整理：中间态过程稿独立成区（2026-10-02，用户指示）**：
      `docs/research/` → **`docs/notes/`**（语义覆盖调研 / 修改意见 / 评审 / 方案）；4 份老稿按规范改名
      `<主题>-<类型>-<日期>.md`；6 份全部补**状态头**（📝 待落定 / ✅ 已落定 / 🗑 已废弃 + 落点）。
      新增 [`docs/README.md`](./docs/README.md)（文档索引：哪份是真源、哪份是过程稿）与
      [`docs/notes/README.md`](./docs/notes/README.md)（生命周期四步 + 状态块模板 + 清单）。
      [`AGENTS.md`](./AGENTS.md) 加「文档」纪律第 15 条（**过程稿必须登记落点，未登记不得当结论引用**）
      + 文档地图两行；引用同步 5 处（AGENTS / DEV / REFERENCES / DSH-SEAMS / PROTOCOL）。
      新增 [`scripts/check-doc-links.mjs`](./scripts/check-doc-links.mjs)：全仓 md 断链体检，首次即跑出 2 条 ——
      ① `docs/MIGRATION.md` 从未存在（`git log --all` 无记录）⇒ 已在评审稿里更正并说明迁移早已完成；
      ② `local-mic/README.md` 的相对链接多一层 ⇒ 属 Windows 侧维护的文件，记入脚本 `KNOWN_BROKEN`，**只报未改**。

## 下一步（按顺序）

1. **dsh 插件侧：第 ① 步已收官，下一步走 ② 转写**（local-mic 与诊断页均已就绪）。
   重装 = `dsh plugin --profile web add /workspace/dsh-remote-mic/dsh-plugin`（装前备份 profile 的
   `package.json` / `pnpm-lock.yaml`）；改完让用户**硬刷新**页面再验。
   接线已定案并有本机实证，见 [`docs/DSH-SEAMS.md` §7](./docs/DSH-SEAMS.md)（2026-10-01）：
   - **纯 client 插件**，消费已被官方 bundle 挂好的 `remote.speech`（不 `$mount`、不写 Host 部分）
   - UI 挂 **`conversation.input.right`**（`list` 槽，须给 `id`）——**不是** `activity`（`single`，官方麦克风已占用）
   - 两个入口语义不同：官方麦克风=软件触发，我们的=**硬件 PTT**（~5.7 s 窗口）⇒ 视觉要能区分
   - F5 拦截**由详情页开关控制**，随协议 §8.1 的 `state` 取值边沿自动开合（`connected` 开 / 其它关）
   - 打包：`dsh.bundle.patch` 必需；`dsh plugin --profile desktop add <本地路径>` 迭代
   - 三步走，每步可独立验证：**① 挂上 + 连 WS + 显示状态**（✅ 2026-10-02 人工验收通过：点图标开浮层、
     点外面 / Esc 关；按住遥控器说话 → 显示「上次录音 x.x s · y KB」→「试听」能听到原话）
     → ② 接 `transcribe`（⚠️ 本机被两个前置卡住，见「待办」与「尚未验证」）→ ③ 接 `insertText`
   - ✅ **① 于 2026-10-04 真机结项**（修订版插件，遥控器在手，非本机夹具）：按住语音键说话 → 松开 →
     「上次录音 x.x s · y KB」有数、点「试听」听到原话 ⇒ **客户端半边闭环成立**
     （`kind=0x02` 字节流 → 补 44 字节 WAV 头 → 可播放）。同时**反证两条接缝事实**：
     图标外圈脉冲**只在按住期间闪、松手即停** —— 说明 `capture{start,end}` 在真机上**成对到达**
     （§8.2.5），且不变量 2 的收尾窗口生效（否则末尾丢半字、且脉冲不灭）。
     ⚠️ 到此为止**不含**第②步转写，也**不等于** local-mic 侧的 `--record` 闭环（见「尚未验证」表）。
   - ✅ 刷新键拦截已实现（2026-10-02）：只拦**裸 F5**，`Ctrl+R` / `Ctrl+F5` / `Shift+F5` 放行给用户刷新；
     完整口径见 [DSH-SEAMS §7.5](./docs/DSH-SEAMS.md)（⚠️ 手动路径已真机验；**自动路径的「开」方向与
     WS 断开时的交还已于 2026-10-04 真机验**，见「已关闭」；**「遥控器断开而 WS 在线」的关方向仍未验**，
     挂在「尚未验证」表——遥控器已在手，这是当前最便宜的一条待验项）
   - ✅ 迭代实测（2026-10-02）：直接改 `dsh-plugin/lib/client.js`，**宿主侧一定会热推送** ——
     `@deepseek-ai/dsh-client-hmr`（dsh-web-app 挂载）每 500 ms stat 轮询每个客户端 bundle，
     变了就 `clientModules.rebuilt(id)`，再经 `/plugins/events`（SSE，**不需要鉴权**）把新 graph
     推给浏览器。实测 rev `419ca220481f` → `218c97666f39`，同一时刻旧 rev 立即 404。
     ⇒ 迭代循环 = 改文件 → **刷新页面**。宿主侧 100% 会 rebuilt 并推送（实测抓到 `rebuilt` 帧），
     但浏览器侧是否就地换上未稳定复现（2026-10-02：用户页面明显还在跑旧 bundle）⇒ **以硬刷新为准**，
     别指望不刷新。查当前 rev：`curl -sN http://127.0.0.1:3080/plugins/events | head -c 2000`
   - ⚠️ 混合内容：GUI 若走 https（本机还挂了 `dsh.app.bulabula.space`），浏览器会拦 `ws://127.0.0.1:8787`，
     插件会静默连不上（DSH-SEAMS §7.7）
2. **取证收尾**（需人工按遥控器）：
   - `local-mic.exe --record capture`（按住语音键说一句 → 松手）
   - 校验 `capture.txt` 帧数/时长，`capture.wav` 试听
   - `node diagnostics/decode-bin.mjs capture.bin` 与 C# 输出**逐字节比对**
3. **端到端联调**：按住遥控器 → 文字进输入框 → 手动触发发送（松手不自动发）
4. **修 `TrayApp` 的 GDI 句柄泄漏**（2026-10-01 顺带发现，不阻塞 v1）：
   `RefreshUi()` 每次都 `MakeIcon(c)` 新建一个 `Bitmap` + `Icon.FromHandle`，
   并 `KeepAlive.Add(...)` **永久保留、从不移除** ⇒ 每一次状态 / 设备 / 电量变化泄漏一个 GDI 句柄。
   托盘程序是常驻的，长期跑会缓慢累积（GDI 对象默认上限 10 000）。
   现状：无功能影响，纯泄漏。修法二选一 ——① 按颜色缓存图标复用，别每次重画；
   ② 用 `new Icon(icon, icon.Size)` 克隆而不是 `FromHandle`，且只在颜色变化时重建。
   **别删 `KeepAlive` 这个静态列表**（`Icon.FromHandle` 的句柄依赖原 `Bitmap` 存活，
   提前回收会让图标变黑——那是 #24 之前踩过的坑，注释已标）

## 待办（2026-10-02 立项，均未开工）

### 待办 1 · （✅ 已完成 2026-10-02）插件改动的「事前辩证」纪律 → 已落进插件开发手册

**为什么**：2026-10-02 的两次事故都不是能力问题，而是**改之前没有取证、没有评估失败半径**：
① 自加一层全屏透明 backdrop 想"确保点外面能关"，结果把宿主页面点死；
② `var open = React.useState(false)` 未解构（拿到数组 ⇒ 恒真、`setOpen` 未定义），
浮层永远关不掉，我连续三轮在错误的假设上改代码（悬停语义 → 事件相位 → 热替换孤儿）。
**交付（已完成）**：条文已按用户批准写入
[`docs/DSH-SEAMS.md` §7.9「插件改动前置清单」](./docs/DSH-SEAMS.md)；同期一并纠正了 §7.5（F5 拦截口径）
与 §7.8 的"替代做法"（改为框架原语的方案 A）。出处与完整复盘：
[`docs/notes/插件热加载纪律-修改意见-2026-10-02.md`](./docs/notes/插件热加载纪律-修改意见-2026-10-02.md)。
**同日对齐的旧文案**：`AGENTS.md` §3 坑速查、本文件 §下一步 的 F5 两处、本文件 2026-09-30 那条 F5 记录。

### 待办 2 · 架构解耦：去掉**插件里的单设备假设**（分析已定调 2026-10-02，待实施）

**目标**：现在只支持 RC003-MS，将来要接其它同类外设（蓝牙耳机 / 脚踏开关 / 手机 PTT / 键盘热键等），
现在先把**抽象**做对，而不是等到第二个设备时返工。**代码未动。**

**分层盘点**（按 AGENTS.md 定案 A1/A4）：

| 层 | 现状 | 是否已成设备无关 |
|---|---|---|
| 设备层（BLE / ATVV / 按键 / 编解码） | 全在 `local-mic` | ✅ 本来就在 Windows 端，接缝上不出现硬件词汇 |
| 接缝层（proto 1） | 文本事件 + `capture{phase,reason,source}` + 二进制 `kind=0x02` | ✅ 已设备无关；且不变量 1 把「设备自门控」与「local-mic 主动丢弃」压成**同一形状** |
| 插件层（本仓库 `dsh-plugin/`） | 默认端口 8787、`Seam` 模块级单例、文案写"遥控器"、图标=遥控器造型 | ⚠️ **风险全部在这里** |

**⇒ 结论：要解耦的不是协议，是插件里的单设备假设。**

**能力描述：结论是「暂不加」，理由是没有消费者。**

复核修正了原分析的一处过保守判断：原文写"要在接缝加字段 ⇒ 必须先改规范并抬 `proto`"。
对 `ready.caps` **不成立** —— `PROTOCOL.md` §5 规定 `caps` 只增，§6 明文分工（`proto` 管破坏性 /
`caps` 管加功能、老客户端忽略不认识项），不变量 8 就是这条的地基。
**加 cap 项是非破坏性的，不抬 `proto`。**

但真正的问题不是"怎么加"而是"要不要加"。逐项查插件有没有真实需求（代码实测）：

| 原设想的"能力差异" | 插件是否消费 | 证据 |
|---|---|---|
| 按住型 PTT vs 常采 | ❌ 不需要 | 不变量 1 已把两类设备压成同一接缝形状，插件只认 `start` / `end` |
| 典型时长 | ❌ 不需要 | 时长由 `capture` 边界 + 字节数实测（浮层的「本段已收」/「上次录音」），声明反而多一处会漂的地方 |
| 是否带电平 | ❌ 不需要 | 插件**当前零电平 UI**（P1 未做） |
| 是否可中断 | ❌ 不需要 | `reason` 在 `dsh-plugin/lib/client.js` 里**零命中** |

⇒ **第二条真实设备出现之前，能力描述没有消费者。** 协议自己否决过一次没有消费者的字段：
`hello.caps` 于 2026-09-29 删除，理由逐字是"它没有任何消费场景，存在的唯一理由是跟 `ready.caps` 对称"（§6 注）。
⇒ 原分析的问题 b)（是否先写能力扩展草案）**结论：不写**。改为登记**触发条件**：
出现第二台设备且**实测出行为差异**时才立项；届时优先走 `caps`（不抬 `proto`）。

**新发现：多源时 F5 拦截规则会自相矛盾**（原分析没有）

现规则是 `state === 'connected'` ⇒ 开拦截（`client.js` 的 `state` 分支），单源无歧义。双源时：
A 连上 ⇒ 开、B 断开 ⇒ 关 —— **后到者决定，安全方向错了**（本该"还有能用的源就保持"）。
正确规则是聚合的：**任一源 `connected` ⇒ 开；全部非 `connected` ⇒ 关**。
这是几行逻辑，但**必须先定规则**再实现，否则第二台设备到来时会以 bug 形式暴露。
另：F5 拦截是**浏览器级**概念（不是设备概念），挂在插件上是合法的；它拿"连接事实"当驱动量，多源时必须聚合。

**耦合点精确定位**（`dsh-plugin/lib/client.js` 实测，按符号而非行号记，免得随改动失效）：

| # | 位置 | 耦合内容 | 成本 |
|---|---|---|---|
| C1 | `var seam = new Seam()`（模块级） | 单例：`registerUi` 里 `connect()`、`apply` 的 disposer 里 `dispose()` ⇒ 结构上假设"整页只有一个输入源" | 小 |
| C2 | `Seam` 构造函数里的 `this.port = readPort()` | 构造函数自己伸手读全局 localStorage key ⇒ `Seam` 不是"一个连接"的纯对象 | ~10 行 |
| C3 | `Seam` 的 `state` | 单设备快照（`device` / `capturing` / `lastClip` 全单数） | 与 C1 同批 |
| C4 | `installRefreshKeyGuard()` + `window` 级 keydown | 一个全局监听，驱动量是单例的 `interceptRefresh` | 见上，先定规则 |
| C5 | 用户可见文案 / 图标 | 图标注释「画成遥控器」、`aria-label`「遥控器语音输入」、面板标题、「遥控器就绪」、「没带遥控器时」 | **~10 行** |
| C6 | `capture.source` 未消费 | `capture` 只读 `phase`；`device.model` 存了不用（仅当 `name` 兜底） | ~15 行 |

**C6 是最值得先做的一步**：热键 / 脚踏这类源**根本没有 `device` 消息可发**（§7 的 `device` 是可选的设备身份），
所以 `source` 是它们**唯一的身份来源**。显示优先级定为：`device.name` → `source`（映射友好名，未知值原样或通用兜底）
→ 中性「语音输入」。这纯属"仅用于 UI 标注"，不碰逻辑分支，不违 §8.2。

**命名**：包名 `dsh-remote-mic-plugin` 已表达"远程麦克风"而非 RC003，**建议不动** ——
改它要动 profile 安装记录与设置槽 key，收益为零。**要改的只有用户可见文案。**

**定案范围（2026-10-02 用户批准，待实施）**：

- **S1** `Seam(port)` 构造注入，去掉构造函数伸手读 key —— 唯一必要、零行为变更的接口留口（~10 行）
- **S2** 消费 `capture.source` 作显示兜底（见 C6，~15 行）
- **S3** 文案去设备化：默认中性 + 优先 `device.name`（~10 行）
- **S4** **只登记**能力描述的触发条件，不加 `caps`、不写草案
- **S5** **只写下**多源聚合规则与多源 UI 模型，不实现

**顺序（2026-10-02 用户批准）**：先更新本节 → 看过后再决定是否开工改代码。

**坑（修正后）**：① 不要为"将来"把 v1 拖住——解耦只做**接口与命名**层面，不提前实现第二设备；
② 抬 `proto` 只看**破坏性**：只增字段 / 只增 `caps` 项非破坏、不抬 `proto`，
   但**任何接缝字段变动仍必须先改 `PROTOCOL.md`**，不许只改代码（纪律 §4）；
③ **不得用 `source` 冒充能力描述**（协议明令它不得参与逻辑分支）；
④ 多源规则**先定后写**，别等第二台设备以 bug 形式暴露。

**待落定的长期结论**：若"能力按需、不预防性加 `caps`"被判为长期纪律，`AGENTS.md` §1 值得加一条定案（**未改，等你点名**）。

### 待办 3 · 插件设计定调（显示 / 解析位置 / 注入逻辑 / 边界）

**交付**：调研已成文 →
[`docs/notes/插件设计待定问题-调研-2026-10-02.md`](./docs/notes/插件设计待定问题-调研-2026-10-02.md)，
逐条列出"必须由你定调的问题 + 推荐选项 + 潜在坑"（UI 呈现、音频在哪解析、转写 seam 怎么补、
文字怎么进输入框、F5 拦截、多标签页、性能节流、可测试性、分发）。
**待你定**：过一遍逐条拍板；**定调前不开发这部分**。

### 待办 4 · `diagnostics/` 归谁：是否搬进 `local-mic/`（2026-10-02 挂起，等与 Windows 侧商量）

**问题**：用户提出"`diagnostics` 实际服务于 local-mic，是否挪到 `local-mic/` 下更合适"。

**盘点（证据，2026-10-02）**：它有三个消费者，**不是 local-mic 的私有资产**——

1. **local-mic 运行时**：`local-mic/local-mic.csproj` 五条 `<EmbeddedResource Include="..\diagnostics\…" />`；
   `local-mic/src/DiagWeb.cs` 的 `DirName = "diagnostics"` 从 exe 向上搜索（**磁盘优先 / 内嵌兜底**）。
2. **仓库级契约**：`conformance/gen-adpcm-vectors.mjs` 直接 `import` 它的 `js/adpcm.js`（黄金向量的单一数据源）。
3. **接缝的独立观察面**：`probe-ws.mjs` / `decode-bin.mjs` / `test/l0|l1|l2` —— 插件侧排查同样用它；
   `docs/PROTOCOL.md` 的"生效范围"把 `diagnostics/` 与生产侧、插件**并列**。

**搬动代价**：改 csproj 的 5 条内嵌路径 + `DiagWeb.cs` 的搜索逻辑（**这两项都在 Windows 侧**）
+ conformance 的 import + 约 12 处文档引用；还要重编译、跑 `--selftest`、真机验两级回退。
收益只是"目录语义更贴切"。

**当前决定（2026-10-02 用户）**：**暂不搬**，挂在这里等与 Windows 侧一起定。
若最终要搬：本仓库这侧由我改（conformance + 文档），csproj / `DiagWeb.cs` 需 Windows 侧配合。

## 尚未验证 / 未定项

| 级别 | 项 |
|---|---|
| 🟡 | **F5 拦截自动路径的「关」方向（设备侧）**：2026-10-04 验到的是 **WS 侧** —— 改 local-mic 端口 ⇒ 插件秒断、开关同步关（`onclose` 路径）、改回 ⇒ 约 1 s 重连后开关同步开（`state` 边沿 `connected`）。**未验**：遥控器**自身**断开 / 关机而 **WS 仍在线**时，local-mic 发 `state: disconnected` ⇒ 插件应据 `state` 边沿拨回关 —— 这才是 §7.5【遥控器断开】的原始场景 |
| 🔴 | **local-mic 侧 `--record` 闭环**（`capture.txt` 帧数 / 时长 + `capture.wav` 试听 + 与 C# 逐字节比对）——待人工在 **Windows 侧**按遥控器。⚠️ 与「插件侧采集闭环」是两件事，后者 2026-10-04 已真机结项 |
| 🟡 | **混合内容（2026-10-02 复核后降级）**：只有把 GUI 直接曝成 **https** 时，浏览器才会拦掉 `ws://127.0.0.1`、插件静默失效。实际拓扑是 SSH 隧道访问 `http://127.0.0.1:3080`（http + 浏览器本机）⇒ **当前形态不受影响**（2026-10-02 真机连通）；将来要走 https，须给 local-mic 加 wss + 自签证书（DSH-SEAMS §7.7） |
| 🟡 | 原始电平（约 −30.6 dBFS）直接喂 ASR 是否够用——决定要不要加会话内定长增益（若加属破坏性变更，须抬 proto） |
| 🟡 | C# 与 Node 解码器对**真实录音**输出的逐字节一致性（黄金向量已 12/12，真机数据未比） |
| 🟡 | RTF 实测——v1.1 复活实时转写的前提，不需要遥控器 |

**部署 / 开发拓扑（2026-10-02 实测确认，插件已连通）**

- dsh 跑在 N100 节点的容器里；台式 Windows 用 **SSH 隧道**代理 3080 访问 ⇒ 浏览器地址栏是 `http://127.0.0.1:3080`。
- 插件连的 `ws://127.0.0.1:8787` 是**浏览器的回环 = Windows 本机**，不经隧道、不经容器；local-mic 就在那儿。
- 两半代码分两地开发：`local-mic/` 在 Windows（另一 agent 开发，走 git 推远程仓），`dsh-plugin/` 在本容器
  （`link:` 装进 web profile）。⇒ **跨机变更只认 git**：容器里改 `dsh-plugin/` 立即生效，但不进 git 就等于只在容器里；
  接缝协议要动，必须两边同时改 `docs/PROTOCOL.md` 并抬 `proto` 号。
- 插件端口存在**浏览器 localStorage**（按 origin 隔离）：换浏览器、或换 origin（http ↔ https）都要重设。
- 容器侧碰不到真 local-mic：真机契约（`diagnostics/probe-ws.mjs`、诊断页在线契约、`--record`）只能在 Windows 侧跑，
  容器侧只能跑 L0/L1/L2 离线夹具；要让容器也能验真机接缝，需 Windows 起 `ssh -R 8787:127.0.0.1:8787 <N100>`。

**已关闭（2026-10-04 真机）**：

- ~~F5 拦截的**开**方向 + WS 断开时的交还~~ —— 改 local-mic 监听端口 ⇒ 插件**秒掉线**、刷新屏蔽开关同步关
  （`onclose` 路径，[`dsh-plugin/lib/client.js`](./dsh-plugin/lib/client.js) 的注释即此意「接缝断了 ⇒ 把刷新键交还用户」）；
  改回 ⇒ **约 1 秒**重连、开关同步开（`state` 边沿 `connected`）。
  ⚠️ **只关了 WS 侧**：「遥控器自身断开而 WS 在线」那条仍未验，见「尚未验证」表。

- ~~「图标一直闪」是否为 bug~~ —— **不是 bug，是设计行为**：外圈脉冲（`dsh-plugin/lib/client.js` 的 `dsh-rm-pulse`）
  只绑 `data-rec`（= `state.capturing`）⇒ 只在一次采集窗口内存在。它是「硬件 PTT 正在进行」的**唯一提示**
  （松手才出字，不闪就无从知道按下了）。**判据留档，将来再看到闪烁先分流、别当故障**：
  **脉冲环不停** = `capturing` 卡住（`capture{end}` 未到 ⇒ 看面板「本段已收」在没按键时是否仍在涨）；
  **底色灰 / 橙 / 绿跳变** = WS 反复重连（看面板「状态」行）—— 两者视觉不同。
- ~~插件侧采集闭环是否真机成立~~ —— 见「下一步」① 结项。**未覆盖**：第②步转写、local-mic 侧 `--record`。

**已关闭（2026-10-01 本机实证）**：

- ~~服务端是否已安装并准备好 speech 语音包~~ —— 已就位：`~/.dsh/speech-to-text/sensevoice/models/`
  下 `model.int8.onnx` 228 MB + `tokens.txt`，另有 `silero_vad.onnx`
- ~~Client 侧插件的 bundle / 挂载写法~~ —— 纯 client 包 + `dsh plugin --profile desktop add <本地路径>`，
  必须声明 `dsh.bundle.patch`（否则 `not-bundle` 拒收）；写法见 DSH-SEAMS §7.6
