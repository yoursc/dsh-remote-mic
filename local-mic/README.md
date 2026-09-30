# local-mic —— Windows 端本机采集端（正式版）

把小米蓝牙语音遥控器（RC003-MS）的 ATVV 音频，经 `ws://127.0.0.1:8787` 暴露给浏览器里的 dsh 插件。

```
遥控器 ──BLE──► local-mic（本机常驻，托盘 + 主界面）──ws://localhost──► 浏览器 / dsh 插件
```

## 为什么是它

硬件与蓝牙是 Windows 在管，硬塞进浏览器会处处受限。事实依据：

- `requestDevice()` 的选择器**只列正在广播的设备**。遥控器一旦被 Windows 配对就停止广播，从选择器里消失；
  而未配对时链路不加密，订阅必出 ATT 0x05。**两头堵死**，浏览器侧无解。
- Web Bluetooth **没有配对 API**，微软官方给的解法（`PairAsync` 后重建设备对象）浏览器做不到。

所以硬件层与 ADPCM 解码归 local-mic；转写、注入在浏览器侧 —— **唯一例外**是自检播放，见下文「解码器为什么有两份」。

## 产物与依赖

| | |
|---|---|
| 产物 | `bin/Release/local-mic.exe`，**约 135 KB，单个文件，无附带 DLL** |
| 目标机依赖 | **仅需 .NET Framework 4.8**（Win10 1809+ / Win11 自带，属操作系统组件，由 Windows Update 维护） |
| 构建期依赖 | .NET SDK 8 + 两个 NuGet 包（都只在构建期起作用，不进产物） |

两个 NuGet 包的作用：

- `Microsoft.NETFramework.ReferenceAssemblies` —— 本机没装 net48 targeting pack，用它提供引用程序集
- `Microsoft.Windows.SDK.Contracts` —— 提供 WinRT（`Windows.Devices.Bluetooth`）类型投影

## 构建

```bat
build.bat
```

或直接用 SDK：

```bash
dotnet build local-mic.csproj -c Release
```

## 界面

### 主窗口

**双击托盘图标**打开。点关闭只是隐藏，程序继续在托盘常驻；真正退出走托盘的「退出」。

| 区域 | 内容 |
|---|---|
| 设备 | 型号友好名 + 型号徽章、厂商/固件/硬件版本/序列号、MAC、**电量条** |
| 自检 | 测试连接 / 测试音频、实时时长·帧数·速率、**波形图**、播放 / 另存为 WAV |
| 底部 | 开机自启、链路状态、WebSocket 地址与在线客户端数 |

「测试音频」的流程是**按住说话 → 松手自动解码 → 点播放试听**。录制期间时长和帧数实时刷新。

### 托盘菜单

精简为「开关 + 快速动作」，其余进入主窗口：

| 菜单项 | 作用 |
|---|---|
| **打开主窗口…** | 双击图标同效 |
| 状态 / 设备 / 电量 | 只读 |
| 重新连接 | 手动踢一次连接 |
| 打开浏览器诊断页 | `http://localhost:8000` |
| 开机自启 | 写 `HKCU\...\Run`，免管理员权限 |
| 退出 | — |

状态灯：绿=已连接，黄=连接中，灰=未连接，红=错误。

单击托盘图标弹气泡显示状态摘要（含电量）；**低电量（≤20%）时会主动弹一次提醒**。

## 设备识别

连接后读标准 **Device Information Service (0x180A)**：

| 特征 | 实测值（RC003-MS） |
|---|---|
| `2A24` Model Number | `RC003` |
| `2A29` Manufacturer | `MIOM` |
| `2A27` Hardware Revision | `V2.0` |
| `2A26` Firmware Revision | `2671` |
| `2A25` Serial Number | `250519` |

⚠ **DIS 里的型号是 `RC003`，没有 `-MS` 后缀。** 所以白名单必须**按前缀匹配**
（`DeviceProfiles.cs`）。写成 `== "RC003-MS"` 会永远匹配不上，然后静默退化成未知设备 —— 这种 bug 很难发现。

未知型号**不会被拒绝**：ATVV 是 Google 的通用协议，别家遥控器可能完全可用。
界面会显示「型号 · 未验证」并注明，用户自行判断。加新设备只需在 `DeviceProfiles.Known` 里加一行。

## 电量

标准 **Battery Service (0x180F)**，特征 `2A19`。实测属性是 **Read + Notify**，所以：

- 连接后先读一次
- 订阅 notify 后由遥控器主动推送，**不需要轮询**（轮询会打扰一个省电设备）

界面在主窗口显示进度条（>50% 绿 / >20% 黄 / ≤20% 红），托盘 tooltip 也带百分比。
**断线后保留上次读数但灰显**，不会让过期数字看起来像实时值。

## 解码器为什么有两份

Node 侧（`diagnostics/js/adpcm.js`，供离线测试）与 C# 侧各有一份 ADPCM 解码器。
**生产路径走 C# 这份** —— ADPCM 是硬件编码细节，
不该越过接缝，所以客户端拿到的已经是 PCM16。

Node 那份并未失效：它继续支撑诊断页与 `--record` 产物的离线比对。
但要注意 **它的测试不再覆盖生产路径** —— 生产侧的状态机回归靠
`AtvvSession.RunSelfTest()`（25 条用例，无需硬件）。

