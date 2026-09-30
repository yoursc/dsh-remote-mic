using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshRemoteMic
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private const int AttachParentProcess = -1;

        [STAThread]
        private static void Main(string[] args)
        {
            // --debug 可与其他入口组合，也可以单独用来开 GUI：
            // 之后所有 BLE 通知、写命令、会话机状态转移、WS 收发都会落盘到 debug\<时间戳>\
            bool debug = false;
            var rest = new System.Collections.Generic.List<string>();
            foreach (var a in args)
            {
                if (a == "--debug") debug = true;
                else rest.Add(a);
            }
            args = rest.ToArray();

            if (debug)
            {
                string dir = DebugLog.Enable();
                Console.WriteLine(dir == null ? "调试日志开启失败" : "调试日志目录：" + dir);
            }

            // 开发用自检入口：
            //   1. ADPCM 解码器与 Node 侧共享黄金向量逐字节一致
            //   2. ATVV 会话状态机（无硬件回归 —— 解码搬进 local-mic 后生产路径必须有自己的覆盖）
            // WinExe 没有自己的控制台，所以结果同时写文件，方便脚本读取。
            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest(args.Length > 1 ? args[1] : "adpcm-selftest.txt");
                return;
            }

            // 开发用探测入口：真机连一次，把型号/电量读出来。
            // GUI 里的东西看不见，没有这个入口就只能靠猜。
            if (args.Length > 0 && args[0] == "--probe")
            {
                RunProbe(args.Length > 1 ? args[1] : "ble-probe.txt");
                return;
            }

            // 开发用录音入口：等按键 → 录制 → C# 侧解码 → 输出 WAV。
            // 同时把原始帧存成 .bin（与 Python 探针同格式），以便和 Node 侧解码结果逐字节比对。
            if (args.Length > 0 && args[0] == "--record")
            {
                RunRecord(args.Length > 1 ? args[1] : "capture");
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "local-mic_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "DSH 遥控麦克风 local-mic 已经在运行了 —— 请看系统托盘。",
                        "local-mic", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show("未处理异常：" + e.Exception.Message, "local-mic",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);

                var localMic = new LocalMic();
                localMic.Start();
                Application.Run(new TrayApp(localMic));
                DebugLog.Close();               // 收尾写 report.txt
            }
        }

        private static void RunSelfTest(string outPath)
        {
            string report;
            int code;
            try
            {
                var adpcm = AdpcmDecoder.RunSelfTest();
                var session = AtvvSession.RunSelfTest();
                var vectors = Protocol.RunSelfTest();
                report = adpcm + Environment.NewLine + session + Environment.NewLine + vectors;

                int bad = 0;
                if (adpcm.IndexOf("自检通过", StringComparison.Ordinal) < 0) bad++;
                if (session.IndexOf("自检通过", StringComparison.Ordinal) < 0) bad++;
                if (vectors.IndexOf("夹具通过", StringComparison.Ordinal) < 0) bad++;
                code = bad == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                report = "自检异常：" + e;
                code = 2;
            }

            try { File.WriteAllText(outPath, report, new UTF8Encoding(false)); } catch { }
            DebugLog.Close();

            try
            {
                if (AttachConsole(AttachParentProcess))
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            catch { }
            Console.WriteLine(report);

            Environment.Exit(code);
        }

        /// <summary>
        /// 真机探测：连一次遥控器，把 DIS 读到的身份信息与电量打到文件。
        /// 只在开发期用，用来确认 WinRT 侧确实读到了 Standard GATT 数据。
        /// </summary>
        private static void RunProbe(string outPath)
        {
            var sb = new StringBuilder();
            DeviceInfo info = null;
            int battery = -1;
            string state = "";

            var localMic = new LocalMic();
            localMic.DeviceIdentified += i => info = i;
            localMic.BatteryUpdated += b => battery = b;
            localMic.StateChanged += (s, d) => state = s + " / " + d;

            try
            {
                localMic.Start();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 45)
                {
                    if (info != null && battery >= 0) break;
                    Thread.Sleep(200);
                }

                sb.AppendLine("state      " + state);
                sb.AppendLine("address    " + localMic.DeviceMac);
                sb.AppendLine("display    " + localMic.DisplayName);
                sb.AppendLine("connected  " + localMic.IsConnected);
                sb.AppendLine("paired     " + localMic.IsPaired);
                sb.AppendLine();
                if (info == null)
                {
                    sb.AppendLine("DeviceInfo 未读到（DIS 服务缺失或读取被拒）");
                }
                else
                {
                    sb.AppendLine("model       " + info.Model);
                    sb.AppendLine("manufacturer" + " " + info.Manufacturer);
                    sb.AppendLine("firmware    " + info.Firmware);
                    sb.AppendLine("hardware    " + info.Hardware);
                    sb.AppendLine("serial      " + info.Serial);
                    sb.AppendLine("isKnown     " + info.IsKnown);
                    sb.AppendLine("displayName " + info.DisplayName);
                    sb.AppendLine("note        " + info.Note);
                }
                sb.AppendLine("battery     " + (battery >= 0 ? battery + "%" : "未读到"));
            }
            catch (Exception e)
            {
                sb.AppendLine("异常：" + e);
            }
            finally
            {
                try { localMic.Dispose(); } catch { }
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
            Console.WriteLine(sb.ToString());
        }

        /// <summary>
        /// 真机录音：等用户按住语音键 → 收帧 → 用 C# 侧解码器组装 WAV。
        /// 原始帧另存 .bin（2 字节 LE 长度 + payload，与 Python 探针同格式），
        /// 这样可以用 Node 侧解码器解同一份数据做逐字节对比。
        /// </summary>
        private static void RunRecord(string outBase)
        {
            var sb = new StringBuilder();
            var localMic = new LocalMic();
            try
            {
                localMic.Start();
                var r = localMic.RunSelfTestAsync(m => { }, true).GetAwaiter().GetResult();

                sb.AppendLine("device     " + localMic.DisplayName);
                sb.AppendLine("battery    " + (localMic.Battery >= 0 ? localMic.Battery + "%" : "—"));
                sb.AppendLine("ok         " + r.Ok);
                sb.AppendLine("message    " + r.Message.Replace("\r", " ").Replace("\n", " | "));

                if (r.RawFrames != null && r.RawFrames.Count > 0)
                {
                    using (var fs = File.Create(outBase + ".bin"))
                    {
                        foreach (var f in r.RawFrames)
                        {
                            fs.WriteByte((byte)(f.Length & 0xFF));
                            fs.WriteByte((byte)((f.Length >> 8) & 0xFF));
                            fs.Write(f, 0, f.Length);
                        }
                    }

                    WavStats st;
                    double gain;
                    var wav = Wav.BuildAuto(r.RawFrames, 16000, out st, out gain);
                    File.WriteAllBytes(outBase + ".wav", wav);

                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "frames     {0}  seconds {1:F2}  bytes {2}  rate {3:F0} B/s",
                        r.Frames, r.Seconds, r.Bytes, r.Seconds > 0 ? r.Bytes / r.Seconds : 0));
                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "gain       x{0:F2}   rms {1:F1} dBFS   peak {2:F1} dBFS   clipped {3}   samples {4}",
                        gain, st.RmsDbfs, st.PeakDbfs, st.Clipped, st.Samples));
                    sb.AppendLine("wrote      " + outBase + ".bin / " + outBase + ".wav");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("exception  " + e);
            }
            finally
            {
                try { localMic.Dispose(); } catch { }
            }

            try { File.WriteAllText(outBase + ".txt", sb.ToString(), new UTF8Encoding(false)); } catch { }
            Console.WriteLine(sb.ToString());
        }
    }
}
