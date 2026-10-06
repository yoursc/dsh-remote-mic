# docs/drawio/ —— 项目示意图

> **规则**（必须用什么工具、图放哪）在 [`AGENTS.md`](../../AGENTS.md) 的「图表纪律」（纪律 17）。
> 本页只回答三个问题：**怎么画 / 怎么导出 / 怎么引用**。

## 1. 格式：单文件 `.drawio.svg`

本目录里的每张图都是**一个文件**，既是渲染图、又是可编辑图源：

| 特性 | 说明 |
|---|---|
| 合法 SVG | Markdown、GitHub、VS Code 预览都能直接渲染 |
| 内嵌图源 | `content` 属性里存着完整的 `<mxfile>`，用 draw.io 打开即可继续编辑 |
| 不失配 | 图和源**是同一个文件** ⇒ 不存在「改了源图忘了重新导出」的问题 |

因此**不另存 `.drawio` 副本**（两份文件必然失配）。文件内的图源是 **XML 转义**的纯文本
（不是 base64），所以 `content` 那一行很长但 diff 仍可解析。

## 2. 怎么改图

直接用 draw.io 打开本目录下的 `.drawio.svg` 改，改完保存即可，**不需要任何转换步骤**。

| 方式 | 说明 |
|---|---|
| **VS Code + Draw.io Integration 插件** | 推荐：改完直接在 IDE 里提交 |
| **draw.io Desktop**（本机已装 v31.7.0） | `C:\Program Files\draw.io\draw.io.exe` |
| **网页版** app.diagrams.net | File → Open From → Device，选本地文件；数据不出本机 |

⚠ **不要手改 XML 里的 `content`** —— 那是要给 draw.io 读的图源，手工编辑极易写坏。

## 3. 怎么导出（新增图，或把旧图换成原生排版时）

原生导出比任何手写渲染都好：带 **`light-dark()` 明暗自适应 + foreignObject 富文本双路 +
透明背景**，会随观看者的主题变化。两条路等价：

### 3.1 GUI

draw.io 打开 → **File → Export as → SVG** → 勾 *Include a copy of my diagram*
（透明背景按需勾）→ 覆盖同名文件。

⛔ **别勾 *Compressed***（File → Properties）：勾了图源会变 base64+deflate，diff 成一坨天书。

### 3.2 CLI（draw.io Desktop ≥ 30，本机 v31.7.0 实测）

```bash
"/c/Program Files/draw.io/draw.io.exe" \
  --no-sandbox --disable-gpu \
  -x -f svg -e -t \
  -o out.drawio.svg in.drawio.svg
```

| 参数 | 含义 |
|---|---|
| `-x, --export` | 导出模式；输入除 draw.io 文件外还支持 vsdx / csv / Mermaid |
| `-f, --format` | `pdf` `png` `jpg` `svg` `xml` `html`（`-o` 带扩展名时该项被忽略） |
| `-e, --embed-diagram` | **内嵌图源副本** —— 即单文件 `.drawio.svg`（仅 png/svg/pdf） |
| `-t, --transparent` | PNG / SVG 透明背景 |
| `-u, --uncompressed` | SVG / XML 不压缩 ⇒ **diff 可读**（等价 GUI 不勾 *Compressed*） |
| `--theme` | `dark` / `light` / `auto`（**默认 auto** ⇒ 自适应观看者明暗，即 `color-scheme: light dark`） |
| `--crop` | ⚠ **只对 PDF 生效**，SVG 加了没变化（实测 791×498 vs GUI 790×497，差 1px 无妨） |

> 参数出处：`jgraph/drawio-desktop` → `src/main/args.js`。
> **README 里没有 CLI 文档**，别去那找。

#### 两个必踩的坑

1. ⚠ **受限环境下必须加 `--no-sandbox --disable-gpu`**：否则 Electron 渲染进程直接崩
   （`Renderer process gone (crashed)`）—— 报错很含糊，容易误判成「CLI 坏了」。
2. ⚠ **GUI 开着时会抢缓存报 `0x20`**：`AppData/Roaming/draw.io` 被别的实例占用，
   先把 GUI 关掉再跑。

## 4. 怎么引用

文档里用标准 Markdown 图片语法，**路径相对引用方**：

| 引用方在哪 | 写法 |
|---|---|
| `docs/` 下的文档（如 `STATE-MODEL.md`） | `![说明文字](./drawio/xxx.drawio.svg)` |
| 仓库根目录的文档 | `![说明文字](./docs/drawio/xxx.drawio.svg)` |

图下面可加一行 `<sub>…</sub>` 注明图源文件名，顺序见 §5。

## 5. 本目录文件清单

| 文件 | 内容 | 被谁引用 |
|---|---|---|
| [`architecture-layers.drawio.svg`](./architecture-layers.drawio.svg) | 两层真相源 + 一个渲染器（判定层 → 协议层 / 桌面 UI → 插件 UI） | [`STATE-MODEL.md`](../STATE-MODEL.md) §1 |
| [`state-model.drawio.svg`](./state-model.drawio.svg) | 判定层八态转移图（前提层 4 态 + 链路层 4 态） | [`STATE-MODEL.md`](../STATE-MODEL.md) §3.2 |

新增图请**同时更新本清单**（一行：内容 + 被谁引用），方便别人找。

## 6. 坑与约定速查

| 坑 | 后果 / 处置 |
|---|---|
| **draw.io 用严格 XML 解析，浏览器用容错 HTML 解析** | 少一个 `</svg>` 时「预览正常、draw.io 报『非绘图文件』」。只有等到别人去改图才会暴露（2026-10-05 踩过） |
| 导出勾了 *Compressed* | 图源变 base64，diff 不可读。CLI 对应 `-u` 不压缩 |
| 拿脚本/程序去「优化」已导出的 svg | 会丢掉 §3 那三项原生特性（明暗自适应 / 富文本双路 / 透明背景），图看着还在实则降级 |
| 编辑器留下的 `.$xxx.bkp` | draw.io 的临时备份，**不要提交**，用完删掉 |
| **一张图里只保留一套编号** | 边标了 `①–⑤` 表示用户动作步骤，图下方的注释就**不要再编号** —— 两套圆圈数字同图会被读成同一套（2026-10-05 踩过，注释已改为不带编号） |
| 判断现有图是不是「官方导出」 | **看 `content` 里有没有 `&quot;`**（`grep -c '&quot;'`）。官方导出的 `value` 属性是**严格转义**的；若返回 **0**（属性里用裸引号），说明是脚本产物或未严格转义，应重新用官方 CLI 导出。⚠ **不要用 `agent=` 判** —— 2026-10-05 实证：GUI 打开脚本产物再保存，会把原 `mxfile` 头的 `agent="WorkBuddy"` 一起**继承**下来（`host` 已变成 `Electron`，但 `agent` 不变）⇒ 拿来当判据会误判 |

⛔ **不要为此自造转换脚本** —— 官方 GUI/CLI 已完全覆盖，用法就在本页
（2026-10-05 教训：自造脚本未经同意入库，已删除）。

## 7. 许可

| | 许可 |
|---|---|
| draw.io Desktop（`jgraph/drawio-desktop`） | **GPL v3** |
| draw.io 网页核心（`jgraph/drawio`） | Apache-2.0 |

官方声明**对用它画的图不做版权主张**，且本仓库不引用它的代码 ⇒ 与本仓库的 GPL-3.0 无冲突。
