using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DshRemoteMic
{
    /// <summary>
    /// 调试落盘：把一次测试里**所有**数据流原样存到一个目录里。
    ///
    /// 为什么需要它：真机问题（松手不结束）靠猜是查不出来的 —— 设备到底发了什么、
    /// 什么时候发的、local-mic 自己怎么判的，必须能事后逐条回放。日志只挑"异常"记没用，
    /// 因为**没发生的那件事**才是线索（比如松手后根本没有 0x00），那恰恰记不到。
    /// 所以这里是全量记，不做过滤。
    ///
    /// 落盘内容：
    ///   events.ndjson  所有事件一行一条（BLE 通知、写命令、状态转移、WS 收发）
    ///   control.bin    CONTROL 原始帧（2 字节 LE 长度 + payload）
    ///   audio.bin      AUDIO 原始 ADPCM 帧（同格式，与 diagnostics/decode-bin.mjs 兼容）
    ///   report.txt     收尾汇总（计数 + 各阶段耗时，先看这个）
    /// </summary>
    internal static class DebugLog
    {
        private static readonly object Sync = new object();
        private static readonly Stopwatch Clk = Stopwatch.StartNew();

        private static string _dir;
        private static StreamWriter _ev;
        private static BinaryWriter _ctl;
        private static BinaryWriter _aud;

        private static int _nControl, _nAudio, _nAudioBytes, _nWrite;
        private static long _firstAudioMs = -1, _lastAudioMs = -1;
        private static long _pressMs = -1, _endMs = -1;

        public static bool On { get { lock (Sync) { return _ev != null; } } }
        public static string Dir { get { lock (Sync) { return _dir; } } }

        /// <summary>开一个日志目录。返回目录全路径，失败返回 null。</summary>
        public static string Enable()
        {
            lock (Sync)
            {
                if (_ev != null) return _dir;

                string baseDir;
                try { baseDir = AppDomain.CurrentDomain.BaseDirectory; }
                catch { baseDir = Directory.GetCurrentDirectory(); }

                string dir;
                try
                {
                    dir = Path.Combine(baseDir, "debug", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(dir);
                }
                catch { return null; }

                try
                {
                    _dir = dir;
                    _ev = new StreamWriter(new FileStream(Path.Combine(dir, "events.ndjson"),
                        FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    _ev.AutoFlush = true;                       // 崩溃时也不能丢已发生的事件
                    _ctl = new BinaryWriter(new FileStream(Path.Combine(dir, "control.bin"),
                        FileMode.Create, FileAccess.Write, FileShare.Read));
                    _aud = new BinaryWriter(new FileStream(Path.Combine(dir, "audio.bin"),
                        FileMode.Create, FileAccess.Write, FileShare.Read));

                    // 版本行：用来确认跑的确实是刚构建的那个 exe，而不是旧的
                    string exe = "";
                    try
                    {
                        var p = Process.GetCurrentProcess().MainModule.FileName;
                        exe = new FileInfo(p).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    catch { }
                    Event("open", "exe 构建时间=" + exe + " 目录=" + dir);
                    return dir;
                }
                catch
                {
                    _dir = null; _ev = null;
                    return null;
                }
            }
        }

        /// <summary>记一条事件。kind 用小写短词，note 写人能读的一句话。</summary>
        public static void Event(string kind, string note)
        {
            Event(kind, null, note);
        }

        public static void Event(string kind, byte[] payload, string note)
        {
            lock (Sync)
            {
                if (_ev == null) return;
                try
                {
                    long ms = Clk.ElapsedMilliseconds;
                    var sb = new StringBuilder(128);
                    sb.Append("{\"ms\":").Append(ms.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append('"');
                    sb.Append(",\"kind\":\"").Append(Esc(kind)).Append('"');
                    if (payload != null && payload.Length > 0)
                        sb.Append(",\"hex\":\"").Append(Hex(payload, 16)).Append('"')
                          .Append(",\"len\":").Append(payload.Length);
                    if (!string.IsNullOrEmpty(note))
                        sb.Append(",\"note\":\"").Append(Esc(note)).Append('"');
                    sb.Append('}');
                    _ev.WriteLine(sb.ToString());
                }
                catch { }
            }
        }

        /// <summary>CONTROL 通知。落 bin 并记事件，连 Arm 前被丢弃的也要记。</summary>
        public static void Control(byte[] b)
        {
            if (b == null || b.Length == 0) return;
            lock (Sync)
            {
                if (_ev == null) return;
                _nControl++;
                try
                {
                    _ctl.Write((byte)(b.Length & 0xFF));
                    _ctl.Write((byte)((b.Length >> 8) & 0xFF));
                    _ctl.Write(b);
                    _ctl.Flush();
                }
                catch { }
            }
            Event("control", b, OpName(b[0]));
        }

        /// <summary>AUDIO 通知。落 bin 并记事件；帧长分布异常会在这里显形。</summary>
        public static void Audio(byte[] b)
        {
            if (b == null || b.Length == 0) return;
            long ms;
            lock (Sync)
            {
                if (_ev == null) return;
                ms = Clk.ElapsedMilliseconds;
                _nAudio++;
                _nAudioBytes += b.Length;
                if (_firstAudioMs < 0) _firstAudioMs = ms;
                _lastAudioMs = ms;
                try
                {
                    _aud.Write((byte)(b.Length & 0xFF));
                    _aud.Write((byte)((b.Length >> 8) & 0xFF));
                    _aud.Write(b);
                    _aud.Flush();
                }
                catch { }
            }
            Event("audio", null, b.Length + "B  距上一帧 " + Gap(ms) + "ms");
        }

        /// <summary>主机写设备的命令。err 为 null 表示成功 —— 失败要能一眼看见。</summary>
        public static void Write(byte[] cmd, string err)
        {
            lock (Sync) { if (_ev == null) return; _nWrite++; }
            Event("write", cmd, err == null ? OpName(cmd[0]) + " 成功" : OpName(cmd[0]) + " 失败：" + err);
        }

        /// <summary>会话机状态转移。reason 之类关键字段必须落，事后才能对账。</summary>
        public static void State(string what, string note)
        {
            Event("state", null, what + (string.IsNullOrEmpty(note) ? "" : " —— " + note));
        }

        /// <summary>WS 收发。dir: in = 客户端发来，out = local-mic 发出。</summary>
        public static void Ws(string dir, string text, int bytes)
        {
            Event("ws", null, dir + " " + (text != null ? Trunc(text, 200) : bytes + "B 二进制"));
        }

        public static void MarkPress()
        {
            lock (Sync) { if (_pressMs < 0) _pressMs = Clk.ElapsedMilliseconds; }
        }

        public static void MarkEnd()
        {
            lock (Sync) { if (_endMs < 0) _endMs = Clk.ElapsedMilliseconds; }
        }

        /// <summary>收尾：写 report.txt 并关闭文件句柄。</summary>
        public static string Close()
        {
            lock (Sync)
            {
                if (_ev == null) return null;
                string dir = _dir;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("目录        " + dir);
                    sb.AppendLine("CONTROL 帧  " + _nControl);
                    sb.AppendLine("音频帧      " + _nAudio + " 帧 / " + _nAudioBytes + " 字节");
                    sb.AppendLine("写命令      " + _nWrite);
                    if (_nAudio > 0)
                    {
                        double sec = _nAudio * 15 / 1000.0;
                        sb.AppendLine("音频时长    " + sec.ToString("F2", CultureInfo.InvariantCulture) + " 秒（按 15ms/帧）");
                        sb.AppendLine("首帧        " + _firstAudioMs + " ms");
                        sb.AppendLine("末帧        " + _lastAudioMs + " ms");
                    }
                    if (_pressMs >= 0) sb.AppendLine("按下        " + _pressMs + " ms");
                    if (_endMs >= 0) sb.AppendLine("结束        " + _endMs + " ms"
                        + (_pressMs >= 0 ? "（按下后 " + (_endMs - _pressMs) + " ms）" : ""));
                    else if (_pressMs >= 0) sb.AppendLine("结束        ** 从未结束 **  ← 问题在这");

                    sb.AppendLine();
                    sb.AppendLine("events.ndjson  全量事件，一行一条，按 ms 排序");
                    sb.AppendLine("control.bin    CONTROL 原始帧（2B LE 长度 + payload）");
                    sb.AppendLine("audio.bin      AUDIO 原始 ADPCM 帧（同格式，可用 decode-bin.mjs 解）");
                    File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString(), new UTF8Encoding(false));
                }
                catch { }

                try { _ev.Flush(); _ev.Dispose(); } catch { }
                try { _ctl.Flush(); _ctl.Dispose(); } catch { }
                try { _aud.Flush(); _aud.Dispose(); } catch { }
                _ev = null; _ctl = null; _aud = null;
                return dir;
            }
        }

        // ---------- 内部 ----------

        private static string Gap(long ms)
        {
            long last = _lastAudioMs;
            return last < 0 ? "-" : (ms - last).ToString(CultureInfo.InvariantCulture);
        }

        private static string OpName(byte op)
        {
            switch (op)
            {
                case Atvv.OpStreamStopped: return "0x00 StreamStopped";
                case Atvv.OpStreamStarted: return "0x04 StreamStarted";
                case Atvv.OpMicOpenRequested: return "0x08 MicOpenRequested";
                case Atvv.OpDecoderSync: return "0x0A DecoderSync";
                case Atvv.OpCaps: return "0x0B Caps";
                case 0x0C: return "0x0C MicOpen";
                case 0x0D: return "0x0D MicClose";
                case 0x0E: return "0x0E MicExtend";
                default: return "未知 opcode 0x" + op.ToString("x2");
            }
        }

        private static string Hex(byte[] b, int max)
        {
            var sb = new StringBuilder();
            int n = Math.Min(b.Length, max);
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
            if (b.Length > max) sb.Append("…(").Append(b.Length).Append('B').Append(')');
            return sb.ToString();
        }

        private static string Trunc(string s, int n)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
