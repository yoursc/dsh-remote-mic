# dsh-remote-mic

把**小米蓝牙遥控器 2 Pro（RC003-MS）**变成 [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)（dsh）的语音输入设备：按住遥控器语音键说话，松手后文字进入 dsh 输入框——全程不用碰键盘。

## 当前状态

| 部分 | 状态 |
|---|---|
| Windows local-mic（蓝牙采集） | ✅ 可用 |
| 通信协议 | ✅ 已冻结（proto 1） |
| 诊断页 | ✅ 可用 |
| **dsh 插件（文字进输入框）** | ⬜ **开发中** |

local-mic 已可独立使用（连接遥控器、录音、试听、自检）；插件完成后即可端到端使用。进度详见 [DEV.md](./DEV.md)。

## 为什么

用 AI 编程时键盘输入太慢，而点屏幕上的麦克风只是"换汤不换药"——交互逻辑没变。真正要的是**完全脱离键盘的物理输入设备**。

遥控器 ¥99，自带麦克风 + 蓝牙 5.4 + USB-C 可充电，是当前性价比最高的选择。

## 怎么用

### 你需要

- Windows 10 1809+ / Windows 11（自带 .NET Framework 4.8，**无需另装任何运行库**）
- 小米蓝牙遥控器 2 Pro（RC003-MS）
- dsh（deepseek-harness）——插件完成后接入

### 第一步：配对遥控器

Windows 设置 → 蓝牙和其他设备 → 添加设备 → 蓝牙 → 选「小米蓝牙语音遥控器」。

> ⚠ **拿到设备第一件事：删掉配对、重新配对一次。** 首次配对的链路送达率只有 55%（语音会丢字），重配对后恢复到 98.7%。

### 第二步：运行 local-mic

- 从源码构建：`local-mic/build.bat`（或获取已构建的 `local-mic.exe`——约 135 KB 单文件，免安装、免管理员权限）
- 启动后常驻系统托盘；双击托盘图标打开主窗口（设备信息、电量、自检、波形）
- 可选：托盘菜单开启「开机自启」

### 第三步：验证

- 主窗口 →「测试连接」：应显示型号（RC003）、固件、电量
- 主窗口 →「测试音频」：**等提示出现后**，按住遥控器语音键说一句话 → 松手 → 点播放试听
- 或从托盘菜单打开诊断页（`http://localhost:8000`）：查看接缝消息、在线跑协议契约测试

> 遥控器语音键同时会被 Windows 识别为键盘 F5（刷新键）。诊断页已默认拦截；其他网页里按语音键会刷新页面，这是硬件行为，插件侧同样会拦截。

### 第四步：在 dsh 里使用（开发中）

dsh 插件正在开发。完成后的使用方式：安装插件 → 打开 dsh 网页 → 按住遥控器语音键说话 → 松手后文字出现在输入框 → 用**另一个按钮**发送（松手不会自动发送）。

浏览器无特殊要求：音频走本地 WebSocket，不依赖 Web Bluetooth——Firefox / Safari / Chrome / Edge 均可。

## 它是怎么工作的

```
遥控器 ──蓝牙──► local-mic（Windows 托盘程序：采集 + 解码，输出 PCM16）
                       │ ws://127.0.0.1:8787
                       ▼
         dsh 插件（浏览器）──► 服务器端语音识别 ──► 文字注入输入框
```

- 遥控器的音频走 Google **ATVV 协议**（BLE），由 local-mic 完成采集与解码，归一化为 16 kHz 单声道 PCM16——与 dsh 语音链路要求的格式一致，零转换
- 语音识别（ASR）在 dsh 服务器侧完成（SenseVoice），浏览器只负责调用与注入文字
- **接缝协议设备无关**：遥控器、蓝牙麦克风、脚踏开关在协议里长得一样——将来换输入设备不需要改插件
- 为什么需要 local-mic：浏览器在 Windows 上拿不到这个设备的蓝牙连接（已配对的设备不出现在 Web Bluetooth 选择器里，不配对则链路不加密），硬件层只能交给本地程序

技术细节见 [AGENTS.md](./AGENTS.md) 与 `docs/`。

## 文档

| 文档 | 内容 |
|---|---|
| [DEV.md](./DEV.md) | 开发计划与进度 |
| [AGENTS.md](./AGENTS.md) | 架构定案与开发纪律（面向开发者 / AI） |
| [`docs/PROTOCOL.md`](./docs/PROTOCOL.md) | local-mic ↔ 插件线缆协议规范（proto 1） |
| `docs/` 其余文档 | 硬件与协议参考、dsh 平台接入点、已知坑（见 AGENTS.md 文档地图） |

## 仓库结构

| 路径 | 说明 |
|---|---|
| `local-mic/` | ★ **正式产品**：C# + .NET Framework 4.8 托盘程序（见其 [README](./local-mic/README.md)） |
| `diagnostics/` | 浏览器诊断页 + 离线测试 + Python 取证探针（`probe_ble.py`） |
| `conformance/` | 共享黄金向量（防止两套解码实现漂移） |
| `docs/` | 协议规范、参考手册、调研归档 |

## 致谢与来源

协议规格与真机取证资料主要来自 [`getsayall/remote-mic-app-windows`](https://github.com/getsayall/remote-mic-app-windows)（GPL-3.0）与 [`wangyl/mi-input`](https://github.com/wangyl/mi-input)（MIT）。许可边界详见 `AGENTS.md`。

## 许可

[GPL-3.0](./LICENSE)
