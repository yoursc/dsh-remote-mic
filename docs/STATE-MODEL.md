# STATE-MODEL.md — 状态模型（判定层 → 协议层）

> **当前实现规范。** 本文只描述已经确定、实现时必须遵守的规则；历史推演与实验过程见[历史档案](./notes/STATE-MODEL-历史推演-2026-10-07.md)。
>
> **实现状态：🚧 判定层尚未实现。** 当前代码仍是旧的协议四态，差距登记在 §6；本文是目标实现规格。
>
> 与 [`PROTOCOL.md`](./PROTOCOL.md) 冲突时，以协议唯一真源为准；本文定义判定层如何产生协议值。

## 1. 层次与硬规矩

| 层 | 定义 | 消费者 |
|---|---|---|
| **判定层** | local-mic 进程内设备处境的唯一真相源 | 协议层 |
| **协议层** | 跨进程的冻结契约：4 个 `state` 值 + `error.code` | 插件、诊断页 |

1. 协议层是单向输出契约，不能反过来影响 local-mic 的连接、重试或判定工作。
2. 判定层只使用可靠事实：配置地址、注册表配对存在性、Radio 状态、`ConnectionStatus`、fd/gd 账本及记忆位；**禁止使用 `IsPaired`**。
3. `Decide(facts, now)` 必须是无 IO 纯函数；IO 采集和转移记忆维护在外层完成。
4. 判定层七态压缩为协议层四类状态；协议层只表达跨进程所需的粗粒度连接语义。

## 2. 判定层七态

### 2.1 状态定义

| 状态 | 定义 | 用户指引 |
|---|---|---|
| `BLE_NotExist` | `Radio.GetRadiosAsync()` 没有 Bluetooth radio | 插上蓝牙适配器 |
| `BLE_Off` | 有 Bluetooth radio，但 `State != On` | 打开本机蓝牙 |
| `DeviceNotSelected` | `Config.Address == 0` | 选择一台遥控器 |
| `BLE_Unpaired` | 已选设备，但注册表没有该地址 | 在 Windows 设置中配对 |
| `Connecting` | 所有尚未成功的连接尝试，包括前提解除后的首次连接和断连后的重试 | 等待 |
| `Unresponsive` | 连续链路失败达到阈值，或 gd 超时；原因不可进一步区分 | 按一下遥控器任意键；若拿远过先放回 |
| `Connected` | 完整链路和 GATT 已打开并订阅 | 使用 |

`Unresponsive` 是推断态，不等于设备已经确认休眠；休眠与不在范围在本机不可分辨，因此合并。

### 2.2 判定优先级

每轮从头求值，先命中者胜：

1. 没有 Bluetooth radio → `BLE_NotExist`
2. 有 radio 但不是 `On` → `BLE_Off`
3. 地址为空 → `DeviceNotSelected`
4. 注册表没有地址 → `BLE_Unpaired`
5. `ConnectionStatus == Connected` 且 GATT 未进入失败阶段 → `Connected`
6. 否则按链路层记忆位、fd/gd 和时间分流：
   - `Connecting`：前提解除后的入口窗口未超时，或已进入 gd 阶段；
   - `Connecting`：断连后的重试中且 fd < 3；
   - 入口 20 秒超时且未进入 gd，或断连后 fd ≥ 3 → `Unresponsive`；
   - gd 连续 60 秒未完成 → `Unresponsive`。

Radio 监听或探测失败时按“蓝牙在”乐观兜底，不能反判为 `BLE_Off`。

### 2.3 判据与禁止事项

| 事实 | 用途 | 结论 |
|---|---|---|
| `Config.Address` | 是否选择设备 | 可靠 |
| 注册表 `BTHLE\\Dev_*` / `BTHENUM\\Dev_*` | 是否配对 | `BLE_Unpaired` 主判据 |
| `Radio.StateChanged` + `GetRadiosAsync()` | 蓝牙适配器 / 无线电 | 主监听 + 轮询兜底；对象必须保强引用 |
| `ConnectionStatus` / `ConnectionStatusChanged` | 链路是否连接 | 可靠 |
| `Pairing.IsPaired` | 配对判定 | **禁用**，仅可作为取证日志字段 |
| `FromBluetoothAddressAsync` 是否建出对象 | 配对判定 | **禁用**；删配对后仍可能从缓存建出对象 |
| RSSI / 广播 | 区分休眠与出范围 | 不可用 |

## 3. 账本、记忆位与转移

### 3.1 fd / gd 两本账

| 账本 | 计入 | 不计入 | 消费者 |
|---|---|---|---|
| **fd 链路账本** | 链路未建立：创建失败、异常、等待超时 | 链路已建立后的 GATT 失败 | `Connecting`（断连重试）的阈值 3 |
| **gd GATT 账本** | 链路已建立但服务/特征枚举或订阅未完成 | — | 独立 60 秒窗口 |

- 一轮是一次真正执行到底的 `ConnectAsync`；被 `_busy` 拒绝不算一轮。
- 两本账只有在完整 GATT 打开并订阅成功时同时归零。
- gd 阶段 fd 冻结；协议只显示 `connecting`。
- `Unresponsive` 期间重试定时器不得停止或放慢。
- 前提解除后的 `Connecting` 入口只看 20 秒墙钟；进入 gd 后让位给 gd 的 60 秒窗口。断连后的 `Connecting` 按 fd < 3 / ≥ 3 判定。

