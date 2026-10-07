# docs/ —— 文档索引

> 先回答"**哪份是真源、哪份只是过程稿**"。找文档从这里进；纪律与架构定案在 [`../AGENTS.md`](../AGENTS.md)。

## 1. 真源（冲突时以这些为准）

| 文档 | 是什么 | 什么时候读 |
|---|---|---|
| [`PROTOCOL.md`](./PROTOCOL.md) | 线缆协议（proto 1，**已冻结**）——**唯一真源** | 碰 local-mic ↔ 客户端通信时 |
| [`STATE-MODEL.md`](./STATE-MODEL.md) | **状态模型**：判定层 8 态（定义/判据/优先级/转移/两本账）+ 判定态→协议的映射表 | 改状态判定、写 UI 提示、动 `error.code` 时 |
| [`HARDWARE.md`](./HARDWARE.md) | RC003 硬件、ATVV 协议规格、键位真值表（local-mic 端手册） | 改 local-mic / 碰硬件时 |
| [`DSH-SEAMS.md`](./DSH-SEAMS.md) | dsh 平台接入点与实证 API（插件端手册；含 §7.9「插件改动前置清单」） | 写插件时 |
| [`PITFALLS.md`](./PITFALLS.md) | 真机坑全量：时序、日志证据、排查过程、本机环境 | 踩坑 / 调试时 |
| [`REFERENCES.md`](./REFERENCES.md) | 参考仓库清单、许可明细、排除记录 | 引用外部资料 / 碰许可问题时 |
| [`../AGENTS.md`](../AGENTS.md) | 开发纪律 + 架构定案（**少改文档**） | 每次开工前 |
| [`../DEV.md`](../DEV.md) | 进度、下一步、待办、未验证项 | 每次开工前 / **每次开发后更新** |

## 2. 中间态（**不作为结论来源**）

[`notes/`](./notes/README.md) —— 调研 / 修改意见 / 评审 / 方案 / 历史推演。每份文件头部都标了
**状态**（📝 待落定 / ✅ 已落定 / 🗑 已废弃）与**落点**；与落点文档冲突时，一律以落点文档为准。
状态模型历史推演归档见 [`notes/STATE-MODEL-历史推演-2026-10-07.md`](./notes/STATE-MODEL-历史推演-2026-10-07.md)。

## 3. 规则与工具

- **权威顺序**：长期文档（本页 §1）> `notes/` 过程稿。生命周期四步与状态块模板见 [`notes/README.md`](./notes/README.md)。
- **内容分层**（纪律 17）：§1 的设计文档**只放最终结论**——判据是"把 `notes/` 藏起来，只读它仍能开发 / 排查"；
  讨论、论证、被否方案、实验时间线属过程内容，放 `notes/`（证据类放 [`PITFALLS.md`](./PITFALLS.md)）。
  设计文档头部必须有「过程 / 历史见 X」指针，过程档案的**落点**回指设计文档。
- **命名**：过程稿 `<主题>-<类型>-<日期>.md`（类型：调研 / 修改意见 / 评审 / 方案 / 历史推演）。
- **图**：一律用 **draw.io**，放 [`drawio/`](./drawio/)，格式固定为 **`.drawio.svg`（单文件）** ——
  既是合法的 SVG（Markdown / GitHub 直接渲染），又内嵌了可编辑的图源（用 draw.io 打开即可改），
  **不存在「改了源图忘了重新导出」的失配**。文档里用 `![说明](./drawio/xxx.drawio.svg)` 引用。
  **怎么画、怎么导出（GUI / CLI 参数与坑）见 [`drawio/README.md`](./drawio/README.md)**。
- **体检**：改完文档跑一次

  ```bash
  node scripts/check-doc-links.mjs
  ```

  扫全仓 markdown 的相对链接；已知但未修的问题单独列在脚本的 `KNOWN_BROKEN` 里（带原因与日期）。
