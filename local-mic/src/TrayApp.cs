using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshRemoteMic
{
    /// <summary>
    /// 托盘外壳。存在的意义不是好看，而是让链路状态可见 —— 蓝牙链路会静默失败
    /// （遥控器休眠、系统蓝牙关掉、配对丢失），没有这个绿灯排查成本极高。
    ///
    /// 主窗口已于 2026-09-29 加入，托盘菜单相应精简为「开关 + 快速动作」，
    /// 设备信息、自检、播放这些进入主窗口。
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly LocalMic _localMic;
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _mStatus;
        private ToolStripMenuItem _mDevice;
        private ToolStripMenuItem _mBattery;
        private MainForm _form;

        // Icon.FromHandle 的句柄依赖原 Bitmap 存活，必须留着引用，否则图标会变黑
        private static readonly List<Bitmap> KeepAlive = new List<Bitmap>();

        private static readonly Color CConnected = Color.FromArgb(0x1D, 0x9E, 0x75);
        private static readonly Color CConnecting = Color.FromArgb(0xEF, 0x9F, 0x27);
        private static readonly Color CIdle = Color.FromArgb(0x88, 0x87, 0x80);
        private static readonly Color CError = Color.FromArgb(0xE2, 0x4B, 0x4A);

        public TrayApp(LocalMic localMic)
        {
            _localMic = localMic;
            _localMic.StateChanged += (s, d) => SafeRefresh();
            _localMic.DeviceIdentified += _ => SafeRefresh();
            _localMic.BatteryUpdated += _ => SafeRefresh();
            _localMic.LowBattery += OnLowBattery;

            BuildMenu();
            _icon = new NotifyIcon
            {
                Text = "DSH 遥控麦克风 local-mic",
                Icon = MakeIcon(CIdle),
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowBalloon(); };
            _icon.DoubleClick += (s, e) => ShowMain();

            SafeRefresh();
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            _mStatus = new ToolStripMenuItem("状态：—") { Enabled = false };
            _mDevice = new ToolStripMenuItem("设备：—") { Enabled = false };
            _mBattery = new ToolStripMenuItem("电量：—") { Enabled = false };

            var mOpen = new ToolStripMenuItem("打开主窗口…");
            mOpen.Font = new Font(_menu.Font, FontStyle.Bold);
            mOpen.Click += (s, e) => ShowMain();

            var mReconnect = new ToolStripMenuItem("重新连接");
            mReconnect.Click += (s, e) =>
            {
                var ignored = System.Threading.Tasks.Task.Run(async () =>
                {
                    _localMic.SetState("connecting", "手动重连…");
                    await _localMic.EnsureConnectedAsync();
                });
            };

            var mDiag = new ToolStripMenuItem("打开浏览器诊断页");
            mDiag.Click += (s, e) => Shell.OpenUrl("http://localhost:8000");

            var mAuto = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = Config.AutoStart };
            mAuto.CheckedChanged += (s, e) => Config.AutoStart = mAuto.Checked;

            var mExit = new ToolStripMenuItem("退出");
            mExit.Click += (s, e) => Exit();

            _menu.Items.Add(mOpen);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_mStatus);
            _menu.Items.Add(_mDevice);
            _menu.Items.Add(_mBattery);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(mReconnect);
            _menu.Items.Add(mDiag);
            _menu.Items.Add(mAuto);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(mExit);
        }

        private void ShowMain()
        {
            if (_form == null || _form.IsDisposed) _form = new MainForm(_localMic);
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }

        private void OnLowBattery(int level)
        {
            SafePost(() =>
            {
                _icon.BalloonTipTitle = "遥控器电量偏低";
                _icon.BalloonTipText = "当前 " + level + "%，建议充电。电量耗尽时按语音键会完全没反应。";
                _icon.ShowBalloonTip(6000);
            });
        }

        private void ShowBalloon()
        {
            _icon.BalloonTipTitle = "DSH 遥控麦克风 local-mic";
            _icon.BalloonTipText = "状态：" + NameOf(_localMic.State) + "（" + _localMic.Detail + "）\n" +
                                   "设备：" + _localMic.DisplayName +
                                   (_localMic.Battery >= 0 ? "　电量 " + _localMic.Battery + "%" : "") +
                                   "\n双击图标打开主窗口";
            _icon.ShowBalloonTip(4000);
        }

        private void SafeRefresh()
        {
            SafePost(RefreshUi);
        }

        private void SafePost(Action a)
        {
            var ctx = System.Threading.SynchronizationContext.Current;
            if (ctx != null) ctx.Post(_ => a(), null);
            else a();
        }

        private void RefreshUi()
        {
            string state = _localMic.State;
            _mStatus.Text = "状态：" + NameOf(state) + "（" + _localMic.Detail + "）";
            _mDevice.Text = "设备：" + _localMic.DisplayName + (_localMic.IsPaired ? "  已配对" : "");

            if (_localMic.Battery < 0)
            {
                _mBattery.Text = "电量：—";
            }
            else
            {
                _mBattery.Text = "电量：" + _localMic.Battery + "%" +
                                 (_localMic.IsConnected ? "" : "（上次已知）");
            }

            Color c;
            switch (state)
            {
                case "connected": c = CConnected; break;
                case "connecting": c = CConnecting; break;
                case "error": c = CError; break;
                default: c = CIdle; break;
            }
            _icon.Icon = MakeIcon(c);

            // NotifyIcon.Text 上限 127 字符，超了会抛异常
            string tip = "DSH 遥控麦克风 local-mic\n" + NameOf(state) + "：" + _localMic.Detail;
            if (_localMic.Battery >= 0) tip += "\n电量 " + _localMic.Battery + "%";
            _icon.Text = tip.Length > 126 ? tip.Substring(0, 126) : tip;
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

        private static Icon MakeIcon(Color c)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(c))
                    g.FillEllipse(brush, 4, 4, 24, 24);
                using (var pen = new Pen(Color.FromArgb(0x44, 0x44, 0x44), 2))
                    g.DrawEllipse(pen, 4, 4, 24, 24);
            }
            lock (KeepAlive) KeepAlive.Add(bmp);
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void Exit()
        {
            try { if (_form != null && !_form.IsDisposed) { _form.Dispose(); _form = null; } } catch { }
            try { _icon.Visible = false; } catch { }
            try { _localMic.Dispose(); } catch { }
            try { _icon.Dispose(); } catch { }
            ExitThread();
        }
    }
}
