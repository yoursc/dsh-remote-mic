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

**链路质量验证公式**：`到达帧数 × 15 ms ÷ 按住时长 = 实时送达率`（分母是**真实音频时长**，不是收集窗口）。

⚠ **送达率只在首帧到末帧之间算**：窗口两端天然没有音频（按下→开流约 180 ms，松手→收尾约 630 ms），
拿它们当丢帧会把满分的链路误判成 80 分。两端延迟单独显示为"开流等待 / 收尾等待"。

**诊断日志优先于加功能**：全量落盘（`debug/<时间戳>/`，先看 `report.txt`）比加任何新功能都更能定位链路问题。
BLE 层通知回调是唯一权威观测点——只记"异常"查不出问题，因为**没发生的那件事**才是线索。

---

## 2. 本机环境坑（开发机相关，换机器可能不适用）

| 坑 | 解法 |
|---|---|
| **HTTP 代理 `127.0.0.1:14159`** | `curl` 探 localhost 会拿到**假 502**（端口没监听也如此，无法与"真打不开"区分）⇒ 一律加 `--noproxy '*'`，并用 `netstat -ano \| grep LISTENING` 二次确认。Chrome 默认绕过 localhost，不受影响 |
| **直接调 `csc.exe` / `MSBuild.exe` 被安全策略拦**（LOLBin） | 用 `dotnet build` 代替 |
| **本机 `dotnet` 只有 runtime，无 SDK** | SDK 已补装到隔离目录 `C:\Users\OWEN\.workbuddy\binaries\dotnet-sdk\dotnet.exe`（8.0.425），构建时设 `DOTNET_ROOT` 指向它 |
| **无 MSVC 工具链** | VS 2022 Community 存在但未装 C++ 桌面开发工作负载（`VC\Tools\MSVC` 不存在）⇒ NativeAOT / Nuitka 均需 `link.exe`，当前不可用 |
| **Bash 里用 `&` 启动的 GUI 进程会随 shell 退出被杀** | 要用后台任务方式启动才常驻 |
| **无 Rust / Go** | — |

> 构建前还需 NuGet 还原两个包（都要联网）：
> `Microsoft.NETFramework.ReferenceAssemblies`（本机未装 net48 targeting pack）、
> `Microsoft.Windows.SDK.Contracts`（WinRT 类型投影）。
