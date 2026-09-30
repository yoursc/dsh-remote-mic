"""
路径 B 可行性探针 —— 绕开浏览器，直接用 WinRT 蓝牙栈碰 ATVV 服务。

为什么需要它：
  浏览器里报 "Authentication failed"（ATT 0x05）时，无法区分是
    ① Chrome 自己的问题 —— Web Bluetooth 没有配对 API，无法主动建立加密链路
    ② Windows 蓝牙栈的问题 —— 连 WinRT 层面也拿不到加密链路
  本脚本直接走 WinRT，能把这两者分开。它同时是「路径 B 本地 local-mic」的第一块砖。

用法：
  python probe_ble.py list                 列出 Windows 已配对的蓝牙设备（读注册表，不需要设备广播）
  python probe_ble.py scan [秒]            扫描广播（设备未配对且醒着时用这个）
  python probe_ble.py probe <MAC> [--pair] 直连并探测 ATVV 三个特征的可访问性
  python probe_ble.py audio <MAC>          端到端取音频：等按下语音键→MicOpen→收帧→报吞吐（需人工按键）

说明：
  - probe 不扫描，直接用地址连，所以配对后的设备（不广播）也能连。
  - 若在 Windows 里已配对，MAC 从 list 的输出里取。
  - --pair 会触发系统配对流程（可能弹窗），仅在未配对或权限不足时使用。
"""

import asyncio
import sys
import winreg

ATVV_SERVICE = "ab5e0001-5a21-4f05-bc7d-af01f617b664"
CHAR_TRANSMIT = "ab5e0002-5a21-4f05-bc7d-af01f617b664"
CHAR_AUDIO = "ab5e0003-5a21-4f05-bc7d-af01f617b664"
CHAR_CONTROL = "ab5e0004-5a21-4f05-bc7d-af01f617b664"

CMD_GET_CAPS = bytes([0x0A, 0x01, 0x00, 0x00, 0x03, 0x03])


def macify(hex12: str) -> str:
    """把 12 位十六进制转成 aa:bb:cc:dd:ee:ff。"""
    h = hex12.lower().replace(":", "")
    return ":".join(h[i : i + 2] for i in range(0, 12, 2))


def reverse_hex12(hex12: str) -> str:
    """Windows 注册表里蓝牙地址常按字节反序存储，两种顺序都试一遍。"""
    h = hex12.lower().replace(":", "")
    return "".join(h[i : i + 2] for i in range(10, -1, -2))


def friendly_name(root, path, subkey) -> str:
    """设备实例下的 FriendlyName 通常挂在子键（驱动实例）上，要往下找一层。"""
    try:
        k = winreg.OpenKey(root, f"{path}\\{subkey}")
    except OSError:
        return "(未知)"
    for j in range(winreg.QueryInfoKey(k)[0]):
        try:
            s = winreg.EnumKey(k, j)
        except OSError:
            continue
        try:
            k2 = winreg.OpenKey(k, s)
            try:
                return winreg.QueryValueEx(k2, "FriendlyName")[0]
            except OSError:
                pass
        except OSError:
            continue
    return "(未知)"


def cmd_list() -> None:
    """枚举 Windows 已配对的蓝牙设备。读注册表而非扫描，因此已配对但不广播的设备也在。"""
    print("Windows 已配对的蓝牙设备：")
    print("-" * 78)
    n = 0
    for bus in ("BTHLE", "BTHENUM", "BTH"):
        path = rf"SYSTEM\CurrentControlSet\Enum\{bus}"
        try:
            k = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path)
        except FileNotFoundError:
            continue
        i = 0
        while True:
            try:
                sub = winreg.EnumKey(k, i)
                i += 1
            except OSError:
                break
            if not sub.lower().startswith("dev_"):
                continue
            hex12 = sub[4:]
            if len(hex12) != 12:
                continue
            name = friendly_name(winreg.HKEY_LOCAL_MACHINE, path, sub)
            a = macify(hex12)
            b = macify(reverse_hex12(hex12))
            print(f"{name}")
            print(f"    {bus:8} 原始序 {a}   反序 {b}")
            n += 1
    print("-" * 78)
    if n == 0:
        print("一个都没有 —— 说明遥控器尚未与 Windows 配对，这正是 ATT 0x05 的常见根因。")
        print("请先到「设置 → 蓝牙和其他设备 → 添加设备 → 蓝牙」里配对遥控器，再回来跑 list。")
    else:
        print("用法：python probe_ble.py probe <MAC>（两个顺序都试试）")