### 3.2 记忆位

判定层需要读取、但不负责读取 IO 的记忆位：

- 当前连接尝试来源：前提解除后的入口，或 `Connected` 断连后的重试；
- 进入时刻 `t0`；
- fd、gd 账本及 gd 起始时刻。

时钟 `now` 作为参数注入，不在纯函数内读取 `DateTime.Now`。

### 3.3 转移表

| 从 | 到 | 条件 |
|---|---|---|
| 任一前提态 | `Connecting` | 前提解除；记 `t0`；立即尝试一次 |
| `Connecting` | `Connected` | 20 秒窗口内完整连接成功 |
| `Connecting` | `Unresponsive` | 20 秒超时且未进入 gd，或 gd 超时 |
| `Connected` | `Connecting` | 断连；fd 连续失败计数开始；立即尝试 |
| `Connecting` | `Connected` | 完整连接成功；账本归零 |
| `Connecting` | `Unresponsive` | 入口 20 秒超时，或断连后 fd ≥ 3 |
| `Unresponsive` | `Connected` | 后续重试成功（按键唤醒或设备自愈） |
| 任一链路态 | `BLE_NotExist` / `BLE_Off` | Radio 事实命中 |
| 任一链路态 | `BLE_Unpaired` | 注册表确认地址消失 |
| 任一链路态 | `DeviceNotSelected` | 用户选择“（不选择设备）” |

前提层解除后**固定进入 `Connecting`**；`Connected` 断连后同样进入 `Connecting`。不得由 `Decide` 直接选择 `Unresponsive`，只能由入口时钟、fd 或 gd 超时推出。

## 4. 判定时机与 IO 责任

必须在以下时机重新调用同一个 `Decide`：

1. 程序启动；
2. 每次连接尝试结束；
3. `ConnectionStatusChanged` 到达时；
4. `Radio.StateChanged` / `DeviceWatcher` 到达时；
5. 保留 Tick 轮询作为事件丢失和设备无事件场景的兜底。

事件对象（Radio、DeviceWatcher）必须由字段或集合保持强引用。监听不能替代轮询：设备休眠、断连和自愈可能不产生可用的 Windows 事件。

建议的无 IO 结构：

```text
IO 采集事实 → Decide(facts, now) → 判定态 → 协议映射
```

## 5. 判定态到协议层映射

协议层不参与判定，也不向 local-mic 下发设备命令；它只接收判定结果并压缩成跨进程状态。

判定态归并为四类，正好对应插件侧连接颜色：

| 协议类别 | 颜色 | 判定态 | 协议 `state` |
|---|---|---|---|
| 无法连接设备（前提未满足） | 灰 | `BLE_NotExist` / `BLE_Off` / `DeviceNotSelected` / `BLE_Unpaired` | `error` |
| 连接成功 | 绿 | `Connected` | `connected` |
| 连接中 | 黄 | `Connecting` | `connecting` |
| 连接失败 | 红 | `Unresponsive` | `error` |

`error` 状态需要结合 `error.code` 区分“无法连接设备”和“连接失败”；协议层不新增第五个 `state` 值。`state` 和 `error` 都是状态语义，只在值变化时推送。

| 判定态 | 协议 `state` | `error.code` | `retryable` |
|---|---|---|---|
| `BLE_NotExist` | `error` | `device_not_found`（当前压缩映射） | `true` |
| `BLE_Off` | `error` | `device_not_found`（当前压缩映射） | `true` |
| `DeviceNotSelected` | `error` | `device_not_found`（待协议同步） | 待定 |
| `BLE_Unpaired` | `error` | `pairing_required` | `false` |
| `Connecting` | `connecting` | 不推送 | — |
| `Unresponsive` | `error` | `connect_timeout` | `true` |
| `Connected` | `connected` | — | — |

`device.paired` 在目标实现中由判定态派生：除 `DeviceNotSelected` 和 `BLE_Unpaired` 外为 `true`。客户端不得用它判断是否可用；可用性只看 `state == connected`。

> `DeviceNotSelected` 的错误码和 `retryable`、`device.paired` 是否保留，属于协议同步事项；在协议文档更新前不得自行修改冻结协议。

## 6. 当前实现差距

判定层尚不存在。当前代码仍有以下已知脱节：

- `LocalMic` 仍直接维护协议四态；
- `BleRemote.IsPaired` 仍被连接错误和 `device.paired` 使用；
- `FindRemote()` 仍有自动选择逻辑；
- fd/gd 两本账尚未实现；
- Radio / DeviceWatcher 尚未接入生产判定层；
- `Connecting` 入口态和 20 秒窗口尚未实现；

实现顺序应以本文件为准，并为 `Decide`、状态转移和映射补充无硬件夹具测试。

## 7. 当前未决事项

1. `DeviceNotSelected` 的协议错误码与 `retryable` 取值（协议同步时决定）。
2. `device.paired` 是否从 proto 1 中废弃（若废弃属于破坏性变更）。
3. 设备被其他主机占用时的实测签名（当前按 `Unresponsive` 兜底）。
4. RSSI 在连接态是否可用（若未来能可靠区分休眠与出范围，再单独设计拆态）。
5. gd 阶段的具体内部数据结构和测试夹具边界。

这些事项未定前，不得在代码或协议中自行发明新语义。
