using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshRemoteMic
{
    /// <summary>
    /// 主界面。托盘菜单的能力天花板很低 —— 放不下电量条、波形，也没法做「按住期间实时刷新」。
    /// 这个窗口存在的理由就是让设备状态和自检结果一次看全。
    ///
    /// 生命周期：点关闭只隐藏，程序仍在托盘常驻。真正退出走托盘的「退出」。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly LocalMic _localMic;

        // 设备区
        private Label _devName, _devBadge, _devSub, _devMac;
        private BatteryBar _battery;
        private Label _batteryText;
        private Button _btnSelect, _btnPair;

        // 自检区
        private Button _btnTestConn, _btnTestAudio, _btnPlay, _btnSave, _btnDebug, _btnPort;
        private TextBox _txtPort;
        private Label _portNote;
        private Label _testHint;
        private Label _capDuration, _capFrames, _capRate;
        private Label _valDuration, _valFrames, _valRate;
        private WavePanel _wave;

        // 底部
        private CheckBox _chkAuto;
        private Label _statusLine, _wsLine;

        private readonly Timer _uiTick;
        private bool _testing;
        private volatile bool _recording;
        private int _liveFrames;
        private byte[] _wavBytes;

        private SoundPlayer _player;
        private MemoryStream _playerStream;
        private bool _playing;

        private static readonly Color CText = Color.FromArgb(0x2C, 0x2C, 0x2A);
        private static readonly Color CMuted = Color.FromArgb(0x5F, 0x5E, 0x5A);
        private static readonly Color CFaint = Color.FromArgb(0x9A, 0x98, 0x90);
        private static readonly Color CBadgeKnownBack = Color.FromArgb(0xE6, 0xF1, 0xFB);
        private static readonly Color CBadgeKnownText = Color.FromArgb(0x18, 0x5F, 0xA5);
        private static readonly Color CBadgeUnkBack = Color.FromArgb(0xF1, 0xEF, 0xE8);
        private static readonly Color CBadgeUnkText = Color.FromArgb(0x5F, 0x5E, 0x5A);
        private static readonly Color CSep = Color.FromArgb(0xE4, 0xE2, 0xDC);

        private static Font F(float size, FontStyle style)
        {
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        public MainForm(LocalMic localMic)
        {
            _localMic = localMic;
            BuildUi();

            _localMic.DeviceIdentified += OnDeviceIdentified;
            _localMic.BatteryUpdated += OnBatteryUpdated;
            _localMic.StateChanged += OnStateChanged;
            _localMic.ClientListChanged += () => Post(RefreshAll);   // 否则「浏览器在线」永远停在 0
            _localMic.AudioFrame += OnAudioFrame;

            _uiTick = new Timer { Interval = 200 };
            _uiTick.Tick += (s, e) => UpdateLiveStats();
            _uiTick.Start();

            RefreshAll();
        }

        // ------------------------------------------------------------------
        // 界面构建
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            Text = "DSH 遥控麦克风 local-mic";
            FormBorderStyle = FormBorderStyle.FixedSingle;

            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(444, 534);
            Font = SystemFonts.MessageBoxFont;
            BackColor = Color.FromArgb(0xFA, 0xFA, 0xF8);
            Icon = SystemIcons.Application;
            ShowInTaskbar = true;   // 让 Alt+Tab 能找到它，不是那种点完就找不着的窗口

            // ---- 设备区 ----
            _devName = new Label
            {
                Text = "—",
                Location = new Point(16, 12),
                Size = new Size(280, 28),
                Font = F(15F, FontStyle.Bold),
                ForeColor = CText,
                AutoEllipsis = true,
            };
            Controls.Add(_devName);

            _devBadge = new Label
            {
                Text = "",
                Location = new Point(300, 16),
                Size = new Size(128, 22),
                Font = F(8.5F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
            };
            Controls.Add(_devBadge);

            _devSub = new Label
            {
                Location = new Point(16, 44),
                Size = new Size(412, 18),
                ForeColor = CMuted,
                AutoEllipsis = true,
            };
            Controls.Add(_devSub);

            _devMac = new Label
            {
                Location = new Point(16, 63),
                Size = new Size(412, 18),
                ForeColor = CFaint,
                Font = F(8.5F, FontStyle.Regular),
            };
            Controls.Add(_devMac);

            var capBattery = new Label { Text = "电量", Location = new Point(16, 91), Size = new Size(34, 16), ForeColor = CMuted };
            Controls.Add(capBattery);

            _battery = new BatteryBar { Location = new Point(54, 93), Size = new Size(330, 12) };
            Controls.Add(_battery);

            _batteryText = new Label
            {
                Location = new Point(390, 88),
                Size = new Size(38, 18),
                ForeColor = CText,
                TextAlign = ContentAlignment.MiddleRight,
            };
            Controls.Add(_batteryText);

            _btnSelect = new Button { Text = "选择遥控器", Location = new Point(16, 116), Size = new Size(110, 28) };
            _btnSelect.Click += OnSelectDevice;
            Controls.Add(_btnSelect);

            _btnPair = new Button { Text = "在 Windows 中配对…", Location = new Point(134, 116), Size = new Size(146, 28) };
            _btnPair.Click += (s, e) => Shell.OpenWindowsBluetooth();
            Controls.Add(_btnPair);

            AddSeparator(156);

            // ---- 自检区 ----
            var capTest = new Label { Text = "自检", Location = new Point(16, 164), Size = new Size(60, 18), ForeColor = CMuted };
            Controls.Add(capTest);

            _btnTestConn = new Button { Text = "测试连接", Location = new Point(16, 186), Size = new Size(110, 30) };
            _btnTestConn.Click += async (s, e) => await RunTestAsync(false);
            Controls.Add(_btnTestConn);

            _btnTestAudio = new Button { Text = "测试音频", Location = new Point(134, 186), Size = new Size(130, 30) };
            _btnTestAudio.Click += async (s, e) => await RunTestAsync(true);
            Controls.Add(_btnTestAudio);

            _testHint = new Label
            {
                Text = "按住遥控器语音键说话，松手后自动解码，然后可以在这里播放试听。",
                Location = new Point(16, 224),
                Size = new Size(412, 34),
                ForeColor = CMuted,
            };
            Controls.Add(_testHint);

            AddStat(16, "时长", out _capDuration, out _valDuration);
            AddStat(154, "帧数", out _capFrames, out _valFrames);
            AddStat(292, "速率", out _capRate, out _valRate);

            _wave = new WavePanel { Location = new Point(16, 306), Size = new Size(412, 60) };
            Controls.Add(_wave);

            _btnPlay = new Button { Text = "播放", Location = new Point(16, 376), Size = new Size(100, 30), Enabled = false };
            _btnPlay.Click += async (s, e) => await TogglePlayAsync();
            Controls.Add(_btnPlay);

            _btnSave = new Button { Text = "另存为 WAV", Location = new Point(124, 376), Size = new Size(130, 30), Enabled = false };
            _btnSave.Click += OnSaveWav;
            Controls.Add(_btnSave);

            AddSeparator(418);

            // ---- 底部 ----
            _chkAuto = new CheckBox
            {
                Text = "开机自动启动",
                Location = new Point(12, 426),
                Size = new Size(160, 22),
                Checked = Config.AutoStart,
            };
            _chkAuto.CheckedChanged += (s, e) => Config.AutoStart = _chkAuto.Checked;
            Controls.Add(_chkAuto);

            // 调试日志开关：真机问题靠猜查不出来，必须能随手开、随手收尾。
            // 做成按钮而不是启动参数，是因为用户是双击启动的，没有地方传参。
            _btnDebug = new Button { Text = "调试日志：关", Location = new Point(186, 424), Size = new Size(242, 24) };
            _btnDebug.Click += OnToggleDebug;
            Controls.Add(_btnDebug);
            RefreshDebugButton();

            _statusLine = new Label { Location = new Point(16, 456), Size = new Size(412, 18), ForeColor = CMuted };
            Controls.Add(_statusLine);

            _wsLine = new Label { Location = new Point(16, 476), Size = new Size(412, 18), ForeColor = CFaint, Font = F(8.5F, FontStyle.Regular) };
            Controls.Add(_wsLine);

            // 端口可改：8787 被别的程序占了就没法工作，必须留一个换端口的口子（PROTOCOL.md §3）。
            var capPort = new Label { Text = "监听端口", Location = new Point(16, 504), Size = new Size(56, 18), ForeColor = CMuted };
            Controls.Add(capPort);

            _txtPort = new TextBox { Location = new Point(76, 502), Size = new Size(56, 22), Text = _localMic.Port.ToString() };
            Controls.Add(_txtPort);

            _btnPort = new Button { Text = "应用", Location = new Point(138, 500), Size = new Size(56, 24) };
            _btnPort.Click += OnApplyPort;
            Controls.Add(_btnPort);

            _portNote = new Label { Location = new Point(202, 504), Size = new Size(226, 18), ForeColor = CFaint, Font = F(8.5F, FontStyle.Regular) };
            Controls.Add(_portNote);
        }

        private void OnApplyPort(object s, EventArgs e)
        {
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port))
            {
                SetPortNote("端口要是数字", true);
                _txtPort.Text = _localMic.Port.ToString();
                return;
            }

            string err = _localMic.SetPort(port);
            if (err != null)
            {
                // 失败时旧端口还在跑，回显实际端口，别让用户以为已经换了
                SetPortNote(err, true);
                _txtPort.Text = _localMic.Port.ToString();
            }
            else
            {
                SetPortNote("已改为 " + _localMic.Port, false);
            }
            RefreshAll();
        }

        private void SetPortNote(string text, bool bad)
        {
            _portNote.Text = text;
            _portNote.ForeColor = bad ? Color.FromArgb(0xE2, 0x4B, 0x4A) : CFaint;
        }

        private void OnToggleDebug(object s, EventArgs e)
        {
            if (DebugLog.On)
            {
                string dir = DebugLog.Close();
                RefreshDebugButton();
                if (dir != null)
                {
                    // 收尾后 report.txt 已写好，这时打开才有东西看。
                    // 低完整性下打不开 explorer（见 Shell.OpenFolder 注释），退化为只显示路径。
                    bool folderOpened = Shell.OpenFolder(dir);
                    _testHint.Text = "调试日志已存到：" + dir + (folderOpened ? "" : "（自动打开失败，请手动前往）");
                }
                return;
            }

            string opened = DebugLog.Enable();
            RefreshDebugButton();
            if (opened == null)
            {
                Shell.Warn("建不了日志目录，可能是没有写权限。");
                return;
            }
            // 开启时**不弹文件夹** —— 一开就弹窗口会打断测试动作，而且那时目录还是空的。
            // 路径写进提示里；等收尾（report.txt 生成完）再打开。
            _testHint.Text = "调试日志已开（收尾后自动打开目录）：" + opened;
        }

        private void RefreshDebugButton()
        {
            if (_btnDebug == null) return;
            _btnDebug.Text = DebugLog.On ? "调试日志：开（点此收尾）" : "调试日志：关";
        }

        private void AddStat(int x, string caption, out Label cap, out Label val)
        {
            cap = new Label { Text = caption, Location = new Point(x, 262), Size = new Size(130, 16), ForeColor = CFaint, Font = F(8.5F, FontStyle.Regular) };
            val = new Label { Text = "—", Location = new Point(x, 278), Size = new Size(136, 24), ForeColor = CText, Font = F(11F, FontStyle.Bold) };
            Controls.Add(cap);
            Controls.Add(val);
        }

        private void AddSeparator(int y)
        {
            Controls.Add(new Panel
            {
                Location = new Point(16, y),
                Size = new Size(412, 1),
                BackColor = CSep,
            });
        }

        // ------------------------------------------------------------------
        // 状态刷新
        // ------------------------------------------------------------------

        private void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private void OnDeviceIdentified(DeviceInfo info)
        {
            Post(RefreshAll);
        }

        private void OnStateChanged(string state, string detail)
        {
            Post(RefreshAll);
        }

        private void OnBatteryUpdated(int level)
        {
            Post(RefreshAll);
        }

        private void OnAudioFrame(byte[] frame)
        {
            if (_recording) _liveFrames++;
        }

        private void RefreshAll()
        {
            var info = _localMic.Info;

            _devName.Text = _localMic.DisplayName;

            if (info != null && info.IsKnown)
            {
                _devBadge.Text = info.Model;
                _devBadge.BackColor = CBadgeKnownBack;
                _devBadge.ForeColor = CBadgeKnownText;
                _devBadge.Padding = new Padding(0, 0, 6, 0);
            }
            else if (info != null && info.Model.Length > 0)
            {
                _devBadge.Text = info.Model + " · 未验证";
                _devBadge.BackColor = CBadgeUnkBack;
                _devBadge.ForeColor = CBadgeUnkText;
                _devBadge.Padding = new Padding(0, 0, 6, 0);
            }
            else
            {
                _devBadge.Text = "";
                _devBadge.BackColor = Color.Transparent;
            }

            _devSub.Text = info != null && info.Subtitle.Length > 0
                ? info.Subtitle
                : (info != null ? info.Note : "");

            _devMac.Text = _localMic.DeviceMac + (info != null && !info.IsKnown ? "　（" + info.Note + "）" : "");

            // 电量：断开时保留上次读数但灰显，避免把过期数字当成实时值
            int bat = _localMic.Battery;
            bool stale = !_localMic.IsConnected && bat >= 0;
            _battery.SetPercent(bat, stale);
            if (bat < 0)
            {
                _batteryText.Text = "—";
            }
            else if (stale)
            {
                _batteryText.Text = bat + "%";
                _batteryText.ForeColor = CFaint;
            }
            else
            {
                _batteryText.Text = bat + "%";
                _batteryText.ForeColor = bat <= LocalMic.LowBatteryThreshold ? Color.FromArgb(0xE2, 0x4B, 0x4A) : CText;
            }

            _statusLine.Text = "状态：" + NameOf(_localMic.State) + "　·　" + _localMic.Detail +
                               (_localMic.IsPaired ? "　·　已配对" : "");
            _wsLine.Text = "WebSocket ws://127.0.0.1:" + _localMic.Port + "　浏览器在线：" + _localMic.ClientCount;

            bool busy = _testing;
            _btnTestConn.Enabled = !busy;
            _btnTestAudio.Enabled = !busy;
        }

        private static string NameOf(string state)
        {
            switch (state)
            {
                case "connected": return "已连接";
                case "connecting": return "连接中";
                case "error": return "错误";
                default: return "未连接";
            }
        }

        // ------------------------------------------------------------------
        // 选择设备
        // ------------------------------------------------------------------

        private void OnSelectDevice(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            var list = DeviceFinder.EnumeratePaired();
            if (list.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("（没有已配对的蓝牙设备）") { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                var go = new ToolStripMenuItem("打开 Windows 蓝牙设置…");
                go.Click += (s2, e2) => Shell.OpenWindowsBluetooth();
                menu.Items.Add(go);
            }
            else
            {
                foreach (var d in list)
                {
                    var item = new ToolStripMenuItem(d.Name + "  [" + d.Mac + "]") { Tag = d };
                    item.Click += (s2, e2) =>
                    {
                        var dev = (FoundDevice)((ToolStripMenuItem)s2).Tag;
                        _localMic.SetAddress(dev.Address, dev.Name);
                        _wave.Clear();
                        _wavBytes = null;
                        _valDuration.Text = _valFrames.Text = _valRate.Text = "—";
                        _btnPlay.Enabled = _btnSave.Enabled = false;
                        RefreshAll();
                    };
                    menu.Items.Add(item);
                }
            }
            menu.Show(_btnSelect, new Point(0, _btnSelect.Height));
        }

        // ------------------------------------------------------------------
        // 自检
        // ------------------------------------------------------------------

        private async Task RunTestAsync(bool waitForKey)
        {
            if (_testing) return;
            _testing = true;
            RefreshAll();

            _liveFrames = 0;
            if (waitForKey)
            {
                _recording = true;
                // 先说"准备中"，别一上来就喊"请按住" —— 准备要一两秒，
                // 用户照着按会抢跑，那一按会被丢掉且毫无提示（真机 2026-09-29 踩过）。
                // 真正就绪后由 progress 回调改成"请按住"。
                _testHint.Text = "正在准备…（约 1–2 秒，请等提示出现再按）";
                _wave.SetSamples(null);
                _wavBytes = null;
                _btnPlay.Enabled = _btnSave.Enabled = false;
                _valDuration.Text = _valFrames.Text = _valRate.Text = "—";
            }
            else
            {
                _testHint.Text = "正在测试连接…";
            }

            try
            {
                var result = await _localMic.RunSelfTestAsync(
                    msg => Post(() => _testHint.Text = msg), waitForKey);

                if (result.Ok && waitForKey && result.RawFrames != null && result.RawFrames.Count > 0)
                {
                    ShowRecording(result);
                    // 时序必须留在界面上：松手没反应时，这一行是唯一的定因依据
                    _testHint.Text = string.Format("已录制 {0:F2} 秒，点「播放」试听。", result.Seconds)
                                     + result.ControlTrace;
                }
                else
                {
                    _testHint.Text = result.Message;
                    if (!result.Ok)
                    {
                        MessageBox.Show(result.Message, "测试未通过",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                _testHint.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                _recording = false;
                _testing = false;
                RefreshAll();
            }
        }

        private void ShowRecording(LocalMic.SelfTestResult r)
        {
            WavStats stats;
            double gain;
            var wav = Wav.BuildAuto(r.RawFrames, 16000, out stats, out gain);

            _wavBytes = wav;
            _valDuration.Text = string.Format("{0:F2} 秒", r.Seconds);
            _valFrames.Text = r.Frames.ToString();
            _valRate.Text = r.Seconds > 0 ? string.Format("{0:F0} B/s", r.Bytes / r.Seconds) : "—";

            _wave.SetSamples(Wav.DecodeFrames(r.RawFrames));
            _btnPlay.Enabled = true;
            _btnSave.Enabled = true;

            // 增益显示在提示里，方便对照 AGENTS.md §7 坑 7 那个「约 ×5」的经验值
            if (gain > 1.02)
            {
                _testHint.Text = string.Format(
                    "已录制 {0:F2} 秒，自动增益 ×{1:F2}（RMS {2:F1} dBFS，削波 {3}）。点「播放」试听。",
                    r.Seconds, gain, stats.RmsDbfs, stats.Clipped) + r.ControlTrace;
            }
        }

        private void UpdateLiveStats()
        {
            if (!_recording) return;
            _valFrames.Text = _liveFrames.ToString();
            _valDuration.Text = string.Format("{0:F2} 秒", _liveFrames * 15 / 1000.0);
        }

        // ------------------------------------------------------------------
        // 播放 / 保存
        // ------------------------------------------------------------------

        private async Task TogglePlayAsync()
        {
            if (_playing)
            {
                StopPlay();
                return;
            }
            if (_wavBytes == null) return;

            _playing = true;
            _btnPlay.Text = "停止";

            try
            {
                // PlaySync 放在后台线程：前台播放会阻塞 UI，期间窗口会假死
                var bytes = _wavBytes;
                await Task.Run(() =>
                {
                    var stream = new MemoryStream(bytes);
                    var player = new SoundPlayer(stream);
                    _playerStream = stream;
                    _player = player;
                    try { player.PlaySync(); }
                    catch { }
                    finally
                    {
                        try { player.Dispose(); } catch { }
                        try { stream.Dispose(); } catch { }
                    }
                });
            }
            finally
            {
                _player = null;
                _playerStream = null;
                _playing = false;
                _btnPlay.Text = "播放";
            }
        }

        private void StopPlay()
        {
            var p = _player;
            if (p != null) { try { p.Stop(); } catch { } }
        }

        private void OnSaveWav(object sender, EventArgs e)
        {
            if (_wavBytes == null) return;
            using (var dlg = new SaveFileDialog
            {
                Filter = "WAV 音频|*.wav",
                FileName = "remote-mic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dlg.FileName, _wavBytes);
                }
                catch (Exception ex)
                {
                    Shell.Warn("保存失败：" + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// true = 关闭窗口即退出程序。只在"拿不到托盘图标"的低完整性模式下设（见 <c>TrayApp.RunWithoutTray</c>）；
        /// 正常模式下关闭只隐藏 —— 程序要继续在托盘里常驻，真正退出走托盘的「退出」。
        /// </summary>
        public bool CloseExitsApp { get; set; }

        /// <summary>在标题后追加一句说明（低完整性无托盘时用）。</summary>
        public void AppendTitleNote(string note)
        {
            Text = Text + " " + note;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!CloseExitsApp && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                StopPlay();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopPlay();
                _uiTick.Stop();
                _uiTick.Dispose();
                _localMic.DeviceIdentified -= OnDeviceIdentified;
                _localMic.BatteryUpdated -= OnBatteryUpdated;
                _localMic.StateChanged -= OnStateChanged;
                _localMic.AudioFrame -= OnAudioFrame;
            }
            base.Dispose(disposing);
        }
    }
}
