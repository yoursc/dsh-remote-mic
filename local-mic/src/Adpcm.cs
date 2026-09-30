using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace DshRemoteMic
{
    /// <summary>
    /// IMA ADPCM (DVI4) 解码器 —— 与浏览器侧 js/adpcm.js 逐行对齐。
    ///
    /// 为什么 local-mic 里会有这份解码器：ADPCM 是硬件编码细节，**不能越过接缝**，
    /// 所以生产链路在 local-mic 内就把它解成 PCM16；自检窗口播放录音用的也是同一份。
    /// 它是接缝两侧唯一的"双实现"，因此靠共享黄金向量锁死（改动必须两侧同时过）。
    ///
    /// 两份实现靠 conformance/adpcm-vectors.txt 约束 —— 该文件由 Node 侧解码器生成，
    /// C# 侧 RunSelfTest() 逐字节比对。任何一边写错，自检立刻失败。
    ///
    /// 三条最容易写错的点（照抄时必须保留）：
    ///   1. predictor / stepIndex 必须跨帧连续传递，每帧重置会变成连续爆音
    ///   2. nibble 高 4 bit 先
    ///   3. predictor 每一步都要 clamp 到 int16（不只是最后）
    /// </summary>
    internal sealed class AdpcmDecoder
    {
        private static readonly int[] StepTable = {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
            19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
            337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
            5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };

        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        public int ResetCount { get; private set; }
        public int Frames { get; private set; }
        public int Predictor { get; private set; }
        public int StepIndex { get; private set; }

        public AdpcmDecoder()
        {
            Reset(0, 0);
            ResetCount = 0;
            Frames = 0;
        }

        /// <summary>重置解码状态。由 CONTROL 的 0x0A DecoderSync 驱动。</summary>
        public void Reset(int predictor, int stepIndex)
        {
            Predictor = predictor > 32767 ? 32767 : (predictor < -32768 ? -32768 : predictor);
            StepIndex = stepIndex < 0 ? 0 : (stepIndex > 88 ? 88 : stepIndex);
            ResetCount++;
        }

        /// <summary>解码一帧裸 payload，返回 PCM 样本（长度 = bytes.length * 2）。</summary>
        public short[] DecodeFrame(byte[] bytes)
        {
            if (bytes == null) return new short[0];
            var outBuf = new short[bytes.Length * 2];
            int predictor = Predictor;
            int stepIndex = StepIndex;
            int o = 0;

            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                DecodeNibble((b >> 4) & 0x0F, ref predictor, ref stepIndex);
                outBuf[o++] = (short)predictor;

                DecodeNibble(b & 0x0F, ref predictor, ref stepIndex);
                outBuf[o++] = (short)predictor;
            }

            Predictor = predictor;
            StepIndex = stepIndex;
            Frames++;
            return outBuf;
        }

        private static void DecodeNibble(int code, ref int predictor, ref int stepIndex)
        {
            int step = StepTable[stepIndex];
            int diff = step >> 3;
            if ((code & 4) != 0) diff += step;
            if ((code & 2) != 0) diff += step >> 1;
            if ((code & 1) != 0) diff += step >> 2;

            int sample = (code & 8) != 0 ? predictor - diff : predictor + diff;
            if (sample > 32767) sample = 32767;
            else if (sample < -32768) sample = -32768;

            int si = stepIndex + IndexTable[code];
            if (si < 0) si = 0;
            else if (si > 88) si = 88;

            predictor = sample;
            stepIndex = si;
        }

        // ------------------------------------------------------------------
        // 自检：拿 conformance/adpcm-vectors.txt 比对（Node 侧生成的真值）
        // ------------------------------------------------------------------

        private const string ResourceName = "DshRemoteMic.adpcm-vectors.txt";

        /// <summary>跑共享黄金向量。返回报告，最后一行是结论。</summary>
        public static string RunSelfTest()
        {
            var sb = new StringBuilder();
            var asm = Assembly.GetExecutingAssembly();

            string text;
            using (var s = asm.GetManifestResourceStream(ResourceName))
            {
                if (s == null)
                {
                    return "找不到嵌入资源 " + ResourceName + "（是否漏了 csproj 里的 EmbeddedResource？）";
                }
                using (var r = new StreamReader(s, Encoding.UTF8))
                    text = r.ReadToEnd();
            }

            int total = 0, failed = 0;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var arrow = line.IndexOf("->", StringComparison.Ordinal);
                if (arrow < 0) continue;

                string left = line.Substring(0, arrow).Trim();
                string right = line.Substring(arrow + 2).Trim();

                int p = 0, si = 0;
                int bar = left.IndexOf('|');
                string hex = left;
                if (bar >= 0)
                {
                    var state = left.Substring(0, bar);
                    hex = left.Substring(bar + 1);
                    foreach (var part in state.Split(','))
                    {
                        var kv = part.Trim();
                        if (kv.StartsWith("p=", StringComparison.Ordinal))
                            int.TryParse(kv.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out p);
                        else if (kv.StartsWith("i=", StringComparison.Ordinal))
                            int.TryParse(kv.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out si);
                    }
                }
                hex = hex.Trim();

                if (hex.Length == 0 || (hex.Length % 2) != 0)
                {
                    sb.AppendLine("向量文件格式错（hex 长度非偶数）：" + line);
                    failed++;
                    total++;
                    continue;
                }

                var bytes = new byte[hex.Length / 2];
                bool badHex = false;
                for (int i = 0; i < bytes.Length; i++)
                {
                    int v;
                    if (!int.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                    {
                        badHex = true;
                        break;
                    }
                    bytes[i] = (byte)v;
                }
                if (badHex)
                {
                    sb.AppendLine("向量文件格式错（非法 hex）：" + line);
                    failed++;
                    total++;
                    continue;
                }

                var expected = new System.Collections.Generic.List<short>();
                foreach (var tk in right.Split(','))
                {
                    var t = tk.Trim();
                    if (t.Length == 0) continue;
                    int val;
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                        expected.Add((short)val);
                }

                total++;
                var dec = new AdpcmDecoder();
                dec.Reset(p, si);
                var got = dec.DecodeFrame(bytes);

                bool ok = got.Length == expected.Count;
                int firstBad = -1;
                if (ok)
                {
                    for (int i = 0; i < got.Length; i++)
                    {
                        if (got[i] != expected[i]) { ok = false; firstBad = i; break; }
                    }
                }
                else
                {
                    firstBad = Math.Min(got.Length, expected.Count);
                }

                if (ok)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  OK    {0}...  样本 {1}", Trunc(hex, 16), got.Length));
                }
                else
                {
                    failed++;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  失败  {0}...  第 {1} 个样本 期望 {2} 实得 {3}",
                        Trunc(hex, 16), firstBad,
                        firstBad < expected.Count ? expected[firstBad].ToString() : "(越界)",
                        firstBad < got.Length ? got[firstBad].ToString() : "(越界)"));
                }
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0
                ? string.Format(CultureInfo.InvariantCulture, "ADPCM 自检通过：{0}/{1} 条向量与 Node 侧完全一致", total, total)
                : string.Format(CultureInfo.InvariantCulture, "ADPCM 自检失败：{0}/{1} 条不一致", failed, total));
            return sb.ToString();
        }

        private static string Trunc(string s, int n)
        {
            return s.Length <= n ? s.PadRight(n) : s.Substring(0, n);
        }
    }
}