两份实现靠**共享黄金测试向量**约束，防止漂移：

```
conformance/adpcm-vectors.txt      ← 单一数据源，由 Node 侧解码器生成
  ├─ Node:  diagnostics/test/l0-adpcm.test.mjs
  └─ C#  :  AdpcmDecoder.RunSelfTest()   （编译进 exe，随发布走）
```

生成：`node conformance/gen-adpcm-vectors.mjs`
校验：`local-mic.exe --selftest`（退出码 0 = 通过）

## 命令行参数（开发用）

| 参数 | 作用 |
|---|---|
| `--selftest [out.txt]` | 三段自检：ADPCM 黄金向量（12 条）· ATVV 会话状态机（25 条）· 协议夹具（19 条） |
| `--probe [out.txt]` | 真机连一次，输出型号 / 固件 / 序列号 / 电量 |
| `--record [base]` | 等按键 → 录制 → 解码，输出 `base.bin`（原始帧）与 `base.wav` |

三个都是 WinExe 没有控制台，结果同时写入文件供脚本读取。

`--record` 存出的 `.bin` 是「2 字节 LE 长度 + payload」的重复序列，与 `probe_ble.py` 同格式，
所以可以用 `node diagnostics/decode-bin.mjs <bin>` 解同一份数据，**拿两套解码器的输出做逐字节对比**。

## 线缆协议

> **规范正文见 [`docs/PROTOCOL.md`](../../docs/PROTOCOL.md)（proto 1）。**
> 本节不复述消息格式 —— 复述必然漂移，规范只有一个真源。

| | 值 |
|---|---|
| **当前实现** | **proto 1** ✅ |

实现要点（这些是行为，不只是字段）：

- 音频载荷是 **PCM16 s16le**（约 480 B/帧）—— 解码在 local-mic 内完成，接缝上不出现 ADPCM
- 控制语义只以 `capture` 事件出现，没有二进制 `0x01` 控制帧
- 接缝上不出现设备细节：没有 `ready.uuids`、没有 `{op:"write"}`（理由见 `docs/PROTOCOL.md` §1 原则 1 / 原则 3）
- ATVV 会话机常驻生产路径（`src/AtvvSession.cs`），含 `0x0A` 解码器重置接线
- 握手判定在 `src/Protocol.cs` 的**纯函数**里 —— 协议夹具跑的就是它

⚠ **不带 `proto` 的 `hello` 视为 proto 0 并回 `error{proto_mismatch}` 断开**：
帧头 `0x02` 在两种解释下相同，
不拦住的话对方会把 ADPCM 当 PCM16 解，得到白噪声且**全程不报错**。

## 设计决策与踩过的坑

**WebSocket 自己实现，不用 HttpListener。** `HttpListener` 走 http.sys，绑定前缀要先
`netsh http add urlacl` 注册，那要管理员权限 —— 与「免安装免提权」直接冲突。
这里用 `TcpListener` 自己做握手和解帧（RFC 6455 子集），约 200 行，零第三方依赖。

**设备发现读注册表，不扫描。** 已配对且已连接的设备不再广播，扫描必然一无所获。
读 `HKLM\SYSTEM\CurrentControlSet\Enum\BTHLE\Dev_xxxxxxxxxxxx`，字节序**不需要反转**
（实测 `Dev_c05d39f850c7` → `C0:5D:39:F8:50:C7`）。

**连上后要等 `ConnectionStatus`。** `FromBluetoothAddressAsync` 返回设备对象不代表链路已建立，
必须等 `ConnectionStatusChanged` 到 Connected。

**自检必须先静默 800 ms。** 连接初期设备会吐出遗留通知（实测刚连上就收到 `04 03 02 11`），
不丢弃就会在用户还没按键时误判成「已按下」并发 MicOpen。用 `armed` 标志把关。

**Caps 的 codecs 字节会变。** 真机上 byte[3] 观测到 `0x00` 和 `0x02` 两种形态。
`Atvv.ParseCaps()` 保留了回落分支（`codecs=0` 且 `version>=0x0100` 时取 `b[4]&0x03`），
**别当冗余代码删掉**。

**吞吐的正确分母。** 音频时长 = 帧数 × 15 ms，不是 wall-clock 收集窗口 ——
拿收集窗口当分母会算出 2709 B/s 并误报「吞吐不足」，真实值是 8000 B/s。

**连接必须串行化。** 定时器、浏览器上线、启动踢一脚、用户点自检 —— 这些都可能同时触发连接。
用 `SemaphoreSlim` 让后来者**排队**，不要用 `_busy` 标志挡掉后来者并报「正在连接中」，
那会让启动瞬间点自检莫名其妙地失败。

**启动后立刻探一次。** 只在定时器里探、无客户端时还降频到约 30 秒一次的话，
开机自启的用户会盯着一个灰色图标怀疑它坏了。

**读 DIS / 电量的失败不能影响音频链路。** 这些是锦上添花，整段包在 try/catch 里，
读不到就显示「—」或标「未验证」，绝不因此让连接失败。

**界面不引任何 NuGet。** 波形和电量条都是自绘（`Widgets.cs`，约 120 行）。
用 `DataVisualization.Charting` 会附带 DLL，破坏「单文件 exe」这个目标。