async def cmd_scan(seconds: float) -> None:
    from bleak import BleakScanner

    print(f"扫描 {seconds:.0f} 秒…（遥控器若已配对通常不再广播，此时改用 list）")
    devs = await BleakScanner.discover(timeout=seconds, return_adv=True)
    if not devs:
        print("一个都没扫到。")
        return
    for d, adv in devs.values():
        print(f"{d.address}  {d.name!r}  rssi={adv.rssi}  uuids={adv.service_uuids}")


def addr_to_int(mac: str) -> int:
    return int(mac.replace(":", ""), 16)


async def try_connect(addr: str, do_pair: bool):
    """
    用给定地址直连，不扫描。

    为什么要绕：bleak 的 WinRT connect() 会先跑 BleakScanner.find_device_by_address，
    而**已配对且已连接的设备不再广播**，于是必然抛 BleakDeviceNotFoundError。
    实测从注册表拿到的地址顺序（Dev_c05d39f850c7 → C0:5D:39:F8:50:C7）可直接被
    BluetoothLEDevice.from_bluetooth_address_async 接受，所以这里把地址直接注入
    backend 的 _device_info，让 connect() 跳过扫描步骤。
    """
    from bleak import BleakClient

    print(f"  尝试连接 {addr} …")
    client = BleakClient(addr, pair=do_pair, timeout=20.0)

    backend = getattr(client, "_backend", client)
    if hasattr(backend, "_device_info"):
        backend._device_info = addr_to_int(addr)
    else:
        print("  警告：bleak 内部结构变了，无法跳过扫描，连接可能失败。")

    try:
        await client.connect()
        print(f"  连接成功：{addr}")
        return client
    except Exception as e:
        print(f"  连接失败：{type(e).__name__}: {e}")
        try:
            await client.disconnect()
        except Exception:
            pass
        return None


async def cmd_probe(mac: str, do_pair: bool) -> None:
    client = await try_connect(mac, do_pair)
    if client is None:
        print("换另一种字节序再试一次：")
        alt = macify(reverse_hex12(mac))
        if alt.lower() != mac.lower():
            print(f"  python probe_ble.py probe {alt}")
        return

    try:
        print("GATT 服务与特征：")
        for svc in client.services:
            mark = "   <<< ATVV 服务" if svc.uuid.lower() == ATVV_SERVICE else ""
            print(f"  [{svc.uuid}] {svc.description}{mark}")
            for ch in svc.characteristics:
                print(f"      {ch.uuid}  属性: {','.join(ch.properties)}")

        print("-" * 78)
        print("读写权限探测（这一步决定路径 B 是否可行）：")

        async def attempt(name, coro):
            try:
                value = await coro
                extra = ""
                if isinstance(value, (bytes, bytearray)):
                    extra = "  值: " + " ".join(f"{b:02x}" for b in value[:24])
                print(f"  [通过] {name}{extra}")
                return True
            except Exception as e:
                print(f"  [失败] {name}  ->  {type(e).__name__}: {e}")
                return False

        await attempt("读 0002 (TRANSMIT)", client.read_gatt_char(CHAR_TRANSMIT))
        await attempt("写 0002 (GetCaps 0A 01 00 00 03 03)",
                      client.write_gatt_char(CHAR_TRANSMIT, CMD_GET_CAPS))

        got = []

        def on_control(_, data):
            got.append(bytes(data))
            print(f"      CONTROL 通知 #{len(got)}: " + " ".join(f"{b:02x}" for b in data[:16]))

        ok = await attempt("订阅 0004 (CONTROL)", client.start_notify(CHAR_CONTROL, on_control))
        if ok:
            print("  现在按住遥控器的语音键说句话（10 秒）…")
            await asyncio.sleep(10.0)
            print(f"  共收到 {len(got)} 条 CONTROL 通知。")
            print("  ★ 路径 B 可行：WinRT 层面能拿到 ATVV 事件。" if got
                  else "  订阅成功但没通知 —— 可能需先写 MicOpen，或设备未进入 ATVV 态。")
            await client.stop_notify(CHAR_CONTROL)

        print("-" * 78)
        if ok:
            print("结论：WinRT 直连可用 —— 浏览器里的失败是 Chrome/Web Bluetooth 层面的，")
            print("      本地 local-mic（路径 B）能救。")
        else:
            print("结论：WinRT 直连也被拒。下一步尝试显式配对：")
            print(f"      python probe_ble.py probe {mac} --pair")
            print("      （会弹系统配对窗；配对完需重连一次，让链路按新的安全级别重建）")
    finally:
        try:
            await client.disconnect()
        except Exception:
            pass


