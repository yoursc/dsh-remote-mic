using System;
using System.Text;

namespace DshRemoteMic
{
    /// <summary>ATVV 协议常量。与浏览器侧 js/atvv-consts.js 保持一致。</summary>
    internal static class Atvv
    {
        public static readonly Guid Service  = new Guid("ab5e0001-5a21-4f05-bc7d-af01f617b664");
        public static readonly Guid Transmit = new Guid("ab5e0002-5a21-4f05-bc7d-af01f617b664");
        public static readonly Guid Audio    = new Guid("ab5e0003-5a21-4f05-bc7d-af01f617b664");
        public static readonly Guid Control  = new Guid("ab5e0004-5a21-4f05-bc7d-af01f617b664");

        // CONTROL 通知首字节
        public const byte OpStreamStopped     = 0x00;
        public const byte OpStreamStarted     = 0x04;
        public const byte OpMicOpenRequested  = 0x08;
        public const byte OpDecoderSync       = 0x0A;
        public const byte OpCaps              = 0x0B;

        // 主机 -> 设备
        public static readonly byte[] CmdGetCaps = { 0x0A, 0x01, 0x00, 0x00, 0x03, 0x03 };

        public const int DefaultFrameSize = 120;

        /// <summary>
        /// Caps 解析。与 js/session.js 的 parseCaps 逐字节对齐，包括那个必须保留的回落分支：
        /// RC003 固件会把 codecs 报成 0，真实 codec 信息藏在 bytes[4] 的低两位。
        /// 真机上两种形态（byte[3] = 0x00 与 0x02）都出现过，别把这个分支"简化"掉。
        /// </summary>
        public static Caps ParseCaps(byte[] b)
        {
            var c = new Caps();
            if (b == null || b.Length < 7)
            {
                c.Raw = b == null ? "" : Hex(b);
                c.FrameSize = DefaultFrameSize;
                return c;
            }

            c.Version = (b[1] << 8) | b[2];
            int codecs = b[3];
            if (c.Version >= 0x0100 && codecs == 0)
                codecs = b[4] & 0x03;
            c.Codecs = codecs;
            c.FrameSize = ((b[5] << 8) | b[6]) != 0 ? ((b[5] << 8) | b[6]) : DefaultFrameSize;
            c.Supports16k = (codecs & 0x02) != 0;
            c.Raw = Hex(b);
            return c;
        }

        public static string Hex(byte[] b)
        {
            if (b == null) return "";
            var sb = new StringBuilder(b.Length * 3);
            foreach (var x in b) sb.Append(x.ToString("x2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    internal sealed class Caps
    {
        public int Version;
        public int Codecs;
        public int FrameSize;
        public bool Supports16k;
        public string Raw = "";

        public string Summary
        {
            get
            {
                return string.Format(
                    "version=0x{0:x4}  codecs=0x{1:x2}  帧大小={2}B  16kHz={3}",
                    Version, Codecs, FrameSize, Supports16k ? "可用" : "不可用");
            }
        }
    }
}
