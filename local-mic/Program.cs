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
                // 诊断页默认优先读仓库里 diagnostics/ 那份（见 DiagWeb）；
                // exe 与页面不在同一棵目录树时用这个显式指定。
                else if (a.StartsWith("--diag-dir=", StringComparison.Ordinal))
                    DiagWeb.SetSourceDir(a.Substring("--diag-dir=".Length));
                else rest.Add(a);
            }
            args = rest.ToArray();

            if (debug)
            {
                string dir = DebugLog.Enable();
                Console.WriteLine(dir == null ? "调试日志开启失败" : "调试日志目录：" + dir);
                DebugLog.StartRadioWatch();   // 关/开蓝牙的事件落 kind="radio"
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
                        "DSH 遥控麦克风 local-mic 已经在运行了。\n\n" +
                        "图标在系统托盘（Windows 11 常折叠进「隐藏的图标」区，点任务栏的 ^ 展开）。",
                        "local-mic", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ReportFatal(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportFatal(e.ExceptionObject as Exception);

                try
                {
                    var localMic = new LocalMic();
                    localMic.Start();

                    var tray = new TrayApp(localMic);

                    if (Integrity.TrayIconUnavailable)
                    {
                        // 低完整性下 Windows 拒绝发放托盘图标（UIPI），而主窗口平时只能从托盘打开
                        // ⇒ 不兜底就成了"双击了，什么也没有"的隐形进程：活着、WS 在听，但看不见也关不掉。
                        // 常见成因不是启动方式，而是 exe 继承了目录的 Low 强制标签（见 Integrity 注释）。
                        tray.RunWithoutTray(
                            "（完整性 " + Integrity.CurrentName + "：系统不发放托盘图标，关窗即退出）");
                    }
                    Application.Run(tray);
                }
                catch (Exception e)
                {
                    // 启动阶段（构造 LocalMic / Start）的异常不会被 ThreadException 接住 ——
                    // 它在消息泵启动之前抛出；WinExe 又没有控制台。不显式报出来，用户看到的
                    // 现象只是"双击了，托盘里什么都没有"（真机上正是这么丢过一次：读配置
                    // 要求了写权限，被沙箱拒绝）。
                    ReportFatal(e);
                }
                finally
                {
                    DebugLog.Close();           // 收尾写 report.txt
                }
            }
        }

        /// <summary>
        /// 把致命异常落到 exe 旁边的 start-error.log，并弹一个能读懂的框。
        /// 两条路各自包着 try：报告本身绝不能再抛。
        /// </summary>
        private static void ReportFatal(Exception e)
        {
            if (e == null) return;

            string log = null;
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                log = Path.Combine(dir, "start-error.log");
                File.AppendAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e + Environment.NewLine);
            }
            catch { }

            try
            {
                MessageBox.Show(
                    "local-mic 启动失败：\n\n" + e.Message +
                    (log != null ? "\n\n详细堆栈已写入：\n" + log : ""),
                    "local-mic", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
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
                var diag = DiagWeb.SelfTest();          // 内嵌页面资源齐全（漏编译 ⇒ 页面 404，肉眼难查）
                report = adpcm + Environment.NewLine + session + Environment.NewLine + vectors +
                         Environment.NewLine + diag;

                int bad = 0;
                if (adpcm.IndexOf("自检通过", StringComparison.Ordinal) < 0) bad++;
                if (session.IndexOf("自检通过", StringComparison.Ordinal) < 0) bad++;
                if (vectors.IndexOf("夹具通过", StringComparison.Ordinal) < 0) bad++;
                if (diag.IndexOf("自检通过", StringComparison.Ordinal) < 0) bad++;
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
