#!/usr/bin/env python3
"""
接缝观测器 —— 长时间旁听 local-mic 的 WS 接缝，记录**带墙钟的状态时间线**。

为什么需要它：诊断页看到的是"此刻"，而很多问题只在时间线上才看得出来
（断连多久才恢复、重试了几轮、错误码什么时候切换）。本工具不参与生产，只做旁听。

与 probe-ws.mjs 的分工（**不重复，互补**）：

    probe-ws.mjs    短时、交互 —— 连上后按住语音键说一句，看一次音频会话的字节
    seam-watch.py   长时、无人值守 —— 跨几十分钟记录 state / device / error 的变化

★ 心跳（本工具的关键设计）：`state` / `device` 是**状态语义**，只在取值变化时推送。
  因此"日志里没记录"既可能是链路稳定，也可能是观测脚本已经死了 —— 两者无从区分。
  本工具每 30 秒写一行心跳，把这个歧义消掉。2026-10-04 的 X3 实验正是靠 39 条心跳
  才敢下"20 分钟零断链"这个结论；在那之前"没记录"和"没变化"是分不开的。

跑：
    <python> seam-watch.py [秒数] [输出文件]
    例：seam-watch.py 1200 x3.log        # 观测 20 分钟

依赖：`websockets`（Python）。本机可用环境见 `docs/PITFALLS.md` §2。
端口：自动探测 18787 → 8787（本机注册表里存的是 18787，不是默认的 8787）。
"""

import asyncio
import datetime
import os
import sys
import time

os.environ["no_proxy"] = "*"   # 本机会话有 HTTP 代理，探 localhost 必须绕开

import websockets  # noqa: E402

PORTS = [18787, 8787]
SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 900
OUT = sys.argv[2] if len(sys.argv) > 2 else "seam.log"

t0 = time.time()


def now():
    return datetime.datetime.now().strftime("%H:%M:%S")


def rel():
    return time.time() - t0


def emit(line, fh):
    print(line, flush=True)
    fh.write(line + "\n")
    fh.flush()


async def watch(fh):
    url = None
    ws = None
    for p in PORTS:
        try:
            ws = await websockets.connect("ws://127.0.0.1:%d" % p, open_timeout=3)
            url = "ws://127.0.0.1:%d" % p
            break
        except Exception:
            continue
    if url is None:
        emit("%s  [错误] 两个端口都连不上 %s" % (now(), PORTS), fh)
        return
    emit("%s  [已连] %s" % (now(), url), fh)
    last_msg = time.time()   # 最后一次收到任何消息的时间
    last_hb = time.time()    # 上次写心跳的时间
    try:
        while time.time() - t0 < SECONDS:
            # 心跳：证明「脚本还活着 + WS 还连着」，与「状态没变化」区分开
            if time.time() - last_hb >= 30:
                emit("%s  T+%6.1fs  [心跳] 脚本存活·WS 仍连·已静默 %.0f 秒"
                     % (now(), rel(), time.time() - last_msg), fh)
                last_hb = time.time()
            try:
                m = await asyncio.wait_for(ws.recv(), timeout=1.0)
            except asyncio.TimeoutError:
                continue
            except Exception as e:
                emit("%s  [WS 断开] %s" % (now(), type(e).__name__), fh)
                break
            last_msg = time.time()
            if isinstance(m, bytes):
                emit("%s  T+%6.1fs  [二进制 %d 字节]" % (now(), rel(), len(m)), fh)
            else:
                s = m.strip()
                try:
                    import json
                    o = json.loads(s)
                    op = o.get("op")
                    if op == "state":
                        s = "STATE=%s  detail=%s" % (o.get("state"), o.get("detail"))
                    elif op == "device":
                        s = "DEVICE model=%s paired=%s battery=%s" % (
                            o.get("model"), o.get("paired"), o.get("battery"))
                    elif op == "error":
                        s = "ERROR code=%s retryable=%s msg=%s" % (
                            o.get("code"), o.get("retryable"), o.get("message"))
                    elif op in ("hello", "ready"):
                        s = s[:120]
                    else:
                        s = s[:160]
                except Exception:
                    s = s[:160]
                emit("%s  T+%6.1fs  %s" % (now(), rel(), s), fh)
    finally:
        try:
            await ws.close()
        except Exception:
            pass


def main():
    with open(OUT, "w", encoding="utf-8") as fh:
        emit("=== 接缝观测开始 目标 %d 秒 ===" % SECONDS, fh)
        try:
            asyncio.run(watch(fh))
        except Exception as e:
            emit("%s  [异常] %s %s" % (now(), type(e).__name__, e), fh)
        emit("=== 结束 ===", fh)


main()
