# REFERENCES.md — 参考仓库、许可与排除记录

> 架构定案与纪律见 [`../AGENTS.md`](../AGENTS.md)（许可边界摘要在 AGENTS.md §5）。
> 本文件放**参考清单、许可明细、排除记录**等署名与合规信息。

---

## 1. 本地文件阅读顺序

| 顺序 | 文件 | 什么时候读 |
|---|---|---|
| 1 | [`../AGENTS.md`](../AGENTS.md) | **每次开工前必读**（纪律 + 定案 + 文档地图） |
| 2 | [`../DEV.md`](../DEV.md) | 每次开工前（进度、下一步、未验证项） |
| 3 | [`PROTOCOL.md`](./PROTOCOL.md) | 要碰 local-mic ↔ 客户端通信时（线缆协议唯一真源） |
| 4 | [`HARDWARE.md`](./HARDWARE.md) | 改 local-mic / 碰硬件、协议、键位时 |
| 5 | [`DSH-SEAMS.md`](./DSH-SEAMS.md) | 写 dsh 插件时 |
| 6 | [`PITFALLS.md`](./PITFALLS.md) | 踩坑 / 调试时 |
| 7 | `local-mic/README.md` | 改 local-mic（构建、界面、设计决策）时 |
| 8 | `research/`（本目录同级） | 追溯历史决策时（过程稿，结论可能过时） |

> ⚠️ **优先级**：线缆协议一律以 `PROTOCOL.md` 为准；`research/` 下文件与其他文件冲突时，以其他文件为准。

---

## 2. 遥控器协议实现（正主，最该参考）

> ⚠ **许可是 GPL-3.0**：它的**文档与事实结论**可以读、可以引用（协议事实不受版权保护），
> **源码不可直接复用或翻译**（本仓库已 GPL-3.0，许可障碍消除，但用户已拍板不解禁——见 §4）。

- **`getsayall/remote-mic-app-windows`** — Rust + Tauri 2 + Vue 3，**活跃维护中**
  - `crates/sayall-core/src/atvv.rs` — **单元测试即协议规格**，四个 case 覆盖全部固件怪癖
  - `hardware/RC003/README.md`（58 KB）— **RC003 真机取证**，第 5 章是关键结论
  - `hardware/RC003/evidence/` — 真实设备枚举日志（等于别人替你踩过的现场记录）
  - `hardware/RC003/probes/` — 三十多个探针脚本
  - `Bugs/` — 三十篇实战故障记录，按日期命名
  - `Testing/probe-rc003-hid-gatt.ps1`、`probe-rc003-vendor-gatt.ps1` — 现成 GATT 枚举脚本

## 3. 其他参考

- `wangyl/mi-input` — Python 纯 ctypes，零第三方依赖。含 `docs/architecture.md`（7 条踩坑 + 键位真值表）。**已归档 v1.0**，但 Python 路线最值得抄
- `QYHHHH/BleRemoteBrideg` — `docs/MIC-AUDIO-FEASIBILITY.md`，ATVV 可行性量化评估
- deepseek-harness（目标平台）的文档与源码清单见 [`DSH-SEAMS.md`](./DSH-SEAMS.md) §6

## 3.1 ⚠️ 已排除的仓库（留档，不为参考）

**`Lucasmantou/MiControl` —— 不要参考，不要引用。**

它是 `getsayall/remote-mic-app-windows` 的未署名副本：

- 6 个同名源码文件，其中 4 个文件**字节数完全相同**，另 2 个**仅差 6 字节**——
  恰为替换 crate 前缀 `sayall` → `micontrol` 的差值
- 连测试 fixture 文件名都一致；**非 fork**
- README 中 13 个署名关键词全部 0 命中
- 正主为 GPL-3.0，但其未履行保留署名义务

⇒ 技术上无额外价值，且来源存疑（**署名 + 许可双重违规**）。排除记录留档，仅供回查。

---

## 4. 许可边界：协议开源 ≠ 代码能抄

**"这个协议开源吗"要分三层回答，答案各不相同**（许可数据 2026-09-30 查 GitHub API）：

| 层 | 状态 |
|---|---|
| **协议规范（ATVV）** | ❌ **不是开放标准**。它是 Android TV 遥控器的语音通道，Google 没有公开规范文档 —— 我们今天能写代码，是因为**别人把它完整逆向出来了**（所以才说"不要自己逆向，照规格写"） |
| **协议事实**（UUID / opcode / 固件怪癖） | ✅ **可自由使用**。事实性信息不受版权保护，照规格重新实现不构成侵权 |
| **实现代码**（各参考仓库） | ⚠️ **开源，但许可不统一** —— 见下表 |
| **遥控器固件** | ❌ 闭源（小米） |

参考仓库许可（`git` 化名前请先看这一列）：

| 仓库 | 许可 | star |
|---|---|---|
| `HD838A/remote-mic-app`（SayAll macOS） | **GPL-3.0** | 1495 |
| `getsayall/remote-mic-app-windows`（正主，我们在参考） | **GPL-3.0** | 81 |
| `ZSTDJan/windows-remote-mic-app` | **GPL-3.0** | 39 |
| `QYHHHH/BleRemoteBrideg` | **GPL-3.0** | 2 |
| `wangyl/mi-input`（Python，已归档） | **MIT** ✅ 唯一宽松的 | 1 |

**🔴 GPL-3.0 有传染性**：直接**复制**这四个项目的代码 ⇒ 本仓库整体必须 GPL-3.0。

**本仓库已于 2026-09-30 采用 GPL-3.0**（用户拍板，LICENSE 在仓库根）。许可障碍因此消除，
但**用户已拍板不解禁源码复用**——维持"照规格自己写"：可读其文档、抄其事实结论，
**不直接复用或翻译其源码**，实现保持独立、署名清晰。

> 本仓库的 C# local-mic 与 TS 实现都是照协议事实自己写的，不受 GPL 传染。
> §3.1 那个被排除的仓库是同一根线的另一面：正主是 GPL-3.0，改名搬运且不留署名，
> 既违反署名义务也违反许可。
