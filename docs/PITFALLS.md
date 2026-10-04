# PITFALLS.md — 已知坑与排查记录

> **踩坑、调试、读日志时查的坑册。** 一句话规则版见 [`../AGENTS.md`](../AGENTS.md) §3；
> 硬件与协议规格见 [`HARDWARE.md`](./HARDWARE.md)；本机环境坑见本文档 §2。
> 本文件随每次真机调试增长——**新增坑请补进表格，并判断是否值得在 AGENTS.md 提炼一条规则**。

---

## 1. 已知坑清单

| # | 坑 | 解法 | 细节 |
|---|---|---|---|
| 1 | **首次配对只有 55% 链路送达率** | **删掉配对重新配对** → 恢复 98.7%。开箱第一件事就做，否则会误判成代码 bug | — |
| 2 | **固件音频窗口只有约 5.7 秒**（不是 60 秒） | 每 2.5 s 发 `0x0E <session_id>` 续期 | `HARDWARE.md` §2.6-4 |
| 3 | **连接初期会收到遗留通知**（实测刚连上就收到 `04 03 02 11`） | 开始前静默 800 ms / 用 `armed` 标志把关，否则误判"用户已按下"并发 MicOpen | `HARDWARE.md` §2.6-5 |
| 4 | **DIS 型号是 `RC003`，不带 `-MS`** | 按**前缀**匹配，别写死全等——否则静默退化成未知设备 | `HARDWARE.md` §1 |
| 5 | **一个设备同时只能有一个 GATT 客户端** | 调试时**不要开两个程序连同一个遥控器**（local-mic 与 Python 探针、诊断页都会互抢），旁路抓包会被挡（`FromIdAsync` code-0） | — |
| 6 | **预按住键（抢跑）会毁掉这次录音** | 按下前的 `0x00` 必丢、按下前的音频不计入、抢跑必须有 UI 提示 | `HARDWARE.md` §2.6-7 |
| 7 | **解码后音量偏小** | ⚠ **该经验值只适用于本地试听，不是 ASR 结论**。实测 **×4.28** 的目标是"按 RMS 归一到 −18 dBFS"，而 −18 dBFS 是音频制作的常用电平、给**人耳听**定的，不是给识别引擎定的。**原始电平约 −30.6 dBFS 喂 ASR 够不够用尚未验证**。协议侧 v1 不做增益；将来若加属**破坏性变更、必须抬 proto** | `PROTOCOL.md` §11.1 |
| 8 | **`insertText` 有选区版本校验** | 长语音 + 本地 ASR 秒级耗时，span 可能失效 → 失败时保留文字 | `DSH-SEAMS.md` §2 |
| 9 | **语音链路全是 `experimental-*` 包** | API 可能变，做好隔离 | — |
| 10 | **连接必须串行化** | 定时器、浏览器上线、启动踢一脚、用户点自检都可能同时触发连接。**别用 `_busy` 标志直接挡掉后来者并报错**，要让后来者排队（`SemaphoreSlim`）。 | — |
| 11 | **无客户端时探得太稀** | 无客户端时别降频到约 30 秒一次，否则开机自启的用户会盯着灰图标怀疑它坏了 ⇒ **启动后立刻探一次** | — |
| 12 | **读 DIS / 电量失败不能拖垮音频链路** | 这些是锦上添花，整段包 try/catch，读不到显示"—"或"未验证"，绝不因此让连接失败 | — |
| 13 | **别引 UI 的 NuGet 包** | `DataVisualization.Charting` 会附带 DLL，破坏"单文件 exe" ⇒ 波形与电量条自绘（`Widgets.cs`） | — |
| 14 | **`HttpListener` 要管理员权限** | 走 http.sys，绑定前缀需 `netsh http add urlacl`，与"免安装免提权"冲突 ⇒ 用 `TcpListener` 自实现 RFC 6455 子集 | — |
| 15 | **已配对设备不再广播** | 设备发现**读注册表**（`BTHLE\Dev_*`），不要扫描，扫描必然一无所获 | `HARDWARE.md` §3 |
| 16 | **`FromBluetoothAddressAsync` 返回不代表已连上** | 必须等 `ConnectionStatusChanged` 到 Connected | — |
| 17 | **本机代理会劫持 localhost** | `curl` 加 `--noproxy '*'`；Python 客户端设 `no_proxy=localhost,127.0.0.1` | §2 |
| 18 | **Windows 输入法相关的坑** | 若走全局 SendInput 路线会遇到 IME / UIAccess 问题；**v1 走 dsh 输入框注入可规避** | — |
| 19 | **🔴 语音键 = F5，浏览器里按下即刷新页面** | 页面内 `keydown` 对 `F5`/`Ctrl+R`/`BrowserRefresh` 调 `preventDefault()`；keydown 取证写 sessionStorage（页面重载会清空内存日志） | `HARDWARE.md` §4.1.1 |
| 20 | **音频帧漏加 kind `0x02`** | kind 若由调用方自己拼，容易漏掉一个字节，表现为"短消息全对、音频全错"。修法：在**接缝出口**（`WsServer.BuildAudioPayload`）加，调用方只管 PCM | — |
| 21 | **🔴 异步发送队列 + 立刻关 socket ⇒ error 帧胎死腹中** | 拒绝时对端**必须先收到 `error` 再断**（`PROTOCOL.md` §6）。而 `WsServer.Send` 只入队，真正的 `Stream.Write` 在另一个线程的排空任务里（这样设计是为了不阻塞 BLE 派发线程），紧跟着 `CloseClient` 就把 socket 拆了 ⇒ error 基本没机会落地，客户端只看到 WebSocket **1006**，**永远不知道自己为什么被踢**。拒绝对端时一律用 `SendThenClose`（同步写，写完再关），不要 `Send` + `CloseClient` | — |
| 22 | **低完整性进程拿不到托盘图标** | 进程 IL 低于 Medium 时 `Shell_NotifyIcon(NIM_ADD)` 返回 false、`GetLastError=5` ⇒ 没有托盘图标、而主窗口只能从托盘打开 ⇒ 用户看到"双击了，什么都没有"。现由 `Integrity.TrayIconUnavailable` 检测并走 `TrayApp.RunWithoutTray(reason)`：直接开主窗口 + **关窗即退出**（否则会变成无图标无窗口的隐形进程）。**成因多半不是"启动方式被沙箱化"，而是 exe 继承了目录的 Low 强制标签 —— 见 #26**，别去怪 explorer 或安全软件 | — |
| 24 | **别用"实测 NIM_ADD"当托盘判据（试过，已回退）** | 2026-10-01 曾加 `TrayProbe.TryRegister()` 真发一次 `NIM_ADD` 来决定要不要兜底，理由是"完整性正常也可能注册不上（explorer 未就绪）"。**这个理由成立但结论有害**：那种情形下正确做法是等 `TaskbarCreated` 补注册（#25），而实测失败会把程序推进"关窗即退出"模式 —— 用户关一下窗口程序就没了，比没有图标更糟。判据回到 `Integrity`（Low IL 才必然无图标） | — |
| 25 | **任务栏重建后图标不会自己回来** | explorer 重启、或我们开机自启时抢在 explorer 前面起来，都会让 `NIM_ADD` 打在空处，之后图标永不出现（进程却活着、WS 在听）。`TrayApp` 内挂 `TaskbarWatcher`（`NativeWindow` 监听系统广播的 `TaskbarCreated` 消息），收到就 `Visible=false → true` 重新注册 | — |
| 23 | **低完整性下拉不起 explorer.exe（0xc0000142）** | `Process.Start("explorer.exe", dir)` 的子进程**继承 Low IL**，而 explorer 在 Low IL 下初始化即失败，弹「explorer.exe - 应用程序错误 0xc0000142」。最阴险的是 **Process.Start 本身成功、崩的是子进程 ⇒ try/catch 拦不住**（2026-10-01 真机截图）。`Shell.OpenFolder` 先查 `Integrity.TrayIconUnavailable`，低完整性直接返回 false，调用方退化成只显示路径 | — |
| 26 | **🔴 进程 IL = min(用户 IL, exe 文件的强制完整性标签)** | 2026-10-01 真机：双击 / Win+R / 任务管理器**全都**启动出 Low 进程，托盘不出、explorer 子进程崩（#22 #23 一起发作）。查了一圈 explorer、SmartScreen、360，全是冤枉的 —— 真因是**仓库目录自身带 `Mandatory Label\Low Mandatory Level`**，编译产物继承它。诊断：`icacls <exe>`（对照 `notepad.exe` 无此行）；修复：`icacls "<目录>" /setintegritylevel (OI)(CI)M /T`（431 文件 0 失败），**然后必须重新编译** —— 已有 exe 不会自己改标签。低完整性进程创建的文件也会继承 Low，所以这个坑会自我复制 | — |
| 27 | **外部读完整性级别前，先拿一个已知级别的进程校准** | 排查 #26 时用 Python ctypes 读 token，把 Medium 的 PowerShell 读成 `Untrusted`，据此误判"explorer 被降级"，白绕两轮（连带得出"360 沙箱化""SmartScreen 代启"等一堆错误假说）。根因是解析 `TOKEN_MANDATORY_LABEL` 时取 sub-authority 的偏移写错了。**任何"从外部测量"的工具，先测一个已知答案的对象**：`whoami /groups` 能给出自己进程的 Mandatory Label，拿它做基准 | — |
| 29 | **🔴 "等待 X 的超时"必须从真正开始等的那一刻起算，不能从发起动作起算** | 2026-10-01 真机（Firefox）：`connect()` 的 15 秒 ready 超时从 `new WebSocket()` 起算，而 Firefox 建连本身能拖 20 秒+（#28）⇒ 超时先把 connect() 判死走 catch（「连接」按钮恢复可用），随后 socket 迟到地连上、state/ready 照常到，状态徽标打出 **connected**，但"await 成功"那条更新按钮的路径已永远走不到 ⇒ **状态与按钮各说各话**。修法：① 超时改在 `onopen` 里起算（等的东西——hello→ready——那时才开始存在）；② 按钮/状态收敛到**唯一真相源** `setConnUI()`，由 onOpen/onClose 驱动，不走"await 之后的顺序代码"；③ `settle` 只认第一次，超时/出错/关闭三条路后到的丢弃；④ 判死的那次尝试要 `disconnect()` 收尸，否则僵尸连接还在订阅音频 | — |
| 28 | **🔴 Firefox 会延迟发起 localhost 的 WebSocket（与本项目无关）** | 2026-10-01 真机：诊断页点「连接」，Edge **秒连**，Firefox **每次约 8 秒**；而 local-mic 那头整条握手实测 **5 ms**（裸 TCP 探针跑 3 次：TCP 2 ms / 握手 4 ms / ready 1 ms）⇒ 时间全耗在浏览器里，且 `state` 与 `ready` 同一秒到达（说明 WS 到那一刻才真正 open）。根因 **Mozilla [Bug 1662694](https://bugzilla.mozilla.org/1662694)**（2020，RESOLVED INCOMPLETE 未修）：Firefox 会延迟**发起** localhost 的 WebSocket，连 socket 都不开，随机最长 45 s；只影响 localhost，Chrome/Edge 不复现；刷新页面无效，得彻底退出 Firefox 等一会儿。另一个复现路径：**先连一个没在监听的端口失败**，会拖住之后所有 localhost 的 WS —— 所以别留着失败的连接。**别去优化 local-mic**：页面里已加浏览器检测（Firefox 开页即提示）+ 建连耗时日志（>1.5 s 时解释是谁慢）。排除了我们自己的两个嫌疑：`DiagWeb` 每次都发 `Connection: close`（不占 Firefox 连接池）、WS 发送队列每客户端独立（不互相阻塞） | — |

| 30 | **🔴 仓库是 Medium 标签 ⇒ 低完整性进程写不进去（#26 的副作用）** | #26 把仓库目录的强制完整性标签从 Low 改成 Medium（`(OI)(CI)M`），托盘图标随之恢复正常。**但 Windows 的强制标签带 `No-Write-Up`：`Mandatory Label\Medium (NW)` 的意思是"低完整性不许写"**。于是任何以 **Low IL** 运行的工具（典型：**WorkBuddy 的沙箱 shell**；⚠ 沙箱行为因工具而异，别外推）都**写不进本仓库任何文件** —— `dotnet build` 报 `error MSB3491: Access to the path 'obj\Release\...cache' is denied`，手工 `Set-Content` 同样被拒。**这个报错与 DACL 无关**：`icacls` 显示 `OWEN\OWEN:(I)(OI)(CI)(F)` 完全控制俱全，拒绝来自完整性级别，不是权限配置错。诊断两条命令：`whoami /groups` 看自己进程的 `Mandatory Label`（Low = `S-1-16-4096`），`icacls <仓库>` 看 `Mandatory Label` 行。**处理：在沙箱外（用户正常 IL）跑构建与运行**。⛔ **别用 `icacls /setintegritylevel … L` 去"修"这个报错** —— 那正是 #26 修掉的 Low 标签，降回去 exe 继承 Low IL，托盘图标与 explorer 子进程立刻复发（#22 #23）。两个坑互为反向，别按下葫芦浮起瓢 | — |

| 31 | **🔴 #26 只修好了公式的一半：「启动者 IL 低」照样拿不到托盘图标** | #26 的公式是 `进程 IL = min(用户 IL, exe 文件标签)`，而当时的修复（`icacls … /setintegritylevel (OI)(CI)M /T` + 重编译）**只把第二项拉满**。**第一项低时结果是照旧的低**：从 Low 完整性上下文（典型＝**AI 编码助手的沙箱 terminal**，本机实测 `whoami /groups` = `Mandatory Label\Low` / `S-1-16-4096`）启动 local-mic，`min(Low, Medium) = Low` ⇒ Windows 拒绝 `Shell_NotifyIcon` ⇒ **托盘里什么都没有**（PITFALLS #22）。2026-10-01 真机复现（同一份 exe，只换启动方式）：

  | 启动上下文 | 实测进程 IL | 托盘图标 | 主窗口 |
  |---|---|---|---|
  | 桌面/资源管理器、正常 IL | `0x2000` Medium | ✅ 冷启动 **t=0s** 注册成功，`IsOffscreen=False` | 不自动弹 |
  | **Low 上下文** | `0x1000` Low | ❌ UIA 遍历通知区域 **NOT FOUND** | 自动弹，标题带 `（完整性 Low：系统不发放托盘图标，关窗即退出）` |

  **别当成托盘 bug 去改代码** —— 兜底是**设计内的正确行为**（`Integrity.TrayIconUnavailable` → `RunWithoutTray`，直接开主窗口 + 把"关窗"改成"退出"，避免无图标无窗口的隐形进程）。**判据：主窗口标题带不带那句完整性说明**，带了就说明启动方式有问题。⛔ 别再动文件标签去"修"，那是 #26 的地盘，改了反而把 #22 #23 请回来。取证/核查见 `HARDWARE.md` §3.1 | — |

| 32 | **🔴 重配对后第一次连不上会报 `枚举特征失败：AccessDenied`，再删再配一次即恢复** | 2026-10-04 真机：在「设置 → 蓝牙」删掉遥控器重新添加后，local-mic 报「状态：错误 · 枚举特征失败：**AccessDenied** · **已配对**」，紧跟着 `GetCaps 写入失败：未连接`（特征没拿到 ⇒ `_transmit == null`，写必然失败，别把它当独立故障）。**同一操作再做一次（再删再配）立刻恢复正常**：接缝自报 `{"op":"state","state":"connected"}` + `device RC003 / paired / battery 84`。⇒ 判定：重配对后的**第一次**连接处于不稳定态，与 #1（首次配对 55% 送达率）同源 —— 都是配对刚建立时的链路/缓存未稳定。**不是代码 bug，也不是固件变化**：同期强制空口读 DIS 四项（`RC003` / `MIOM` / `250519` / `V2.0` / **`2671`**）与 `HARDWARE.md` 记录逐字一致。**排查时两个陷阱**：① **别用第二个 GATT 客户端下结论** —— local-mic 已占用设备时，Python/bleak 探针连上去看到的 GATT 表是**裁剪过的**（本次实测只有 `1800/1801/180a/180f/8a7a0001/000001bf/fe59`，**既无 `ab5e0001` 也无 `0x1812`**），据此得出"设备不再提供 ATVV"是错的（#5 的同一条规则，只是作用点从"抢连接"变成"枚举结果"）；② bleak 的 `GetGattServicesAsync()` **默认走 Windows 缓存**（`_use_cached_services` 未指定 → 调无参重载 = Cached），要空口重发现必须显式传 `BleakClient(addr, winrt={"use_cached_services": False})`。**判断当前状态优先读接缝**（`ws://127.0.0.1:<端口>` 收 `state`/`device`）—— 那是 local-mic 自己的视角，且不抢设备 | — |

| 33 | **🔴 遥控器断连约 1 分钟后进入休眠、不再响应回连 —— 重试再多轮也没用，必须按一下遥控器任意键** | **断连且重试若干轮失败后，UI 条件触发提示「请按一下遥控器任意键」**。⛔ **别靠加快/增加重试**：设备根本不响应，"主动踢一脚"（多发一次 `GetGattServicesAsync`）同样无效。⛔ **提示不能常驻**：静置不掉线（见下），常驻提示 99% 的时间是噪音，用户会学会无视它 | 2026-10-04 真机，**两轮独立对照一致**：<br><br>• 断连 **2 min** → 开蓝牙后干等 **3 min 44 s（22 轮）/ 5 min 04 s（30 轮）全部失败**；按一下方向键 ⇒ **5 s / 1 s 内连上**<br>• 断连 **≤35 s**（还没睡）→ 开蓝牙后 **≤6 s 自己连上**，一轮重连就中<br>• **静置 34 min 零断链**（20 min 连续观测 + 14 min 推理证据）⇒ 休眠只在链路被外力打断后发生<br><br>⚠ **阈值未量化**（只知道在 35 s ~ 2 min 之间）⇒ **不得写进代码**（那是对固件行为的硬编码假设）；文案说「若长时间连不上，请按一下遥控器」，**不要写「等 X 秒后按键」**。<br>⚠ **休眠期间是否仍在广播**：**未验**（唯一一次 scan 时机错误、不作数），下次断链实验顺带做。<br>✅ **关蓝牙时断链事件可靠**（三次复现，同一秒 `disconnected`）⇒ **不存在"假连接"**（系统谎报 connected）。<br>⚠ 与 #1 / #32 是否同源（都是链路刚建立时的不稳定）**只是推测，未证实**，别当结论引用 |

| 34 | **🔴 `IsPaired` / 接缝的 `device.paired` 三个方向都不可信，不得拿来做门控** | 判"能不能用"**一律看 `state`**（`connected` 才是真可用）；`paired` 只当作**展示性字段**，且 UI 要能容忍它是错的（比如显示"—"，或干脆不显示） | 2026-10-04 真机，**三个方向各错一次**：<br><br>• **已配对却报 `false`**：明明能连、能读电量（84%），接缝自报 `paired:false`<br>• **删除配对后仍可能报 `true`**（见 #32）<br>• **自报都不稳**：同一台设备，某一轮恢复后报 `false`、另一轮报 `true`<br><br>⇒ 插件若拿 `paired` 做门控（"未配对就禁用录音"）**必然误判**。已同步警告进 `DSH-SEAMS.md` §7.10。<br>⚠ 顺带：`battery` 在断连时是**保留的陈旧值**（协议 §8.1 让客户端据 `state` 判可信，是对的），别拿它当"设备在线"的证据 |

| 35 | **🔴 用 `DeviceWatcher` 监听「配对被删除」：必须用配对态选择器，单地址选择器一条 `Removed` 都不发** | 2026-10-04 真机**边删边抓**（设置里点"删除设备"，探针同一时刻记录）；过程稿 `docs/notes/设备移除监听-调研-2026-10-04.md`；⚠ **当前未采用，见下表末行** |

  | 项 | 实测结论 |
  |---|---|
  | **选择器** | 配对态 `GetDeviceSelectorFromPairingState(true)` ⇒ 删配对后 **<1 秒**收到 `Removed`；单地址 `GetDeviceSelectorFromBluetoothAddress(addr)` ⇒ **全程沉默**。凭直觉选后者（看起来更精准、噪音更小）会误判成"删配对不触发事件"，进而**错误否定整条设备监听路线** |
  | **判据** | 事件里的 `Pairing.IsPaired` **不可信**——明明已配对的遥控器被报成 `False`（与 #34 同源）⇒ 只能靠 **AQS 过滤本身**判断"在不在配对态结果集里"，**不要读事件字段** |
  | **噪音** | 静默期几乎为零：90 s 观测 = 4 条 ADDED（启动枚举）+ 2 条枚举完成、**0 条 UPDATED / 0 条 REMOVED**。偶发的 UPDATED 阵发全是设备名在 `MI RC` ↔ `小米蓝牙语音遥控器` 之间抖动 ⇒ 加**属性白名单**（只认配对相关属性，丢弃 `ItemNameDisplay` / `Icon` 类）即可免疫 |
  | **可行性** | net48 + `Microsoft.Windows.SDK.Contracts` 能编译能用，无需额外运行时 |
  | **当前状态** | ⚠ **未采用 watcher 路线**——A1 的最小修法是把 `error.code` 摆到界面（十几行），比加监听便宜得多。保留此坑是为将来重新评估时**不再重踩** |

**链路质量验证公式**：`到达帧数 × 15 ms ÷ 按住时长 = 实时送达率`（分母是**真实音频时长**，不是收集窗口）。

⚠ **送达率只在首帧到末帧之间算**：窗口两端天然没有音频（按下→开流约 180 ms，松手→收尾约 630 ms），
拿它们当丢帧会把满分的链路误判成 80 分。两端延迟单独显示为"开流等待 / 收尾等待"。

**诊断日志优先于加功能**：全量落盘（`debug/<时间戳>/`，先看 `report.txt`）比加任何新功能都更能定位链路问题。
BLE 层通知回调是唯一权威观测点——只记"异常"查不出问题，因为**没发生的那件事**才是线索。

---

## 2. 本机环境坑（开发机相关，换机器可能不适用）

> ⚠ **涉及"沙箱"的条目，实测环境一律是本机当前用的 AI 编码助手 —— WorkBuddy。**
> **沙箱行为因工具而异**（进程可见性、完整性标签、网络代理、写权限各不相同），
> 本节的沙箱结论**只对 WorkBuddy 成立**，换工具（Claude Code / Cursor / Copilot 等）必须重新实测，不要外推。

| 坑 | 解法 |
|---|---|
| **HTTP 代理 `127.0.0.1:14159`** | `curl` 探 localhost 会拿到**假 502**（端口没监听也如此，无法与"真打不开"区分）⇒ 一律加 `--noproxy '*'`，并用 `netstat -ano \| grep LISTENING` 二次确认。Chrome 默认绕过 localhost，不受影响 |
| **直接调 `csc.exe` / `MSBuild.exe` 被安全策略拦**（LOLBin） | 用 `dotnet build` 代替 |
| **本机 `dotnet` 只有 runtime，无 SDK** | SDK 已补装到隔离目录 `C:\Users\OWEN\.workbuddy\binaries\dotnet-sdk\dotnet.exe`（8.0.425），构建时设 `DOTNET_ROOT` 指向它 |
| **WorkBuddy 沙箱的 PowerShell 工具看不到用户会话里的进程** | 2026-10-04 实测：**WorkBuddy**（本机 AI 编码助手）沙箱里跑的 `Get-Process` / `Get-CimInstance Win32_Process` **查不到正在运行的 `local-mic.exe`**（`MainWindowTitle` 也全空），据此误判过"程序没在跑"。同一时刻 Bash 的 `tasklist //FI "IMAGENAME eq local-mic.exe"` 能查到（PID 9248，会话 6）。**判断"程序在不在"一律用 `tasklist` 或 `netstat`**，别信 WorkBuddy 沙箱里的 PowerShell 进程查询。⚠ **沙箱行为因工具而异**（WorkBuddy / Claude Code / Cursor / Copilot 各不相同），本条只描述 **WorkBuddy** 的实测行为，**不要外推到别的工具**，换工具要重新实测 |
| **带 bleak 的 Python 环境路径** | `C:\Users\OWEN\.workbuddy\binaries\python\envs\default\Scripts\python.exe`（注意 venv 的 `python.exe` 在 **`Scripts/`** 下，不在 venv 根目录）。仓库根目录没有 venv |
| **无 MSVC 工具链** | VS 2022 Community 存在但未装 C++ 桌面开发工作负载（`VC\Tools\MSVC` 不存在）⇒ NativeAOT / Nuitka 均需 `link.exe`，当前不可用 |
| **Bash 里用 `&` 启动的 GUI 进程会随 shell 退出被杀** | 要用后台任务方式启动才常驻 |
| **无 Rust / Go** | — |

> 构建前还需 NuGet 还原两个包（都要联网）：
> `Microsoft.NETFramework.ReferenceAssemblies`（本机未装 net48 targeting pack）、
> `Microsoft.Windows.SDK.Contracts`（WinRT 类型投影）。
