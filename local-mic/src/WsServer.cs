using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshRemoteMic
{
    /// <summary>
    /// 极简 WebSocket 服务端（RFC 6455 子集：握手 + 文本帧 + 二进制帧 + ping/pong + close）。
    ///
    /// 为什么不用 HttpListener.AcceptWebSocketAsync：HttpListener 走 http.sys，
    /// 绑定前缀需要先 netsh http add urlacl 注册，那要管理员权限 —— 与「免安装、
    /// 免提权的常驻托盘程序」这个目标直接冲突。这里用 TcpListener 自己握手和解帧，
    /// 约 200 行，完全可控，也不需要任何第三方包。
    /// </summary>
    internal sealed class WsServer : IDisposable
    {
        private const string GuidMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly int _port;
        private TcpListener _listener;
        private readonly List<WsClient> _clients = new List<WsClient>();
        private readonly object _sync = new object();
        private CancellationTokenSource _cts;
        private bool _running;

        public event Action<WsClient> ClientOpened;
        /// <summary>音频二进制帧的 kind（PROTOCOL.md §10）。proto 1 只定义这一个。</summary>
        public const byte AudioFrameKind = 0x02;

        public event Action<WsClient, string> TextReceived;
        /// <summary>客户端发来二进制帧。proto 1 里不存在这种消息 ⇒ 上层应关闭连接（§4.2）。</summary>
        public event Action<WsClient> BinaryReceived;
        public event Action<WsClient> ClientClosed;

        public int Port { get { return _port; } }
        public int ClientCount { get { lock (_sync) { return _clients.Count; } } }

        public WsServer(int port)
        {
            _port = port;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                var client = new WsClient(tcp);
                // 每个连接独立跑，握手失败就丢掉，不影响其他连接
                var ignored = Task.Run(() => ServeAsync(client, ct));
            }
        }

        private async Task ServeAsync(WsClient client, CancellationToken ct)
        {
            try
            {
                if (!await HandshakeAsync(client.Stream).ConfigureAwait(false))
                {
                    client.Close();
                    return;
                }
            }
            catch
            {
                client.Close();
                return;
            }

            lock (_sync) { _clients.Add(client); }
            var h = ClientOpened;
            if (h != null) h(client);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var inbound = await ReadFrameAsync(client.Stream).ConfigureAwait(false);
                    if (inbound == null) break;

                    switch (inbound.Opcode)
                    {
                        case 0x1: // text
                            var th = TextReceived;
                            if (th != null) th(client, Encoding.UTF8.GetString(inbound.Payload));
                            break;
                        case 0x8: // close
                            SendRaw(client, Frame(0x8, new byte[0]));
                            return;
                        case 0x9: // ping -> pong，浏览器用它探活
                            SendRaw(client, Frame(0xA, inbound.Payload));
                            break;
                        case 0x2: // 客户端发二进制：协议里没有这种消息，交给上层处置
                            var bh = BinaryReceived;
                            if (bh != null) bh(client);
                            break;
                        case 0xA: // pong
                            break;
                    }
                }
            }
            catch
            {
                // 连接断开属正常生命周期，静默处理
            }
            finally
            {
                lock (_sync) { _clients.Remove(client); }
                client.Close();
                var ch = ClientClosed;
                if (ch != null) ch(client);
            }
        }

        // ---------- 广播 ----------

        /// <summary>文本消息：所有连接都发（含尚未握手的 —— state/device/error 要能在握手前送达）。</summary>
        public void BroadcastText(string text)
        {
            var frame = Frame(0x1, Encoding.UTF8.GetBytes(text));
            foreach (var c in Snapshot()) c.Enqueue(frame, false);
        }

        /// <summary>
        /// 音频采样帧：**只发给已完成 proto 握手且订阅了音频的连接**（PROTOCOL.md §4.1 / §13）。
        /// 建帧动作在订阅者过滤之后 —— 没人订阅时不该每 15 ms 白建一帧。
        /// </summary>
        /// <summary>
        /// 构造音频帧的 WS 载荷：**[kind 0x02][PCM16 s16le]**（PROTOCOL.md §10）。
        ///
        /// 🔴 kind 字节必须在这一层加，不能让调用方自己拼。
        /// 漏掉它 ⇒ 客户端把音频数据的第一个字节当成 kind，整段判成"未知 kind"而丢弃。
        /// 真机 2026-09-30 就是这个：页面刷出 `未知 kind 0x75 / 0x92`，采集段却是 0 样本 ——
        /// 那两个字节其实是 PCM 的第一个采样值，不是什么未知协议。
        ///
        /// 抽成纯静态函数是为了让 §14 夹具能直接测到发送侧格式（BUILD audio-frame），
        /// 否则这条只能靠真机发现 —— 而它恰好发生在真机的最后一环。
        /// </summary>
        internal static byte[] BuildAudioPayload(byte[] pcm)
        {
            var payload = new byte[pcm.Length + 1];
            payload[0] = AudioFrameKind;
            Buffer.BlockCopy(pcm, 0, payload, 1, pcm.Length);
            return payload;
        }

        public void BroadcastAudio(byte[] pcm)
        {
            var frame = Frame(0x2, BuildAudioPayload(pcm));
            foreach (var c in Snapshot())
            {
                if (c.Handshaked && c.WantAudio) c.Enqueue(frame, true);
            }
        }

        public void Send(WsClient client, string text)
        {
            client.Enqueue(Frame(0x1, Encoding.UTF8.GetBytes(text)), false);
        }

        /// <summary>主动断开某个客户端（如 proto 不匹配）。关闭 socket 后读循环会自行清理。</summary>
        public void CloseClient(WsClient client)
        {
            client.Close();
        }

        private WsClient[] Snapshot()
        {
            lock (_sync)
            {
                if (_clients.Count == 0) return EmptyClients;
                return _clients.ToArray();
            }
        }

        private static readonly WsClient[] EmptyClients = new WsClient[0];

        /// <summary>
        /// 直接写，不经队列 —— 只给 ping/pong、close 这类极小的控制帧用。
        /// 它们是对端请求的即时应答，排队反而会让探活失真。
        /// </summary>
        private static void SendRaw(WsClient client, byte[] frame)
        {
            try
            {
                lock (client.WriteLock)
                {
                    client.Stream.Write(frame, 0, frame.Length);
                    client.Stream.Flush();
                }
            }
            catch
            {
                // 写失败在下一次读循环里会被清理
            }
        }

        // ---------- 协议细节 ----------

        private static async Task<bool> HandshakeAsync(NetworkStream s)
        {
            var buf = new List<byte>(512);
            var one = new byte[1];
            while (true)
            {
                int n = await s.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (n == 0) return false;
                buf.Add(one[0]);
                int c = buf.Count;
                if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
                    break;
                if (c > 8192) return false;
            }

            var req = Encoding.ASCII.GetString(buf.ToArray());
            var m = Regex.Match(req, @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) return false;

            string accept;
            using (var sha1 = SHA1.Create())
            {
                var raw = Encoding.ASCII.GetBytes(m.Groups[1].Value.Trim() + GuidMagic);
                accept = Convert.ToBase64String(sha1.ComputeHash(raw));
            }

            var resp = "HTTP/1.1 101 Switching Protocols\r\n" +
                       "Upgrade: websocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(resp);
            await s.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
            return true;
        }

        private sealed class WsFrame
        {
            public byte Opcode;
            public byte[] Payload;
        }

        private static async Task<WsFrame> ReadFrameAsync(NetworkStream s)
        {
            var b0 = await ReadExactlyAsync(s, 2).ConfigureAwait(false);
            if (b0 == null) return null;

            byte opcode = (byte)(b0[0] & 0x0F);
            bool masked = (b0[1] & 0x80) != 0;
            long len = b0[1] & 0x7F;

            if (len == 126)
            {
                var ext = await ReadExactlyAsync(s, 2).ConfigureAwait(false);
                if (ext == null) return null;
                len = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                var ext = await ReadExactlyAsync(s, 8).ConfigureAwait(false);
                if (ext == null) return null;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }

            if (len < 0 || len > 8 * 1024 * 1024) return null;

            byte[] maskKey = null;
            if (masked)
            {
                maskKey = await ReadExactlyAsync(s, 4).ConfigureAwait(false);
                if (maskKey == null) return null;
            }

            var data = await ReadExactlyAsync(s, (int)len).ConfigureAwait(false);
            if (data == null) return null;

            if (masked)
            {
                for (int i = 0; i < data.Length; i++) data[i] ^= maskKey[i & 3];
            }
            return new WsFrame { Opcode = opcode, Payload = data };
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream s, int count)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = await s.ReadAsync(buf, got, count - got).ConfigureAwait(false);
                if (n == 0) return null;
                got += n;
            }
            return buf;
        }

        internal static byte[] Frame(byte opcode, byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | opcode));
            int len = data.Length;
            if (len < 126)
            {
                ms.WriteByte((byte)len);
            }
            else if (len <= 65535)
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)(len >> 8));
                ms.WriteByte((byte)(len & 0xFF));
            }
            else
            {
                ms.WriteByte(127);
                for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(((long)len >> (8 * i)) & 0xFF));
            }
            ms.Write(data, 0, len);
            return ms.ToArray();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            lock (_sync)
            {
                foreach (var c in _clients) c.Close();
                _clients.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            if (_cts != null) { _cts.Dispose(); _cts = null; }
        }
    }

    /// <summary>
    /// 一条 WebSocket 连接。
    ///
    /// 每个连接有自己的**有界发送队列 + 排空任务**（PROTOCOL.md §13）。
    /// 为什么必须这样：音频帧 15 ms 一来，而 Stream.Write 是阻塞的。
    /// 若直接在 BLE 通知线程里同步写，浏览器一卡（打个断点就够）就会：
    /// 发送缓冲满 ⇒ Write 阻塞 ⇒ **BLE 派发线程被卡住** ⇒ 音频堆积 ⇒ 丢帧甚至断连。
    /// 而且旧实现是"逐客户端串行广播"，一个慢客户端会挡住后面所有人。
    ///
    /// ⚠ 队列必须是**单一 FIFO**（音频与事件同队）。拆成两队的话，溢出丢音频时
    /// `end` 会越过还在排队的末尾音频帧先到 ⇒ 违反不变量 2 ⇒ 每句话结尾丢半个字。
    /// </summary>
    internal sealed class WsClient
    {
        /// <summary>队列上限（字节）。约 1 秒音频（32,000 B/s）。</summary>
        private const int MaxQueuedBytes = 32768;

        private sealed class OutItem
        {
            public byte[] Frame;
            public bool IsAudio;
        }

        public readonly TcpClient Tcp;
        public readonly NetworkStream Stream;
        public readonly object WriteLock = new object();

        /// <summary>proto 握手是否通过。**未通过前不得推音频帧**（§4.1）。</summary>
        public bool Handshaked;
        /// <summary>是否订阅音频。缺省 true（§4：hello 不带 audio 视为订阅）。</summary>
        public bool WantAudio = true;

        private readonly LinkedList<OutItem> _queue = new LinkedList<OutItem>();
        private readonly object _qsync = new object();
        private int _queuedBytes;
        private bool _draining;

        public WsClient(TcpClient tcp)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
            Tcp.NoDelay = true;   // 音频帧 15ms 一包，别让 Nagle 把它们攒起来
        }

        /// <summary>
        /// 入队。溢出时从队头丢**最旧的音频帧** —— 事件帧永不丢弃，相对顺序也永不破坏。
        /// </summary>
        public void Enqueue(byte[] frame, bool isAudio)
        {
            lock (_qsync)
            {
                _queue.AddLast(new OutItem { Frame = frame, IsAudio = isAudio });
                _queuedBytes += frame.Length;

                while (_queuedBytes > MaxQueuedBytes)
                {
                    var node = _queue.First;
                    while (node != null && !node.Value.IsAudio) node = node.Next;
                    if (node == null) break;      // 队列里已无可丢的音频帧
                    _queuedBytes -= node.Value.Frame.Length;
                    _queue.Remove(node);
                }

                if (_draining) return;
                _draining = true;
            }
            Task.Run(DrainLoop);
        }

        private void DrainLoop()
        {
            while (true)
            {
                OutItem item;
                lock (_qsync)
                {
                    if (_queue.Count == 0) { _draining = false; return; }
                    item = _queue.First.Value;
                    _queue.RemoveFirst();
                    _queuedBytes -= item.Frame.Length;
                }

                try
                {
                    lock (WriteLock)
                    {
                        Stream.Write(item.Frame, 0, item.Frame.Length);
                        Stream.Flush();
                    }
                }
                catch
                {
                    lock (_qsync) { _queue.Clear(); _queuedBytes = 0; _draining = false; }
                    return;   // 连接已断，读循环会把它从列表里摘掉
                }
            }
        }

        public void Close()
        {
            try { Tcp.Close(); } catch { }
        }
    }
}
