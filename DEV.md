# DEV.md — 开发计划与进度

> 本文档回答三个问题：**计划做什么、做到哪了、下一步干什么。每次开发会话后更新。**
> 架构定案与开发纪律在 [`AGENTS.md`](./AGENTS.md)（长期稳定，不随进度变化）。
>
> 最后更新：2026-10-01

---

## v1（当前目标）

**只做一件事：按住遥控器语音键说话 → 文字进 dsh 输入框（不自动发送）。**

链路：按住语音键 → local-mic 采集 + ATVV 会话 + 解码（输出 PCM16）→ `ws://127.0.0.1:8787` → 插件收 PCM16 → `remote.speech.transcribe()`（Host 侧 SenseVoice）→ `insertText()` 注入 → 用户手动触发发送。

| 组件 | 位置 | 状态 |
|---|---|---|
| 本机采集端 | `local-mic/` | ✅ 建成，proto 1，`--selftest` 三段全绿 |
| 线缆协议规范 | `docs/PROTOCOL.md` | ✅ 冻结（proto 1），评审 14 条闭合（非破坏性） |
| 诊断页 | `diagnostics/` | ✅ proto 1 接缝观察器（显示接缝消息 / 试听 / 在线契约） |
| **dsh 插件** | 待建 | ⬜ **未开工——v1 最后一环** |

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
- [x] F5 刷新拦截（2026-09-30）：诊断页 keydown 拦 F5/Ctrl+R（默认开）
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

## 下一步（按顺序）

1. **写 dsh 插件侧** ← 当前这一步，local-mic 与诊断页均已就绪。
   按 `docs/DSH-SEAMS.md` 的实证 API 接线，直接吃服务端已有的 `sensevoice-local`；
   必须带 F5/Ctrl+R 拦截（见 AGENTS.md §3 坑速查）
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

## 尚未验证 / 未定项

| 级别 | 项 |
|---|---|
| 🔴 | 端到端录音闭环（`--record`）——待人工按遥控器 |
| 🟡 | 原始电平（约 −30.6 dBFS）直接喂 ASR 是否够用——决定要不要加会话内定长增益（若加属破坏性变更，须抬 proto） |
| 🟡 | C# 与 Node 解码器对**真实录音**输出的逐字节一致性（黄金向量已 12/12，真机数据未比） |
| 🟡 | 服务端是否已安装并准备好 speech 语音包（`sensevoice-local`） |
| 🟡 | Client 侧插件的 bundle / 挂载写法（照 `voice-input-bundle` 模板，需读其 `cordis.patch.yml`） |
| 🟡 | RTF 实测——v1.1 复活实时转写的前提，不需要遥控器 |