async def cmd_audio(mac: str) -> None:
    """
    端到端取音频：等按下语音键 → 发 MicOpen → 收 AUDIO 帧 → 报告吞吐。

    这一跑能一次性回答三个未知项：
      · 帧大小是否真是 120 字节（AGENTS.md §4.5）
      · 吞吐是否达到 ~8000 B/s（§7 坑 3：MTU 不足会卡碟）
      · 5.7 秒窗口的 MicExtend 续期是否必须（§7 坑 2）
    """
    import time

    client = await try_connect(mac, False)
    if client is None:
        return

    control_frames: list[bytes] = []
    audio_frames: list[tuple[float, bytes]] = []
    session_id = 0

    def on_control(_, data: bytearray) -> None:
        b = bytes(data)
        control_frames.append(b)
        print(f"  CONTROL: " + " ".join(f"{x:02x}" for x in b[:10]))

    def on_audio(_, data: bytearray) -> None:
        audio_frames.append((time.perf_counter(), bytes(data)))

    try:
        await client.start_notify(CHAR_CONTROL, on_control)
        await client.write_gatt_char(CHAR_TRANSMIT, CMD_GET_CAPS)

        print("  60 秒内按下遥控器的语音键并说话…")
        deadline = time.time() + 60.0
        pressed = False
        while time.time() < deadline:
            hit = next((f for f in control_frames if f[0] in (0x08, 0x04)), None)
            if hit:
                pressed = True
                if len(hit) > 1:
                    session_id = hit[1]
                print(f"  检测到 0x{hit[0]:02x}，session={session_id}，发 MicOpen (0C 00)")
                break
            await asyncio.sleep(0.15)

        if not pressed:
            print("  60 秒内没检测到按下。遥控器可能在休眠 —— 先按一下任意键唤醒，再重跑。")
            return

        await client.write_gatt_char(CHAR_TRANSMIT, bytes([0x0C, 0x00]))
        await client.start_notify(CHAR_AUDIO, on_audio)

        t0 = time.perf_counter()
        last_extend = t0
        print("  收集中（最多 10 秒，每 2.0 秒发一次 MicExtend）…")
        while time.perf_counter() - t0 < 10.0:
            await asyncio.sleep(0.2)
            if time.perf_counter() - last_extend >= 2.0:
                try:
                    await client.write_gatt_char(CHAR_TRANSMIT, bytes([0x0E, session_id]))
                except Exception as e:
                    print(f"  MicExtend 失败：{e}")
                last_extend = time.perf_counter()

        await client.stop_notify(CHAR_AUDIO)
        await client.stop_notify(CHAR_CONTROL)

        elapsed = time.perf_counter() - t0
        total = sum(len(b) for _, b in audio_frames)
        sizes: dict[int, int] = {}
        for _, b in audio_frames:
            sizes[len(b)] = sizes.get(len(b), 0) + 1
        dominant = max(sizes, key=sizes.get) if sizes else 0

        print("-" * 78)
        print(f"  音频帧数    {len(audio_frames)}")
        print(f"  总字节      {total}")
        print(f"  主帧大小    {dominant} 字节   （分布 {sizes}）")
        print(f"  耗时        {elapsed:.2f} s")
        print(f"  吞吐        {total / elapsed:.0f} B/s   （16k ADPCM 需要约 8000 B/s）")
        if dominant:
            rate = (len(audio_frames) * 15.0) / (elapsed * 1000) * 100
            print(f"  实时送达率  {rate:.1f}%   （AGENTS.md §7 公式：帧数×15ms÷时长）")
        print("-" * 78)
        if total / elapsed < 8000:
            print("  ⚠ 吞吐不足 —— MTU 未协商到 247 的典型症状（§7 坑 3）。")
        else:
            print("  ★ 吞吐达标。")

        out = f"audio-{int(time.time())}.bin"
        with open(out, "wb") as f:
            for _, b in audio_frames:
                f.write(len(b).to_bytes(2, "little") + b)
        print(f"  原始 ADPCM 帧已存 {out}（每帧前 2 字节是长度），可用 Node 侧解码器回放。")
    finally:
        try:
            await client.disconnect()
        except Exception:
            pass


async def main() -> None:
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    cmd = args[0]

    if cmd == "list":
        cmd_list()
    elif cmd == "scan":
        secs = float(args[1]) if len(args) > 1 else 20.0
        await cmd_scan(secs)
    elif cmd == "probe":
        if len(args) < 2:
            print("需要 MAC 地址。先跑 list。")
            return
        await cmd_probe(args[1], "--pair" in args)
    elif cmd == "audio":
        if len(args) < 2:
            print("需要 MAC 地址。先跑 list。")
            return
        await cmd_audio(args[1])
    else:
        print(__doc__)


if __name__ == "__main__":
    asyncio.run(main())
