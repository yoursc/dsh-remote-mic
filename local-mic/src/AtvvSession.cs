using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DshRemoteMic
{
    /// <summary>
    /// 往设备写字节的唯一出口。抽出来是为了让会话机能脱离蓝牙做无硬件回归 ——
    /// 解码归 local-mic 之后，生产路径的状态机不能再只靠真机验证。
    /// </summary>
    internal interface IAtvvTransport
    {
        /// <summary>成功返回 null，失败返回原因。</summary>
        Task<string> WriteAsync(byte[] data);
    }

    /// <summary>
    /// ATVV 会话状态机 —— local-mic 侧的「设备适配层」核心。
    ///
    /// 它是 PROTOCOL.md 原则 1 的落点：设备的一切细节（opcode、session_id、ADPCM、
    /// 5.7 秒续期窗口）都止步于这个类。对外只有三个设备无关的信号：
    /// **采集窗口开始 / 结束（带 reason） / PCM16 样本**。
    ///
    /// 三条固件怪癖在这里处理（AGENTS.md §4.6）：
    ///   1. 固件会跳过 0x08 直接发 0x04 —— 不能把 MicOpenRequested 当必需的中间态
    ///   2. 0x00 StreamStopped 会重复到达 —— 必须按 session_id 去重
    ///   3. 免费音频窗口约 5.7 秒 —— 必须每 2.5 秒写 0x0E 续期
    ///
    /// 🔴 0x0A DecoderSync 必须接线：漏掉它 ⇒ 解码器状态漂移 ⇒
    /// 连续爆音且全程不报错。
    /// </summary>
    internal sealed class AtvvSession
    {
        public const int ExtendIntervalMs = 2500;
        public const int TailWindowMs = 30;
        public const int FirstAudioTimeoutMs = 2000;
        public const int ArmDelayMs = 800;

        /// <summary>
        /// 音频静默多久即判定「设备已停流」。
        ///
        /// 为什么需要它：真机实测松手后 0x00 StreamStopped 会迟到，迟到到下一次按键
        /// 才随连接激活一起投递（BLE 空闲时连接间隔变大，通知要等下一个连接事件）。
        /// 只等 0x00 ⇒ 松手后采集窗口一直不关，转写永远不出结果。
        ///
        /// 取 600 ms 的依据：ATVV 每 15 ms 一帧，正常流里连续 40 帧空档不可能出现；
        /// 而人对「松手到出字」的容忍在 1 秒内。0x00 若先到则正常走 0x00 路径，
        /// 迟到时窗口已关，会被 `!_capturing` 挡掉，不会重复结束。
        /// </summary>
        public const int SilenceTimeoutMs = 600;

        /// <summary>
        /// 开麦后多久仍无音频就补发一次 MicOpen。
        ///
        /// 为什么需要它：真机 2026-09-29（debug/20260929-230551）的时序是
        ///   用户按下 → 设备发 0x04 → **主机回 0x0C MicOpen** → 设备再发一个 0x04 → 音频才来。
        /// 固件跳过了 0x08，于是 0x04 同时兼有"请求开麦"的含义；**设备要收到 MicOpen 才真正送流**。
        /// 会话机原先在 0x04 分支只 start、不发 MicOpen ⇒ 插件上线后按下去不会有声音，
        /// 只能等 FirstAudioTimeout 判 timeout。
        ///
        /// 为什么是"300 ms 后补发"而不是"收到 0x04 就发"：若设备本已开始流，
        /// 多写一次 MicOpen 可能打断它。等 300 ms 仍无音频再补，只救真正卡住的那次。
        /// </summary>
        public const int MicOpenNudgeMs = 300;

        private readonly IAtvvTransport _transport;
        private readonly AdpcmDecoder _decoder = new AdpcmDecoder();
        private readonly HashSet<int> _stopped = new HashSet<int>();
        private readonly object _sync = new object();

        private readonly int _extendIntervalMs;
        private readonly int _tailWindowMs;
        private readonly int _firstAudioTimeoutMs;
        private readonly int _silenceTimeoutMs;
        private readonly int _micOpenNudgeMs;

        private bool _armed;
        /// <summary>
        /// 采集窗口是否开着。这是音频门控的唯一判据（接缝不变量 1）。
        /// 它同时覆盖 streaming 与 0x00 之后的收尾窗口 —— 收尾期间迟到的一帧仍属本段
        /// （不变量 2），所以两者不该分成两个标志。
        /// </summary>
        private bool _capturing;
        private int _sessionId;
        private bool _hasSession;
        private bool _extendFailed;     // 最近一次续期是否失败 ⇒ end 时判 aborted
        private bool _gotAudio;         // 本段是否收到过音频 ⇒ 判 timeout
        private Timer _extendTimer;
        private Timer _tailTimer;
        private Timer _timeoutTimer;
        private Timer _silenceTimer;    // 看门狗：每来一帧就推迟，到期 ⇒ 设备已停流
        private Timer _nudgeTimer;      // 开麦后迟迟无音频 ⇒ 补发一次 MicOpen
        private bool _micOpenSent;      // 本次会话是否已写过 MicOpen（不重复写）

        /// <summary>采集窗口开始。参数 = source（当前恒为 remote）。</summary>
        public event Action<string> CaptureStarted;
        /// <summary>采集窗口结束。参数 = reason, source。</summary>
        public event Action<string, string> CaptureEnded;
        /// <summary>一段音频帧解码后的 PCM16 样本。</summary>
        public event Action<short[]> AudioDecoded;
        /// <summary>故障（code, 诊断消息）。local-mic 据此发结构化 error。</summary>
        public event Action<string, string> Failed;
        /// <summary>诊断信息，不进接缝，只写日志/自检输出。</summary>
        public event Action<string> Warned;

        public AtvvSession(IAtvvTransport transport)
            : this(transport, ExtendIntervalMs, TailWindowMs, FirstAudioTimeoutMs, SilenceTimeoutMs)
        {
        }

        /// <summary>仅供自检：把时间常数缩短，让测试不用真等几秒。</summary>
        internal AtvvSession(IAtvvTransport transport, int extendIntervalMs, int tailWindowMs, int firstAudioTimeoutMs)
            : this(transport, extendIntervalMs, tailWindowMs, firstAudioTimeoutMs, SilenceTimeoutMs)
        {
        }

        internal AtvvSession(IAtvvTransport transport, int extendIntervalMs, int tailWindowMs,
                             int firstAudioTimeoutMs, int silenceTimeoutMs)
            : this(transport, extendIntervalMs, tailWindowMs, firstAudioTimeoutMs, silenceTimeoutMs, MicOpenNudgeMs)
        {
        }

        internal AtvvSession(IAtvvTransport transport, int extendIntervalMs, int tailWindowMs,
                             int firstAudioTimeoutMs, int silenceTimeoutMs, int micOpenNudgeMs)
        {
            _transport = transport;
            _extendIntervalMs = extendIntervalMs;
            _tailWindowMs = tailWindowMs;
            _firstAudioTimeoutMs = firstAudioTimeoutMs;
            _silenceTimeoutMs = silenceTimeoutMs;
            _micOpenNudgeMs = micOpenNudgeMs;
        }

        public bool IsCapturing { get { lock (_sync) { return _capturing; } } }
        public Caps Caps { get; private set; }
        public int FrameSize { get { return Caps != null && Caps.FrameSize > 0 ? Caps.FrameSize : Atvv.DefaultFrameSize; } }
        public int ResetCount { get { return _decoder.ResetCount; } }

        /// <summary>
        /// 允许开始处理事件。连接初期设备会吐遗留通知（实测刚连上就收到 04 03 02 11），
        /// 不先静默一段就会被误判成「用户已按下」。
        /// </summary>
        public void Arm()
        {
            lock (_sync) { _armed = true; }
        }

        public void Disarm()
        {
            lock (_sync) { _armed = false; }
            StopTimers();
        }

        /// <summary>连接断开或切换设备：结束进行中的采集，reason 由调用方给。</summary>
        public void Abort(string reason)
        {
            bool wasCapturing;
            lock (_sync)
            {
                wasCapturing = _capturing;
                _capturing = false;
            }
            StopTimers();
            if (wasCapturing) RaiseCaptureEnded(reason);
        }

        private void StopTimers()
        {
            // 锁内清理：与 OnAudio 的喂狗互斥，避免出现「刚 Change 就被 Dispose」的竞态
            lock (_sync)
            {
                StopTimersLocked();
            }
        }

        private void StopTimersLocked()
        {
            if (_extendTimer != null) { _extendTimer.Dispose(); _extendTimer = null; }
            if (_tailTimer != null) { _tailTimer.Dispose(); _tailTimer = null; }
            if (_timeoutTimer != null) { _timeoutTimer.Dispose(); _timeoutTimer = null; }
            if (_silenceTimer != null) { _silenceTimer.Dispose(); _silenceTimer = null; }
            if (_nudgeTimer != null) { _nudgeTimer.Dispose(); _nudgeTimer = null; }
        }

        /// <summary>CONTROL 特征通知。同步入口，写设备的动作异步发出。</summary>
        public void OnControl(byte[] b)
        {
            if (b == null || b.Length == 0) return;
            bool armedNow;
            lock (_sync) { armedNow = _armed; }
            if (!armedNow)
            {
                // Arm 前的遗留通知：这里必须留痕，否则「用户还没按就开始了」无从查起
                DebugLog.State("control-ignored", "未 Arm，丢弃 0x" + b[0].ToString("x2"));
                return;
            }

            byte op = b[0];
            switch (op)
            {
                case Atvv.OpCaps:
                    OnCaps(b);
                    break;

                case Atvv.OpDecoderSync:
                    // predictor = int16 BE @[4..6]，step_index @[6]
                    short predictor = Int16BE(b, 4);
                    byte stepIndex = b.Length > 6 ? b[6] : (byte)0;
                    lock (_sync) { _decoder.Reset(predictor, stepIndex); }
                    DebugLog.State("decoder-sync", "predictor=" + predictor + " stepIndex=" + stepIndex);
                    break;

                case Atvv.OpMicOpenRequested:
                    DebugLog.State("mic-open-requested", "设备请求开麦 ⇒ 回写 0x0C");
                    lock (_sync) { _micOpenSent = true; }   // 已写过，nudge 不必再补
                    FireWrite(new byte[] { 0x0C, 0x00 }, "MicOpen");
                    break;

                case Atvv.OpStreamStarted:
                    OnStreamStarted(b);
                    break;

                case Atvv.OpStreamStopped:
                    OnStreamStopped(b);
                    break;

                default:
                    // 未知 opcode 不能静默吞掉：设备若用别的码表示松手，这里就是唯一的线索
                    DebugLog.State("control-unknown", "未知 opcode 0x" + op.ToString("x2") + "，已忽略");
                    break;
            }
        }

        private void OnCaps(byte[] b)
        {
            var caps = Atvv.ParseCaps(b);
            lock (_sync) { Caps = caps; }
            if (!caps.Supports16k)
                RaiseFailed("unsupported_device", "Caps 显示 16 kHz 不可用：" + caps.Summary);
        }

        private void OnStreamStarted(byte[] b)
        {
            int sid = b.Length > 1 ? b[1] : 0;
            bool hasSession = b.Length > 1;

            bool begin;
            lock (_sync)
            {
                // 固件会重复发 0x04；同一 session 只认第一次
                if (_capturing && hasSession && _hasSession && _sessionId == sid)
                {
                    DebugLog.State("start-ignored", "同一 session 的重复 0x04，sid=" + sid);
                    return;
                }
                _capturing = true;
                _sessionId = sid;
                _hasSession = hasSession;
                _extendFailed = false;
                _gotAudio = false;
                // 注意：_micOpenSent **不在这里重置**。标准流程是 0x08 → 回 MicOpen → 0x04，
                // 紧跟的 0x04 属于同一次按下；若在这里清零，nudge 会再补发一次 0x0C 打断流。
                // 它只在一段真正结束时（FinishCapture）清零。
                _stopped.Remove(sid);
                begin = true;
            }
            if (!begin) return;

            DebugLog.State("capture-start", "session=" + (_hasSession ? _sessionId.ToString() : "(无)"));
            DebugLog.MarkPress();
            RaiseCaptureStarted();

            lock (_sync)
            {
                if (_extendTimer != null) _extendTimer.Dispose();
                _extendTimer = new Timer(OnExtendTick, null, _extendIntervalMs, _extendIntervalMs);
                if (_timeoutTimer != null) _timeoutTimer.Dispose();
                _timeoutTimer = new Timer(OnFirstAudioTimeout, null, _firstAudioTimeoutMs, Timeout.Infinite);
                if (_nudgeTimer != null) _nudgeTimer.Dispose();
                _nudgeTimer = new Timer(OnMicOpenNudge, null, _micOpenNudgeMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 开麦后迟迟无音频 ⇒ 补发一次 MicOpen。
        /// 固件跳过了 0x08，0x04 兼有"请求开麦"语义，设备要收到 0x0C 才真正送流
        /// （真机时序见 MicOpenNudgeMs 的注释）。不补发 ⇒ 按下去没声音，只能等 timeout。
        /// </summary>
        private void OnMicOpenNudge(object _)
        {
            bool need;
            lock (_sync)
            {
                need = _capturing && !_gotAudio && !_micOpenSent;
                if (need) _micOpenSent = true;
            }
            if (!need) return;

            DebugLog.State("mic-open-nudge", "开麦后 " + _micOpenNudgeMs + " ms 仍无音频 ⇒ 补发 MicOpen");
            FireWrite(new byte[] { 0x0C, 0x00 }, "MicOpen(补发)");
        }

        private void OnStreamStopped(byte[] b)
        {
            int sid = b.Length > 1 ? b[1] : 0;
            bool fresh;
            lock (_sync)
            {
                if (!_capturing)
                {
                    // 「0x00 来了但窗口已关」正是这次要找的线索 —— 说明它迟到了
                    DebugLog.State("stop-ignored", "0x00 到达但采集窗口已关（迟到，或已被看门狗结束）");
                    return;
                }
                if (_stopped.Contains(sid))
                {
                    DebugLog.State("stop-ignored", "重复的 0x00，sid=" + sid);
                    return;
                }
                _stopped.Add(sid);
                DebugLog.State("stop", "0x00 收下，进入 " + _tailWindowMs + " ms 收尾窗口，sid=" + sid);

                // 进入收尾窗口：AUDIO 与 CONTROL 是两个独立 GATT 特征，到达顺序不保证，
                // 最后一个音频帧可能排在 0x00 之后才到（PROTOCOL.md §12.2）。
                // _capturing 保持为 true，迟到的一帧因此仍属本段。
                fresh = true;
            }
            if (!fresh) return;

            BeginFinish(CurrentReason());
        }

        private string CurrentReason()
        {
            lock (_sync)
            {
                return _extendFailed ? "aborted"
                     : (!_gotAudio ? "timeout" : "released");
            }
        }

        /// <summary>
        /// 进入收尾窗口并到期封段。0x00 与静默看门狗共用这一条出口，
        /// 保证「谁先到谁生效、另一个被 !_capturing 挡掉」。
        /// </summary>
        private void BeginFinish(string reason)
        {
            StopTimers();
            lock (_sync)
            {
                if (_tailTimer != null) _tailTimer.Dispose();
                _tailTimer = new Timer(s => FinishCapture(reason), null, _tailWindowMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 静默看门狗到期：设备已停流，但 0x00 没按时到（真机实测会迟到到下次按键）。
        /// 没有这条兜底，松手后采集窗口不会关，转写永远不会出结果。
        /// </summary>
        private void OnSilence(object _)
        {
            bool hit;
            lock (_sync) { hit = _capturing && _gotAudio; }
            if (!hit) return;

            RaiseWarned("音频静默 " + _silenceTimeoutMs + " ms ⇒ 判定已停流（0x00 未按时到达）");
            DebugLog.State("silence-watchdog", "音频停够 " + _silenceTimeoutMs + " ms ⇒ 判定停流");
            BeginFinish(CurrentReason());
        }

        /// <summary>收尾窗口结束：封段并回 MicClose。</summary>
        private void FinishCapture(string reason)
        {
            int sid;
            bool hasSession;
            lock (_sync)
            {
                if (!_capturing) return;
                _capturing = false;
                _micOpenSent = false;      // 这一段结束了，下次按下要重新开麦
                sid = _sessionId;
                hasSession = _hasSession;
            }
            StopTimers();
            // 只记 reason，不再另说「段内有无音频」——后者在收尾窗口期间会被迟到的一帧
            // 改写，出现「reason=timeout 而段内有音频」这种自相矛盾的日志，误导排查。
            DebugLog.State("capture-end", "reason=" + reason + " ⇒ 回写 MicClose sid=" + sid);
            DebugLog.MarkEnd();
            RaiseCaptureEnded(reason);
            if (hasSession) FireWrite(new byte[] { 0x0D, (byte)sid }, "MicClose");
        }

        private void OnExtendTick(object _)
        {
            int sid;
            bool hasSession;
            lock (_sync)
            {
                if (!_capturing) return;
                sid = _sessionId;
                hasSession = _hasSession;
            }
            if (!hasSession) return;

            var ignored = Task.Run(async () =>
            {
                string err;
                try { err = await _transport.WriteAsync(new byte[] { 0x0E, (byte)sid }).ConfigureAwait(false); }
                catch (Exception e) { err = e.Message; }

                if (err != null)
                {
                    // 续期失败 ⇒ 设备会在 5.7 秒窗口到期时掐断音频流，
                    // 而掐断发的 0x00 与用户松手的字节完全相同 —— 只能靠这里记住。
                    lock (_sync) { _extendFailed = true; }
                    DebugLog.State("extend-failed", "MicExtend 写入失败：" + err);
                    RaiseFailed("write_failed", "MicExtend 写入失败：" + err);
                }
            });
        }

        private void OnFirstAudioTimeout(object _)
        {
            bool hit;
            lock (_sync) { hit = _capturing && !_gotAudio; }
            if (hit)
            {
                DebugLog.State("timeout", "开麦后 " + _firstAudioTimeoutMs + " ms 内零音频 ⇒ 放弃本段");
                Abort("timeout");
            }
        }

        /// <summary>
        /// AUDIO 特征通知。返回解码后的 PCM16 样本；**窗口外返回 null**（接缝不变量 1：
        /// 窗口外的音频不得出现在接缝上）。遥控器场景下设备自己会门控，但常采设备必须靠这里。
        /// </summary>
        public short[] OnAudio(byte[] b)
        {
            if (b == null || b.Length == 0) return null;
            short[] pcm;
            lock (_sync)
            {
                if (!_armed || !_capturing)
                {
                    // 门控丢弃要留痕：若松手后仍在大量丢帧，症状会指向别处
                    DebugLog.State("audio-dropped", !_armed ? "未 Arm" : "采集窗口已关");
                    return null;
                }
                _gotAudio = true;

                // 喂狗。只在**首帧之后**才起看门狗：开麦后一帧都没有的情况仍归
                // FirstAudioTimeout 判 timeout，两条超时各管一件事，别混。
                if (_silenceTimer == null)
                    _silenceTimer = new Timer(OnSilence, null, _silenceTimeoutMs, Timeout.Infinite);
                else
                    _silenceTimer.Change(_silenceTimeoutMs, Timeout.Infinite);

                pcm = _decoder.DecodeFrame(b);
            }
            RaiseAudioDecoded(pcm);
            return pcm;
        }

        private void FireWrite(byte[] data, string what)
        {
            var ignored = Task.Run(async () =>
            {
                string err;
                try { err = await _transport.WriteAsync(data).ConfigureAwait(false); }
                catch (Exception e) { err = e.Message; }
                if (err != null) RaiseWarned(what + " 写入失败：" + err);
            });
        }

        // ---------- 事件派发（一律在锁外，避免重入死锁） ----------

        private void RaiseCaptureStarted()
        {
            var h = CaptureStarted;
            if (h != null) h("remote");
        }

        private void RaiseCaptureEnded(string reason)
        {
            var h = CaptureEnded;
            if (h != null) h(reason, "remote");
        }

        private void RaiseAudioDecoded(short[] pcm)
        {
            var h = AudioDecoded;
            if (h != null) h(pcm);
        }

        private void RaiseFailed(string code, string message)
        {
            var h = Failed;
            if (h != null) h(code, message);
        }

        private void RaiseWarned(string message)
        {
            var h = Warned;
            if (h != null) h(message);
        }

        private static short Int16BE(byte[] b, int off)
        {
            if (b.Length < off + 2) return 0;
            int v = (b[off] << 8) | b[off + 1];
            return (short)(v > 0x7fff ? v - 0x10000 : v);
        }

        // ------------------------------------------------------------------
        // 无硬件自检：解码归 local-mic 后，生产状态机必须有自己的回归
        // ------------------------------------------------------------------

        /// <summary>跑会话级用例。返回报告，最后一行是结论。</summary>
        internal static string RunSelfTest()
        {
            var sb = new StringBuilder();
            int total = 0, failed = 0;

            Action<string, bool, string> check = (name, ok, detail) =>
            {
                total++;
                if (!ok) failed++;
                sb.Append(ok ? "  OK    " : "  失败  ").Append(name);
                if (detail != null && detail.Length > 0) sb.Append("  —— ").Append(detail);
                sb.AppendLine();
            };

            // ---- 1. 0x08 必须回 MicOpen ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                s.OnControl(new byte[] { 0x08, 0x03 });
                WaitWrites(t, 1);
                check("0x08 MicOpenRequested ⇒ 回写 0x0C", t.Has(0x0C), t.Dump());
                s.Disarm();
            }

            // ---- 2. 0x04 开始采集 + 门控放通 ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                string started = null;
                s.CaptureStarted += src => started = src;
                s.OnControl(new byte[] { 0x04, 0x07 });
                check("0x04 ⇒ 采集窗口开始", started == "remote", "source=" + (started ?? "(未触发)"));
                check("开始后 IsCapturing = true", s.IsCapturing, "");

                var pcm = s.OnAudio(new byte[] { 0x08, 0x08 });
                check("窗口内音频解码出 PCM", pcm != null && pcm.Length == 4,
                      pcm == null ? "(被门控丢弃)" : "样本数 " + pcm.Length);
                s.Disarm();
            }

            // ---- 3. 🔴 0x0A DecoderSync 必须驱动 Reset ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                int before = s.ResetCount;
                s.OnControl(new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x12, 0x34, 0x05 });
                check("0x0A ⇒ 解码器状态被重置", s.ResetCount == before + 1,
                      "ResetCount " + before + " → " + s.ResetCount);
                s.Disarm();
            }

            // ---- 4. 窗口外音频必须被丢弃（不变量 1） ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                var pcm = s.OnAudio(new byte[] { 0x08, 0x08 });
                check("未开始时的音频不得上接缝", pcm == null, pcm == null ? "" : "泄漏 " + pcm.Length + " 样本");
                s.Disarm();
            }

            // ---- 5. 0x00 去重 + 收尾窗口 ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                var ends = new List<string>();
                s.CaptureEnded += (reason, src) => ends.Add(reason);
                s.OnControl(new byte[] { 0x04, 0x07 });
                s.OnAudio(new byte[] { 0x08, 0x08 });
                s.OnControl(new byte[] { 0x00, 0x07 });
                s.OnControl(new byte[] { 0x00, 0x07 });   // 重复
                s.OnControl(new byte[] { 0x00, 0x07 });   // 再重复
                Thread.Sleep(150);
                check("0x00 重复去重 ⇒ 只结束一次", ends.Count == 1, "实际 " + ends.Count + " 次");
                check("松手 reason = released", ends.Count == 1 && ends[0] == "released",
                      ends.Count > 0 ? ends[0] : "(无)");
                WaitWrites(t, 1);
                check("结束后回写 0x0D MicClose", t.Has(0x0D), t.Dump());
                s.Disarm();
            }

            // ---- 6. 收尾窗口期间音频仍属本段（不变量 2） ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 120, 200000);
                s.Arm();
                s.OnControl(new byte[] { 0x04, 0x07 });
                s.OnControl(new byte[] { 0x00, 0x07 });
                var pcm = s.OnAudio(new byte[] { 0x08, 0x08 });   // 排在 0x00 之后到达
                check("收尾窗口内的迟到音频仍被接收", pcm != null && pcm.Length == 4,
                      pcm == null ? "(被丢弃 ⇒ 每句话结尾丢半个字)" : "样本数 " + pcm.Length);
                Thread.Sleep(250);
                check("收尾窗口结束后 IsCapturing = false", !s.IsCapturing, "");
                s.Disarm();
            }

            // ---- 7. 续期失败 ⇒ aborted（不能谎报 released） ----
            {
                var t = new FakeTransport { FailNext = true };
                var s = new AtvvSession(t, 60, 30, 200000);
                s.Arm();
                var ends = new List<string>();
                s.CaptureEnded += (reason, src) => ends.Add(reason);
                string code = null;
                s.Failed += (c, m) => code = c;
                s.OnControl(new byte[] { 0x04, 0x07 });
                s.OnAudio(new byte[] { 0x08, 0x08 });
                Thread.Sleep(250);                       // 等续期失败
                s.OnControl(new byte[] { 0x00, 0x07 });
                Thread.Sleep(120);
                check("续期失败 ⇒ 报 write_failed", code == "write_failed", "code=" + (code ?? "(无)"));
                check("续期失败后结束 reason = aborted", ends.Count == 1 && ends[0] == "aborted",
                      ends.Count > 0 ? ends[0] : "(无)");
                s.Disarm();
            }

            // ---- 8. 开麦后一直没音频 ⇒ timeout ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 100);
                s.Arm();
                var ends = new List<string>();
                s.CaptureEnded += (reason, src) => ends.Add(reason);
                s.OnControl(new byte[] { 0x04, 0x07 });
                Thread.Sleep(250);
                check("开麦后零音频 ⇒ timeout", ends.Count == 1 && ends[0] == "timeout",
                      ends.Count > 0 ? ends[0] : "(无，会空转写)");
                s.Disarm();
            }

            // ---- 9. Caps 不支持 16k ⇒ unsupported_device ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                s.Arm();
                string code = null;
                s.Failed += (c, m) => code = c;
                // version=0x0100, codecs=0x00（16k 位为 0）⇒ 不支持
                s.OnControl(new byte[] { 0x0B, 0x01, 0x00, 0x00, 0x00, 0x00, 0x78 });
                check("Caps 非 16k ⇒ unsupported_device", code == "unsupported_device", "code=" + (code ?? "(无)"));
                s.Disarm();
            }

            // ---- 10. Arm 之前忽略遗留通知 ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000);
                string started = null;
                s.CaptureStarted += src => started = src;
                s.OnControl(new byte[] { 0x04, 0x03 });   // 连接初期的遗留 04
                check("Arm 前忽略遗留通知", started == null && !s.IsCapturing,
                      started == null ? "" : "被误判成用户已按下");
                s.Disarm();
            }

            // ---- 11. 🔴 0x00 迟到 ⇒ 静默看门狗必须兜底结束 ----
            // 真机症状：松手后采集窗口不关，要等下次按键才出结果（0x00 随连接激活才投递）。
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000, 100);   // 静默阈值缩到 100 ms
                s.Arm();
                var ends = new List<string>();
                s.CaptureEnded += (reason, src) => ends.Add(reason);
                s.OnControl(new byte[] { 0x04, 0x07 });
                s.OnAudio(new byte[] { 0x08, 0x08 });
                Thread.Sleep(60);
                s.OnAudio(new byte[] { 0x08, 0x08 });       // 还在说话 ⇒ 窗口必须保持
                check("持续来帧时不得静默结束", ends.Count == 0, "已结束 " + ends.Count + " 次（会截断说话）");

                Thread.Sleep(400);                          // 停流，0x00 始终不来
                check("停流后静默超时 ⇒ 自动结束", ends.Count == 1 && ends[0] == "released",
                      ends.Count > 0 ? ends[0] : "(无 ⇒ 松手后永远不出结果)");

                s.OnControl(new byte[] { 0x00, 0x07 });     // 迟到的 0x00
                Thread.Sleep(150);
                check("迟到的 0x00 不得二次结束", ends.Count == 1, "实际 " + ends.Count + " 次");
                WaitWrites(t, 1);
                check("静默结束后仍回写 0x0D MicClose", t.Has(0x0D), t.Dump());
                s.Disarm();
            }

            // ---- 12. 静默看门狗不得取代「开麦后零音频」的 timeout ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 100, 40);       // 静默 40ms < 首帧超时 100ms
                s.Arm();
                var ends = new List<string>();
                s.CaptureEnded += (reason, src) => ends.Add(reason);
                s.OnControl(new byte[] { 0x04, 0x07 });
                Thread.Sleep(250);
                check("一帧都没有 ⇒ 仍报 timeout 而非 released",
                      ends.Count == 1 && ends[0] == "timeout", ends.Count > 0 ? ends[0] : "(无)");
                s.Disarm();
            }

            // ---- 13. 🔴 0x04 后迟迟无音频 ⇒ 必须补发 MicOpen ----
            // 真机：按下 → 0x04 →（主机回 MicOpen）→ 第二个 0x04 → 音频才来。
            // 不补发 ⇒ 插件上线后按下去没声音，只能等 timeout。
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000, 100000, 80);   // nudge 80 ms
                s.Arm();
                s.OnControl(new byte[] { 0x04, 0x07 });
                Thread.Sleep(60);
                check("刚开麦就补发会打断流 ⇒ 先不写", !t.Has(0x0C), t.Dump());
                Thread.Sleep(200);
                check("开麦后仍无音频 ⇒ 补发 0x0C MicOpen", t.Has(0x0C), t.Dump());

                s.OnAudio(new byte[] { 0x08, 0x08 });
                int after = t.Count(0x0C);
                Thread.Sleep(250);
                check("已有音频后不再重复补发", t.Count(0x0C) == after, "0x0C 写了 " + t.Count(0x0C) + " 次");
                s.Disarm();
            }

            // ---- 14. 走过 0x08（已发 MicOpen）后不得再补发 ----
            {
                var t = new FakeTransport();
                var s = new AtvvSession(t, 100000, 30, 200000, 100000, 80);
                s.Arm();
                s.OnControl(new byte[] { 0x08, 0x03 });
                WaitWrites(t, 1);
                s.OnControl(new byte[] { 0x04, 0x03 });
                Thread.Sleep(300);
                check("0x08 已回 MicOpen ⇒ nudge 不重复写", t.Count(0x0C) == 1, "0x0C 写了 " + t.Count(0x0C) + " 次");
                s.Disarm();
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0
                ? string.Format(CultureInfo.InvariantCulture, "会话自检通过：{0}/{1} 条用例通过", total, total)
                : string.Format(CultureInfo.InvariantCulture, "会话自检失败：{0}/{1} 条不通过", failed, total));
            return sb.ToString();
        }

        private static void WaitWrites(FakeTransport t, int n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000 && t.Writes.Count < n) Thread.Sleep(10);
        }

        private sealed class FakeTransport : IAtvvTransport
        {
            public readonly List<byte[]> Writes = new List<byte[]>();
            public bool FailNext;

            public Task<string> WriteAsync(byte[] data)
            {
                lock (Writes)
                {
                    Writes.Add(data);
                    if (FailNext) { FailNext = false; return Task.FromResult("模拟写入失败"); }
                }
                return Task.FromResult<string>(null);
            }

            public bool Has(byte op)
            {
                lock (Writes)
                {
                    foreach (var w in Writes) if (w.Length > 0 && w[0] == op) return true;
                }
                return false;
            }

            public int Count(byte op)
            {
                int n = 0;
                lock (Writes)
                {
                    foreach (var w in Writes) if (w.Length > 0 && w[0] == op) n++;
                }
                return n;
            }

            public string Dump()
            {
                lock (Writes)
                {
                    var sb = new StringBuilder("写出的命令：");
                    foreach (var w in Writes) sb.Append("0x").Append(w[0].ToString("x2")).Append(' ');
                    return sb.ToString().TrimEnd();
                }
            }
        }
    }
}
