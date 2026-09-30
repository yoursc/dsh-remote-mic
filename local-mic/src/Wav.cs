using System;
using System.Collections.Generic;

namespace DshRemoteMic
{
    internal sealed class WavStats
    {
        public int Samples;
        public double Seconds;
        public double PeakDbfs = double.NegativeInfinity;
        public double RmsDbfs = double.NegativeInfinity;
        public int Clipped;
        public double Gain = 1.0;
    }

    /// <summary>
    /// 把解码后的 PCM16 组装成规范的 16 kHz 单声道 WAV —— 与 js/wav.js 对齐。
    ///
    /// dsh 语音链路只认这种格式（AGENTS.md §6.1），而 ATVV 解码输出正好就是，
    /// 所以这里是零转换，只补 44 字节头。
    /// </summary>
    internal static class Wav
    {
        /// <summary>
        /// 把一批原始 ADPCM 帧解成 PCM，状态跨帧连续。
        /// 这是 ATVV 与普通 ADPCM 最大的差别 —— 每帧都重置的话声音会变成连续爆音。
        /// </summary>
        public static List<short[]> DecodeFrames(List<byte[]> frames)
        {
            var dec = new AdpcmDecoder();
            var chunks = new List<short[]>(frames.Count);
            foreach (var f in frames) chunks.Add(dec.DecodeFrame(f));
            return chunks;
        }

        public static byte[] Build(List<short[]> chunks, int sampleRate, double gain, out WavStats stats)
        {
            int total = 0;
            foreach (var c in chunks) total += c.Length;

            int dataBytes = total * 2;
            var buf = new byte[44 + dataBytes];

            Ascii(buf, 0, "RIFF");
            PutU32(buf, 4, (uint)(36 + dataBytes));
            Ascii(buf, 8, "WAVE");
            Ascii(buf, 12, "fmt ");
            PutU32(buf, 16, 16);
            PutU16(buf, 20, 1);              // PCM
            PutU16(buf, 22, 1);              // 单声道
            PutU32(buf, 24, (uint)sampleRate);
            PutU32(buf, 28, (uint)(sampleRate * 2));   // byteRate
            PutU16(buf, 32, 2);              // blockAlign
            PutU16(buf, 34, 16);             // bitsPerSample
            Ascii(buf, 36, "data");
            PutU32(buf, 40, (uint)dataBytes);

            int off = 44, clipped = 0;
            double peak = 0, sumSq = 0;

            foreach (var c in chunks)
            {
                for (int i = 0; i < c.Length; i++)
                {
                    double s = c[i] * gain;
                    if (s > 32767) { s = 32767; clipped++; }
                    else if (s < -32768) { s = -32768; clipped++; }

                    short v = (short)s;
                    buf[off++] = (byte)(v & 0xFF);
                    buf[off++] = (byte)((v >> 8) & 0xFF);

                    double a = v < 0 ? -v : v;
                    if (a > peak) peak = a;
                    sumSq += s * s;          // 与 js/wav.js 一致：用增益后、截断前的值
                }
            }

            double rms = total > 0 ? Math.Sqrt(sumSq / total) : 0;
            stats = new WavStats
            {
                Samples = total,
                Seconds = sampleRate > 0 ? total / (double)sampleRate : 0,
                PeakDbfs = Dbfs(peak),
                RmsDbfs = Dbfs(rms),
                Clipped = clipped,
                Gain = gain,
            };
            return buf;
        }

        /// <summary>
        /// 解码 + 自动增益 + 组装，一步到位。
        ///
        /// 增益必须按 RMS 定，不能按峰值定：实测录音里有极少数瞬态尖峰（触顶样本仅 0.004%），
        /// 按峰值算会被它们拉低到 ×0.89 —— 反而更小声。AGENTS.md §7 坑 7 给的经验值 ×5
        /// 就是这么来的，这里用 RMS 归一到 -18 dBFS 得到同样的量级。
        /// </summary>
        public static byte[] BuildAuto(List<byte[]> frames, int sampleRate, out WavStats stats, out double appliedGain)
        {
            var chunks = DecodeFrames(frames);

            WavStats probe;
            Build(chunks, sampleRate, 1.0, out probe);

            double rmsLinear = DbfsToLinear(probe.RmsDbfs);
            double targetRms = 32768 * Math.Pow(10, -18 / 20.0);
            double auto = rmsLinear > 1 ? Math.Min(10, targetRms / rmsLinear) : 1;
            if (auto < 1) auto = 1;

            appliedGain = auto > 1.02 ? auto : 1.0;
            return Build(chunks, sampleRate, appliedGain, out stats);
        }

        private static double Dbfs(double x)
        {
            return x > 0 ? 20 * Math.Log10(x / 32768) : double.NegativeInfinity;
        }

        private static double DbfsToLinear(double db)
        {
            return double.IsNegativeInfinity(db) ? 0 : 32768 * Math.Pow(10, db / 20);
        }

        private static void Ascii(byte[] b, int off, string s)
        {
            for (int i = 0; i < s.Length; i++) b[off + i] = (byte)s[i];
        }

        private static void PutU16(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void PutU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }
    }
}
