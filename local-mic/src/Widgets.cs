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
    /// 自绘波形。为什么不用 Chart 控件：那要引 System.Windows.Forms.DataVisualization，
    /// 会附带上百 KB 的 DLL，破坏「单文件 exe」这个目标。这里画一段包络只要几十行。
    /// </summary>
    internal sealed class WavePanel : Control
    {
        private short[] _samples;

        private static readonly Color CBack = Color.FromArgb(0xF7, 0xF7, 0xF5);
        private static readonly Color CWave = Color.FromArgb(0x37, 0x8A, 0xDD);
        private static readonly Color CMid = Color.FromArgb(0xD3, 0xD1, 0xC7);
        private static readonly Color CHint = Color.FromArgb(0x9A, 0x98, 0x90);

        public WavePanel()
        {
            DoubleBuffered = true;
            BackColor = CBack;
            Height = 60;
        }

        public void SetSamples(List<short[]> chunks)
        {
            if (chunks == null || chunks.Count == 0)
            {
                _samples = null;
            }
            else
            {
                int total = 0;
                foreach (var c in chunks) total += c.Length;
                _samples = new short[total];
                int o = 0;
                foreach (var c in chunks) { Array.Copy(c, 0, _samples, o, c.Length); o += c.Length; }
            }
            Invalidate();
        }

        public void Clear()
        {
            _samples = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(CBack);
            int w = Width, h = Height;
            int mid = h / 2;

            using (var midPen = new Pen(CMid))
                g.DrawLine(midPen, 0, mid, w, mid);

            if (_samples == null || _samples.Length == 0)
            {
                using (var hint = new SolidBrush(CHint))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("尚未录制", Font, hint, new RectangleF(0, 0, w, h), sf);
                return;
            }

            using (var pen = new Pen(CWave))
            {
                double step = _samples.Length / (double)Math.Max(1, w);
                for (int x = 0; x < w; x++)
                {
                    int i0 = (int)(x * step);
                    int i1 = (int)((x + 1) * step);
                    if (i1 <= i0) i1 = i0 + 1;

                    short mn = short.MaxValue, mx = short.MinValue;
                    for (int i = i0; i < i1 && i < _samples.Length; i++)
                    {
                        short v = _samples[i];
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                    if (mn == short.MaxValue) break;

                    int y0 = mid - (int)(mx / 32768.0 * (mid - 2));
                    int y1 = mid - (int)(mn / 32768.0 * (mid - 2));
                    if (y1 == y0) y1 = y0 + 1;
                    g.DrawLine(pen, x, y0, x, y1);
                }
            }
        }
    }

    /// <summary>电量条。自绘是为了按档位换色（低电量变红），ProgressBar 做不到。</summary>
    internal sealed class BatteryBar : Control
    {
        private int _percent = -1;
        private bool _stale;

        private static readonly Color CTrack = Color.FromArgb(0xEE, 0xEC, 0xE6);
        private static readonly Color CHigh = Color.FromArgb(0x63, 0x99, 0x22);
        private static readonly Color CMid = Color.FromArgb(0xEF, 0x9F, 0x27);
        private static readonly Color CLow = Color.FromArgb(0xE2, 0x4B, 0x4A);

        public BatteryBar()
        {
            DoubleBuffered = true;
            Height = 12;
        }

        /// <summary>stale = 数据来自上一次连接，界面要灰显，别让过期值看起来像实时值。</summary>
        public void SetPercent(int percent, bool stale)
        {
            _percent = percent;
            _stale = stale;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(CTrack);

            if (_percent >= 0)
            {
                var c = _percent > 50 ? CHigh : (_percent > 20 ? CMid : CLow);
                if (_stale) c = Color.FromArgb(160, c);
                int w = (int)(Width * Math.Min(100, _percent) / 100.0);
                if (w > 0)
                {
                    using (var b = new SolidBrush(c))
                        g.FillRectangle(b, 0, 0, w, Height);
                }
            }
        }
    }
}
