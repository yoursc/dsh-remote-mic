using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshRemoteMic
{
    /// <summary>
    /// 编排层：把 BleRemote 收到的设备字节，经 ATVV 会话机翻译成**设备无关**的
    /// 采集窗口事件与 PCM16 样本，再发给客户端（PROTOCOL.md proto 1）。
    /// 也提供不依赖浏览器的自检（托盘里的「测试连接 / 测试音频」）。
    ///
    /// 与 proto 0 最大的不同：这里不再是「原样转发原始字节」的哑管道。
    /// ATVV opcode、session_id、ADPCM 全部止步于 AtvvSession ——
    /// 接缝上只有 capture 事件与 PCM16。
    /// </summary>
    internal sealed class LocalMic : IDisposable
    {
        /// <summary>线缆协议版本。改动 schema 时递增（PROTOCOL.md §6）。</summary>
        public const int ProtoVersion = 1;

        private readonly BleRemote _ble = new BleRemote();
        private readonly AtvvSession _session;
        private WsServer _ws;
        private int _port;
        private Timer _timer;
        private int _tick;
        private int _working;

        private ulong _address;
        private string _deviceName = "";
        private string _state = "disconnected";
        private string _detail = "未启动";
        private bool _lowNotified;
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);

        // 自检独占：自检期间会话机让位，由自检自己解释 CONTROL 字节。
        // volatile：BLE 通知线程与 UI 线程都会读它。
        private volatile bool _exclusive;

        // 状态语义（§8.1.1 / §9.3）：只在取值变化时推，避免重连定时器把客户端刷屏
        private string _broadcastStateKey;
        private string _errorCode;
        private string _errorMessage;
        private bool _errorRetryable;

        public event Action<string, string> StateChanged;   // state, detail
        public event Action<byte[]> ControlFrame;           // 原始 CONTROL 字节（自检/诊断用）
        public event Action<byte[]> AudioFrame;             // PCM16 s16le 字节（一帧一块）
        public event Action<byte[]> RawAudioFrame;          // 原始 ADPCM 帧（自检存 .bin 用）
        public event Action<DeviceInfo> DeviceIdentified;
        public event Action<int> BatteryUpdated;   // 百分比，-1 表示未知
        public event Action<int> LowBattery;       // 跨过低电量阈值时触发，每次连接只提醒一次
        /// <summary>客户端连上或断开。界面据此刷新「浏览器在线」计数。</summary>
        public event Action ClientListChanged;

        /// <summary>低于这个值提醒一次充电。遥控器没电会导致说话完全没反应 —— 这类静默失败最难排查。</summary>
        public const int LowBatteryThreshold = 20;

        public string State { get { return _state; } }
        public string Detail { get { return _detail; } }
        public bool IsConnected { get { return _ble.IsConnected; } }
        public bool IsPaired { get { return _ble.IsPaired; } }
        public int ClientCount { get { return _ws != null ? _ws.ClientCount : 0; } }
        /// <summary>实际在监听的端口（改端口失败时它仍是旧的那个，不是用户填的那个）。</summary>
        public int Port { get { return _port; } }
        public DeviceInfo Info { get; private set; }
        public int Battery { get; private set; }
        public DateTime BatteryAt { get; private set; }

        public LocalMic()
        {
            Battery = -1;                          // -1 = 尚未读到
            BatteryAt = DateTime.MinValue;
            _port = Config.Port;                   // 先有值，界面在 Start 之前创建也不会显示 0

            _session = new AtvvSession(_ble);
            _session.CaptureStarted += OnCaptureStarted;
            _session.CaptureEnded += OnCaptureEnded;
            _session.AudioDecoded += OnAudioDecoded;
            _session.Failed += OnSessionFailed;
        }

        public string DeviceMac
        {
            get
            {
                var s = _address.ToString("X12");
                var sb = new StringBuilder();
                for (int i = 0; i < 12; i += 2) { if (i > 0) sb.Append(':'); sb.Append(s.Substring(i, 2)); }
                return sb.ToString();
            }
        }

        /// <summary>GAP 里的设备名（如「小米蓝牙语音遥控器」）。</summary>
        public string DeviceName { get { return string.IsNullOrEmpty(_deviceName) ? DeviceMac : _deviceName; } }

        /// <summary>
        /// 给用户看的名字：命中型号白名单时用友好名（「小米蓝牙遥控器 2 Pro」），
        /// 认不出来才回落到 GAP 设备名，并在界面上标注未验证。
        /// </summary>
        public string DisplayName
        {
            get
            {
                var info = Info;
                if (info != null && !string.IsNullOrEmpty(info.DisplayName)) return info.DisplayName;
                return DeviceName;
            }
        }

        public void Start()
        {
            _address = Config.Address;
            if (_address == 0)
            {
                var found = DeviceFinder.FindRemote();
                if (found != null) { _address = found.Address; _deviceName = found.Name; }
            }

            _port = Config.Port;
            try
            {
                _ws = CreateWs(_port);
                _ws.Start();
            }
            catch (Exception e)
            {
                // port_in_use 不在接缝错误码表里（PROTOCOL.md §9.1）：服务端没起来 ⇒
                // 没有连接能承载这条消息 ⇒ 它只是本地故障，由托盘 UI 呈现。
                // 但"被占用"必须说清楚：否则用户只看到一串 Socket 异常，不知道换个端口就行。
                SetState("error", "WebSocket 端口 " + _port + " 启动失败："
                    + (IsPortInUse(_port) ? "已被别的程序占用，请在下面换个端口" : e.Message));
            }

            // proto 1：控制字节不再上接缝（0x01 帧已删），只喂给会话机翻译成 capture 事件
            _ble.ControlReceived += b => { Raise(ControlFrame, b); if (!_exclusive) _session.OnControl(b); };
            // 会话机负责门控与 ADPCM 解码，出来已经是 PCM16
            _ble.AudioReceived += b => { Raise(RawAudioFrame, b); if (!_exclusive) _session.OnAudio(b); };
            _ble.ConnectionChanged += OnConnectionChanged;
            _ble.DeviceInfoReady += OnDeviceInfo;
            _ble.BatteryChanged += OnBattery;

            // 有浏览器在线时积极重连（5s）；否则 30s 探一次，只为让托盘状态保持真实
            _timer = new Timer(Tick, null, 1500, 5000);

            if (_address == 0)
                // retryable=true：local-mic 会一直重试，遥控器开了就能自愈。
                // 客户端据此「安静等待」，而不是弹一个吓人的错误框（§9.2）。
                SetError("device_not_found", true, "未找到遥控器，请先在 Windows 里配对");
            else
            {
                SetState("disconnected", "等待连接 " + DeviceMac);
                // 启动后立刻探一次。否则要等 Tick 轮满 6 拍（约 30 秒）才第一次尝试，
                // 开机自启的用户会盯着一个灰色图标怀疑它坏了。
                Task.Run(() => EnsureConnectedAsync());
            }
        }

        private void Raise(Action<byte[]> h, byte[] b)
        {
            if (h != null) h(b);
        }

        private void RaiseOf<T>(Action<T> h, T v)
        {
            if (h != null) h(v);
        }

        private void OnDeviceInfo(DeviceInfo info)
        {
            Info = info;
            RaiseOf(DeviceIdentified, info);
            BroadcastDevice();      // §7：读到 DIS 是 device 变化的发送时机之一
        }

        /// <summary>
        /// 电量更新。低于阈值提醒一次；回升过阈值后再下降允许再次提醒 ——
        /// 否则充完电再用一整天，第二次跌破阈值就不会有任何提示了。
        /// </summary>
        private void OnBattery(int level)
        {
            if (level < 0) return;
            Battery = level;
            BatteryAt = DateTime.Now;
            RaiseOf(BatteryUpdated, level);
            BroadcastDevice();      // 电量推送（BAS Notify）也是 device 变化的时机

            if (level <= LowBatteryThreshold)
            {
                if (!_lowNotified)
                {
                    _lowNotified = true;
                    RaiseOf(LowBattery, level);
                }
            }
            else
            {
                _lowNotified = false;
            }
        }

        /// <summary>PCM16 样本 → s16le 字节 → 音频帧（kind 0x02）。只发给已握手且订阅的连接。</summary>
        private void BroadcastPcm(short[] pcm)
        {
            if (_ws == null || pcm == null || pcm.Length == 0) return;
            var buf = new byte[pcm.Length * 2];
            for (int i = 0; i < pcm.Length; i++)
            {
                buf[i * 2] = (byte)(pcm[i] & 0xFF);
                buf[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xFF);
            }
            _ws.BroadcastAudio(buf);
        }

        // ---------- 会话机 → 接缝 ----------

        private WsServer CreateWs(int port)
        {
            var ws = new WsServer(port);
            ws.ClientOpened += OnClientOpened;
            ws.TextReceived += OnTextReceived;
            ws.BinaryReceived += OnBinaryReceived;
            // 客户端上下线不带任何状态变化，若不单独通知，界面上的「浏览器在线」会一直停在 0
            // —— 明明连上了却显示没人，比连不上更让人怀疑（真机 2026-09-29）。
            ws.ClientOpened += c => RaiseClientListChanged();
            ws.ClientClosed += c => RaiseClientListChanged();
            return ws;
        }

        private void RaiseClientListChanged()
        {
            var h = ClientListChanged;
            if (h != null) h();
        }

        /// <summary>
        /// 改监听端口。成功返回 null；失败返回原因，**且旧端口继续工作** ——
        /// 换端口失败不该把 WebSocket 一起弄没了（那就真"完蛋了"）。
        /// </summary>
        public string SetPort(int port)
        {
            if (port < 1 || port > 65535) return "端口要在 1–65535 之间";
            if (port == _port) return null;

            string busy = IsPortInUse(port) ? "端口 " + port + " 已被别的程序占用" : null;

            try
            {
                var ws = CreateWs(port);
                ws.Start();                       // 先起新的，成功了才动旧的
                var old = _ws;
                _ws = ws;
                _port = port;
                Config.Port = port;               // 只在起得来之后落盘，别存一个起不来的端口
                if (old != null) { try { old.Dispose(); } catch { } }
                return null;
            }
            catch (Exception e)
            {
                return busy ?? ("端口 " + port + " 启动失败：" + e.Message);
            }
        }

        /// <summary>
        /// 端口占用预检。只为把「被占用」从一串 Socket 异常里挑出来说人话，
        /// 不作为能否启动的判据（预检之后仍可能失败，也可能误判）。
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                l.Start();
                l.Stop();
                return false;
            }
            catch (SocketException) { return true; }
            catch { return false; }
        }

        private void OnCaptureStarted(string source)
        {
            DebugLog.State("ws-capture", "发出 start source=" + source);
            if (_ws != null) _ws.BroadcastText(MiniJson.Capture("start", null, source));
        }

        private void OnCaptureEnded(string reason, string source)
        {
            // 这一条是「松手有没有被识别到结束」的直接证据：没有它，问题在 local-mic；
            // 有它而界面没反应，问题在客户端。
            DebugLog.State("ws-capture", "发出 end reason=" + reason + " source=" + source);
            if (_ws != null) _ws.BroadcastText(MiniJson.Capture("end", reason, source));
        }

        private void OnAudioDecoded(short[] pcm)
        {
            Raise(AudioFrame, PcmToBytes(pcm));
            BroadcastPcm(pcm);
        }

        private void OnSessionFailed(string code, string message)
        {
            SetError(code, IsRetryable(code), message);
        }

        private static byte[] PcmToBytes(short[] pcm)
        {
            var buf = new byte[pcm.Length * 2];
            for (int i = 0; i < pcm.Length; i++)
            {
                buf[i * 2] = (byte)(pcm[i] & 0xFF);
                buf[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xFF);
            }
            return buf;
        }

        /// <summary>PROTOCOL.md §9.1 的 retryable 列。客户端据此决定安静等待还是提示用户。</summary>
        private static bool IsRetryable(string code)
        {
            switch (code)
            {
                case "device_not_found":
                case "connect_timeout":
                case "disconnected":
                case "write_failed":
                    return true;
                default:
                    return false;
            }
        }

        private void OnConnectionChanged(bool connected)
        {
            if (connected)
            {
                var name = _ble.DeviceName;
                if (!string.IsNullOrEmpty(name)) _deviceName = name;
                ClearError();
                SetState("connected", "已连接");
                // proto 1：连接状态变化发 device，不是 ready
                // （ready 只回答「协议能力」，且只在 hello 校验通过时发一次）
                BroadcastDevice();
                ArmSession();
            }
            else
            {
                // 进行中的采集必须按 disconnected 收尾，否则客户端会拿半截音频去转写
                _session.Abort("disconnected");
                _session.Disarm();
                SetState("disconnected", "设备断开");
            }
        }

        /// <summary>
        /// 连接后先静默一段再 arm。设备刚连上会吐遗留通知（实测 04 03 02 11），
        /// 不静默就会被误判成「用户已按下」。之后主动要一次 Caps 用于 16 kHz 校验。
        /// </summary>
        private void ArmSession()
        {
            _session.Disarm();
            var ignored = Task.Run(async () =>
            {
                await Task.Delay(AtvvSession.ArmDelayMs).ConfigureAwait(false);
                if (!_ble.IsConnected) return;
                _session.Arm();
                var err = await _ble.WriteAsync(Atvv.CmdGetCaps).ConfigureAwait(false);
                if (err != null) Log("GetCaps 写入失败：" + err);
            });
        }

        private void OnClientOpened(WsClient c)
        {
            if (_ws == null) return;

            // §4.1：连接建立后立刻补发当前状态 / 错误 / 设备信息。
            // 客户端**不得假设第一条消息是 ready** —— ready 要等它发来合法的 hello。
            _ws.Send(c, MiniJson.State(_state, _detail));
            DebugLog.Ws("out", MiniJson.State(_state, _detail), 0);
            if (_errorCode != null)
            {
                _ws.Send(c, MiniJson.Error(_errorCode, _errorRetryable, _errorMessage ?? ""));
                DebugLog.Ws("out", MiniJson.Error(_errorCode, _errorRetryable, _errorMessage ?? ""), 0);
            }
            if (HasDeviceInfo())
            {
                _ws.Send(c, DeviceJson());
                DebugLog.Ws("out", DeviceJson(), 0);
            }

            if (!_ble.IsConnected)
            {
                var ignored = Task.Run(() => EnsureConnectedAsync());
            }
        }

        /// <summary>
        /// 客户端文本消息。**判定全部委托给 Protocol.Classify** ——
        /// 那是个不依赖 WebSocket 的纯函数，PROTOCOL.md §14 的夹具跑的就是它。
        /// 这里只负责把判定结果变成动作，夹具因此测的是真正的生产逻辑。
        /// </summary>
        private void OnTextReceived(WsClient c, string text)
        {
            DebugLog.Ws("in", text, 0);
            OnTextReceivedCore(c, text);
        }

        private void OnTextReceivedCore(WsClient c, string text)
        {
            var v = Protocol.Classify(text);

            switch (v.Action)
            {
                case ClientMsgAction.Malformed:
                    Log("收到无法解析的消息，关闭连接：" + Trunc(text, 80));
                    if (_ws != null) _ws.CloseClient(c);
                    return;

                case ClientMsgAction.RejectClose:
                    // §6：明确拒绝，不要「警告后继续」—— 老客户端会把 ADPCM 当 PCM16 解，
                    // 得到白噪声且全程不报错，那是最贵的故障。
                    //
                    // ⚠ 必须是 SendThenClose，不能 Send + CloseClient：普通 Send 只入队，
                    // socket 一关 error 帧就胎死腹中，客户端只会看到 1006 而不知道原因。
                    if (_ws != null)
                        _ws.SendThenClose(c, MiniJson.Error(v.ErrorCode, false, ErrorText(v.ErrorCode)));
                    return;

                case ClientMsgAction.RejectKeep:
                    // 已知但被删除的 op：显式报错，但**保持连接**（§4.2）
                    if (_ws != null)
                        _ws.Send(c, MiniJson.Error(v.ErrorCode, false, ErrorText(v.ErrorCode)));
                    return;

                case ClientMsgAction.Ignore:
                    // 真·未知 op：静默忽略（不变量 8：为将来只增字段留出空间）
                    Log("收到未知 op，已忽略：" + MiniJson.GetString(text, "op"));
                    return;

                default:
                    c.WantAudio = v.WantAudio;
                    c.Handshaked = true;      // 音频闸开：在此之前不得推音频帧
                    if (_ws != null) _ws.Send(c, MiniJson.Ready(ProtoVersion, EmptyCaps, 16000, 1));
                    var ignored = Task.Run(() => EnsureConnectedAsync());
                    return;
            }
        }

        private static string ErrorText(string code)
        {
            switch (code)
            {
                case "proto_mismatch":
                    return "协议版本不匹配：local-mic 需要 " + ProtoVersion;
                case "op_not_supported":
                    return "proto 1 已删除客户端→设备的命令通道";
                default:
                    return code;
            }
        }

        /// <summary>客户端发来二进制帧 ⇒ 违反单向性，关闭连接（§4.2）。</summary>
        private void OnBinaryReceived(WsClient c)
        {
            DebugLog.Ws("in", null, 0);
            Log("客户端发来二进制帧，协议里不存在这种消息，关闭连接");
            if (_ws != null) _ws.CloseClient(c);
        }

        private static readonly string[] EmptyCaps = new string[0];

        private static string Trunc(string s, int n)
        {
            return s == null ? "" : (s.Length <= n ? s : s.Substring(0, n));
        }

        private void Tick(object _)
        {
            if (Interlocked.Exchange(ref _working, 1) == 1) return;
            try
            {
                if (_ble.IsConnected) return;
                _tick++;
                bool hasClient = _ws != null && _ws.ClientCount > 0;
                if (!hasClient && (_tick % 6) != 0) return;   // 无客户端时每 30 秒探一次
                var ignored = Task.Run(() => EnsureConnectedAsync());
            }
            finally
            {
                Interlocked.Exchange(ref _working, 0);
            }
        }

        /// <summary>确保已连上。返回 null 表示已连接，否则返回失败原因。</summary>
        public async Task<string> EnsureConnectedAsync()
        {
            if (_ble.IsConnected) return null;
            if (_address == 0)
            {
                SetError("device_not_found", true, "未找到遥控器，请先在 Windows 里配对");
                return "未找到遥控器，请先在 Windows 里配对";
            }

            // 串行化：定时器、浏览器上线、启动踢一脚、用户点自检 —— 这些都可能同时触发连接。
            // 以前这里直接靠 BleRemote 的 _busy 挡，后来者会拿到「正在连接中」当成失败返回，
            // 于是自检在启动瞬间点就会莫名其妙地失败。现在让后来者排队，而不是报错。
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ble.IsConnected) return null;   // 排队期间别人已经连上了

                SetState("connecting", "连接 " + DeviceMac);
                string err;
                try
                {
                    err = await _ble.ConnectAsync(_address).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    err = e.Message;
                }
                if (err != null)
                {
                    // 并发踢了一脚，不是故障，别报成错误
                    if (err == "正在连接中") return err;

                    // ⚠ 错误码由 BleRemote 在失败点结构化记下，这里【不解析消息文本】——
                    // WinRT 异常消息是本地化的，靠字符串包含判定在中文系统上会失效。
                    var code = _ble.LastErrorCode;
                    if (string.IsNullOrEmpty(code)) code = "connect_timeout";
                    SetError(code, IsRetryable(code), err);
                    return err;
                }
                var name = _ble.DeviceName;
                if (!string.IsNullOrEmpty(name)) _deviceName = name;
                SetState("connected", "已连接");
                return null;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public void SetAddress(ulong address, string name)
        {
            _address = address;
            _deviceName = name;
            Config.Address = address;

            // 换设备必须清掉上一台的型号/电量，否则界面会拿旧数据糊弄用户
            Info = null;
            Battery = -1;
            BatteryAt = DateTime.MinValue;
            _lowNotified = false;

            // §7：换设备是 device 的【必须】发送时机 —— 新设备的电量确实还没读到，
            // 客户端手里不能留着上一台的旧值。
            BroadcastDevice();

            _ble.Disconnect();
            var ignored = Task.Run(() => EnsureConnectedAsync());
        }

        /// <summary>
        /// 设置链路状态。**状态语义**（§8.1.1）：值没变就不广播。
        /// 重连定时器每 5 秒试一次，不抑制的话客户端会收到 connecting/error 风暴 ——
        /// 那与 §9.2「retryable:true ⇒ 安静等待」正面冲突。
        ///
        /// 抑制不会造成「客户端不知道 local-mic 死了」：WS 跑在 TCP 上，进程退出会触发 onclose。
        /// </summary>
        public void SetState(string state, string detail)
        {
            _state = state;
            _detail = detail ?? "";

            var h = StateChanged;      // 本地 UI 始终更新，它不受抑制约束
            if (h != null) h(state, _detail);

            var key = state + "\n" + _detail;
            if (key == _broadcastStateKey) return;
            _broadcastStateKey = key;
            if (_ws != null) _ws.BroadcastText(MiniJson.State(state, _detail));
        }

        /// <summary>
        /// 结构化错误（§9）。与 state:"error" 配对发出，但**抑制判据更严**：
        /// 同一 code 的连续重连失败只发一次（§9.3）。
        /// </summary>
        public void SetError(string code, bool retryable, string message)
        {
            var msg = message ?? "";
            SetState("error", string.IsNullOrEmpty(msg) ? code : msg);

            // 抑制判据：同一 code 的持续失败已经广播过一次，不要再刷。
            // 最容易产生分歧的就是「同一 code 重复重连失败算不算变化」—— 不算（§9.3）。
            if (code == _errorCode) return;

            _errorCode = code;
            _errorMessage = msg;
            _errorRetryable = retryable;
            if (_ws != null) _ws.BroadcastText(MiniJson.Error(code, retryable, msg));
        }

        private void ClearError()
        {
            _errorCode = null;
            _errorMessage = null;
            _errorRetryable = false;
        }

        private bool HasDeviceInfo()
        {
            return Info != null || Battery >= 0 || !string.IsNullOrEmpty(_deviceName) || _address != 0;
        }

        private string DeviceJson()
        {
            var info = Info;
            return MiniJson.Device(DeviceMac, DisplayName,
                                   info != null ? (info.Model ?? "") : "",
                                   _ble.IsPaired, Battery);
        }

        private void BroadcastDevice()
        {
            if (_ws != null) _ws.BroadcastText(DeviceJson());
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine("[local-mic] " + message);
        }

        // ---------- 自检 ----------

        public sealed class SelfTestResult
        {
            public bool Ok;
            public string Message = "";
            public Caps Caps;
            public int Frames;
            public int Bytes;
            public double Seconds;
            public int FrameSize;

            /// <summary>
            /// 原始 ADPCM 帧。留着是为了让界面能把这段录音解码播放 ——
            /// 「听得清」才是判断设备可用的最终标准，光看帧数不够。
            /// </summary>
            public List<byte[]> RawFrames = new List<byte[]>();

            /// <summary>
            /// 本次采集收到的 CONTROL 事件时序（相对按下时刻的毫秒 + 原始字节）。
            /// 留着是为了让「松手没反应」这类问题能当场取证，而不是靠猜 ——
            /// 设备到底发了什么、什么时候发的，看这一行就知道。
            /// </summary>
            public string ControlTrace = "";

            /// <summary>结束是否由静默看门狗兜底（0x00 没按时到）。</summary>
            public bool EndedBySilence;
        }

        /// <summary>
        /// 不依赖浏览器的端到端自检：连接 → GetCaps → 等按键 → MicOpen → 收音频。
        /// 对应托盘里的「测试连接 / 测试音频」。
        /// </summary>
        public async Task<SelfTestResult> RunSelfTestAsync(Action<string> progress, bool waitForKey)
        {
            var r = new SelfTestResult();
            if (_address == 0)
            {
                r.Message = "未找到遥控器，请先在 Windows 里配对";
                return r;
            }

            progress("连接遥控器…");
            var err = await EnsureConnectedAsync().ConfigureAwait(false);
            if (err != null) { r.Message = err; return r; }

            var capsTcs = new TaskCompletionSource<Caps>();
            var startTcs = new TaskCompletionSource<byte>();
            var stopTcs = new TaskCompletionSource<bool>();
            var sw = new Stopwatch();
            int frames = 0, bytes = 0;
            var sizes = new Dictionary<int, int>();
            bool armed = false;
            byte session = 0;
            // 上限 5 分钟（20000 帧 × 120 B ≈ 2.4 MB），防止按住不放把内存撑爆
            var rawFrames = new List<byte[]>();

            // 取证用：CONTROL 事件时序。未知 opcode 也要记 —— 松手若发的是别的码，
            // 光看「没收到 0x00」永远查不出根因。
            var events = new List<string>();
            var clk = Stopwatch.StartNew();          // 事件时间轴与静默看门狗共用的时钟
            long pressAt = -1;
            long lastAudioAt = 0;
            bool gotAudio = false;

            Action<byte[]> onControl = b =>
            {
                if (b.Length == 0) return;

                if (!armed)
                {
                    // 自检未就绪时按下 ⇒ 这一按会被静默丢掉。真机 2026-09-29：用户就是
                    // 在「连接遥控器」那一秒抢跑的，之后整次录音不知所踪，还以为是程序坏了。
                    // 丢没办法避免（那时还没订阅完），但**必须说出来**，否则用户无从判断。
                    if (b[0] == Atvv.OpMicOpenRequested || b[0] == Atvv.OpStreamStarted)
                    {
                        DebugLog.State("selftest-too-early", "自检未就绪时的按下，已丢弃");
                        progress("按早了 —— 请等出现「请按住语音键」提示后再按。");
                    }
                    return;
                }

                long now = clk.ElapsedMilliseconds;
                long rel = pressAt >= 0 ? now - pressAt : now;
                if (events.Count < 40)
                {
                    var hex = new StringBuilder();
                    for (int i = 0; i < b.Length && i < 8; i++) hex.Append(b[i].ToString("x2"));
                    events.Add(rel + "ms " + hex);
                }

                if (b[0] == Atvv.OpCaps) capsTcs.TrySetResult(Atvv.ParseCaps(b));
                else if (b[0] == Atvv.OpMicOpenRequested || b[0] == Atvv.OpStreamStarted)
                {
                    // 固件会跳过 0x08 直接发 0x04，两者都当作「用户按下了」（AGENTS.md §7 坑 2）
                    session = b.Length > 1 ? b[1] : (byte)0;
                    if (pressAt < 0) pressAt = now;      // 时间轴改以按下为零点
                    DebugLog.State("selftest-press", "0x" + b[0].ToString("x2") + " 视为按下，session=" + session);
                    DebugLog.MarkPress();
                    startTcs.TrySetResult(session);
                }
                else if (b[0] == Atvv.OpStreamStopped)
                {
                    if (pressAt < 0)
                    {
                        // 🔴 按下之前到达的 0x00 是上一段遗留流的结束，不是本次的。
                        // 让它置位 stopTcs ⇒ 用户按下后 WhenAny(stopTcs) 立即返回 ⇒
                        // 本次录制在按下后几十毫秒就被收尾。真机 2026-09-29 就是这样：
                        // 按下后 25 ms 收尾，录进去的全是遗留流的音频，用户说的话只进 2 帧。
                        DebugLog.State("selftest-stop-ignored", "按下前到达的 0x00（遗留流），丢弃");
                        return;
                    }
                    DebugLog.State("selftest-stop", "收到 0x00，距按下 " + (now - pressAt) + " ms");
                    stopTcs.TrySetResult(true);
                }
                else
                {
                    // 自检路径同样不能静默吞未知 opcode：松手若用的是别的码，这里就是线索
                    DebugLog.State("selftest-control-unknown", "0x" + b[0].ToString("x2") + " 未识别，已忽略");
                }
            };
            Action<byte[]> onAudio = b =>
            {
                if (!armed) return;
                if (pressAt < 0)
                {
                    // 🔴 按下之前的音频一律不算本次录音。
                    // 设备连接后会自己跑一段遗留流（真机实测约 2 秒、133 帧），
                    // 不门控的话它会整段混进本次录制，UI 显示"已录制 2 秒"却不是用户说的话。
                    return;
                }
                lastAudioAt = clk.ElapsedMilliseconds;      // 喂狗：静默兜底靠它判停流
                gotAudio = true;
                if (!sw.IsRunning) sw.Start();
                frames++;
                bytes += b.Length;
                int c;
                sizes.TryGetValue(b.Length, out c);
                sizes[b.Length] = c + 1;
                if (rawFrames.Count < 20000) rawFrames.Add(b);
            };

            // 自检独占：这期间会话机不解释 CONTROL/AUDIO，全部留给自检自己处理。
            // 否则两套状态机同时跑，自检会误收到生产路径已经消费掉的事件。
            _exclusive = true;
            ControlFrame += onControl;
            RawAudioFrame += onAudio;

            try
            {
                // 关键：连接初期设备会吐出遗留通知（Python 版实测刚连上就收到 04 03 02 11）。
                // 不先静默一段就会在用户还没按键时误判成「已按下」。
                await Task.Delay(800).ConfigureAwait(false);
                armed = true;

                progress("发送 GetCaps…");
                var werr = await _ble.WriteAsync(Atvv.CmdGetCaps).ConfigureAwait(false);
                if (werr != null) { r.Message = "GetCaps 写入失败：" + werr; return r; }

                var capsTask = await Task.WhenAny(capsTcs.Task, Task.Delay(3000)).ConfigureAwait(false);
                if (capsTask == capsTcs.Task) { r.Caps = capsTcs.Task.Result; r.FrameSize = r.Caps.FrameSize; }
                else { r.Message = "等 Caps 响应超时（3 秒）"; return r; }
                if (!r.Caps.Supports16k)
                {
                    r.Message = "Caps 显示 16 kHz 不可用：" + r.Caps.Summary;
                    return r;
                }

                if (!waitForKey)
                {
                    r.Ok = true;
                    r.Message = "连接与协议握手正常\n" + r.Caps.Summary;
                    return r;
                }

                progress("请按住遥控器的语音键说话…");
                var startTask = await Task.WhenAny(startTcs.Task, Task.Delay(30000)).ConfigureAwait(false);
                if (startTask != startTcs.Task) { r.Message = "等待按键超时（30 秒）"; return r; }

                progress("收到按键，开麦…");
                await _ble.WriteAsync(new byte[] { 0x0C, 0x00 }).ConfigureAwait(false);   // MicOpen

                // 5.7 秒窗口：每 2.5 秒续一次期（AGENTS.md §7 坑 4）
                var extendCts = new CancellationTokenSource();
                var extend = Task.Run(async () =>
                {
                    while (!extendCts.IsCancellationRequested)
                    {
                        try { await Task.Delay(2500, extendCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        try { await _ble.WriteAsync(new byte[] { 0x0E, session }).ConfigureAwait(false); } catch { }
                    }
                });

                // 不能只等 0x00：真机实测它会迟到到下一次按键才随连接激活一起投递
                // （BLE 空闲时连接间隔变大，通知要等下一个连接事件）。
                // 于是并一条静默看门狗：音频停够 SilenceTimeoutMs 就判定停流。
                var silenceCts = new CancellationTokenSource();
                var watchdog = Task.Run(async () =>
                {
                    while (!silenceCts.IsCancellationRequested)
                    {
                        try { await Task.Delay(50, silenceCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        if (gotAudio && clk.ElapsedMilliseconds - lastAudioAt > AtvvSession.SilenceTimeoutMs)
                        {
                            r.EndedBySilence = true;
                            DebugLog.State("selftest-silence", "音频停够 " + AtvvSession.SilenceTimeoutMs
                                + " ms ⇒ 看门狗判定停流（0x00 未按时到达）");
                            stopTcs.TrySetResult(true);
                            return;
                        }
                    }
                });

                // 收到 StreamStopped（或看门狗判定停流）就停，最多再等 15 秒
                await Task.WhenAny(stopTcs.Task, Task.Delay(15000)).ConfigureAwait(false);
                silenceCts.Cancel();
                extendCts.Cancel();
                sw.Stop();

                r.Frames = frames;
                r.Bytes = bytes;
                r.RawFrames = rawFrames;
                r.Seconds = frames * 15 / 1000.0;   // 每帧 15 ms（AGENTS.md §4.5）
                if (frames == 0)
                {
                    r.Message = "开麦后没有收到音频帧。确认按住的是语音键，且遥控器电量充足。";
                    return r;
                }

                var sizeDesc = new StringBuilder();
                foreach (var kv in sizes) sizeDesc.Append(kv.Key).Append("B×").Append(kv.Value).Append(' ');
                double rate = r.Seconds > 0 ? bytes / r.Seconds : 0;

                r.Ok = true;
                r.Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "收到 {0} 帧 / {1} 字节\n音频时长 {2:F2} 秒\n吞吐 {3:F0} B/s（理论值 8000）\n帧大小分布：{4}\n{5}",
                    frames, bytes, r.Seconds, rate, sizeDesc.ToString().Trim(), r.Caps.Summary);
                return r;
            }
            finally
            {
                ControlFrame -= onControl;
                RawAudioFrame -= onAudio;
                _exclusive = false;
                DebugLog.MarkEnd();

                // 事件时序附到结果里：松手没反应这类问题，看这一行就能定因，不用猜。
                var trace = new StringBuilder();
                trace.Append("\nCONTROL 时序（相对按下）：");
                trace.Append(events.Count == 0 ? "(本次一个都没收到)" : string.Join(" → ", events));
                if (events.Count >= 40) trace.Append(" …");
                if (r.EndedBySilence) trace.Append("\n※ 本次由音频静默判定结束（0x00 未按时到达）");
                r.ControlTrace = trace.ToString();
                r.Message = r.Message + r.ControlTrace;
                Log("自检 CONTROL 时序：" + r.ControlTrace.Replace("\n", " "));
            }
        }

        public void Dispose()
        {
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            if (_ws != null) { _ws.Dispose(); _ws = null; }
            _ble.Dispose();
        }
    }

}
