using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace DshRemoteMic
{
    /// <summary>local-mic 对一条客户端消息的处置动作。</summary>
    internal enum ClientMsgAction
    {
        /// <summary>接受（hello 校验通过）。</summary>
        Accept,
        /// <summary>回 error 并**关闭连接** —— 版本不匹配，继续只会错解字节。</summary>
        RejectClose,
        /// <summary>回 error 但**保持连接** —— 客户端实现有小毛病，不值得踢掉。</summary>
        RejectKeep,
        /// <summary>真·未知 op：静默忽略（不变量 8，为将来只增留出空间）。</summary>
        Ignore,
        /// <summary>无法解析：直接关闭，没有 error 可回。</summary>
        Malformed,
    }

    internal sealed class ClientMsgVerdict
    {
        public ClientMsgAction Action;
        public string ErrorCode;
        public bool WantAudio = true;

        public static ClientMsgVerdict Of(ClientMsgAction action, string errorCode)
        {
            return new ClientMsgVerdict { Action = action, ErrorCode = errorCode };
        }
    }

    /// <summary>
    /// 客户端消息的判定逻辑 —— 刻意做成**不依赖 WebSocket 的纯函数**。
    ///
    /// 为什么抽出来：PROTOCOL.md §14 要求夹具是**可执行**的，而不是文档里的一段话。
    /// 握手语义若埋在 WS 回调里，就只能靠联调发现错误；
    /// 抽成纯函数后，`--selftest` 能把每一条夹具跑一遍，退出码比任何文档都可靠。
    /// </summary>
    internal static class Protocol
    {
        /// <summary>判定一条客户端文本消息该怎么处置。不产生任何副作用。</summary>
        public static ClientMsgVerdict Classify(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ClientMsgVerdict.Of(ClientMsgAction.Malformed, null);

            string op = MiniJson.GetString(text, "op");

            // 拿不到 op ⇒ 不是本协议的客户端。WS 有消息边界，坏 JSON 不是粘包造成的。
            if (string.IsNullOrEmpty(op))
                return ClientMsgVerdict.Of(ClientMsgAction.Malformed, null);

            if (op == "hello")
            {
                int proto;
                // 缺失或类型不对都按 0 处理 ⇒ 被拒（fail-closed，§6）
                if (!MiniJson.TryGetInt(text, "proto", out proto)) proto = 0;

                if (proto != LocalMic.ProtoVersion)
                    return ClientMsgVerdict.Of(ClientMsgAction.RejectClose, "proto_mismatch");

                bool wantAudio;
                if (!MiniJson.TryGetBool(text, "audio", out wantAudio)) wantAudio = true;   // 缺省订阅

                var v = ClientMsgVerdict.Of(ClientMsgAction.Accept, null);
                v.WantAudio = wantAudio;
                return v;
            }

            if (op == "write")
            {
                // 不是「未知 op」，是**已知但被删除**（§4.2）：显式报错，但保持连接。
                return ClientMsgVerdict.Of(ClientMsgAction.RejectKeep, "op_not_supported");
            }

            return ClientMsgVerdict.Of(ClientMsgAction.Ignore, null);
        }

        // ------------------------------------------------------------------
        // 一致性夹具：conformance/protocol-vectors.txt（PROTOCOL.md §14）
        // ------------------------------------------------------------------

        private const string ResourceName = "DshRemoteMic.protocol-vectors.txt";

        /// <summary>跑消息级夹具。返回报告，最后一行是结论。</summary>
        internal static string RunSelfTest()
        {
            var sb = new StringBuilder();
            var asm = Assembly.GetExecutingAssembly();

            string text;
            using (var s = asm.GetManifestResourceStream(ResourceName))
            {
                if (s == null)
                    return "找不到嵌入资源 " + ResourceName + "（是否漏了 csproj 里的 EmbeddedResource？）";
                using (var r = new StreamReader(s, Encoding.UTF8))
                    text = r.ReadToEnd();
            }

            int total = 0, failed = 0;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0) continue;

                string left = line.Substring(0, arrow).Trim();
                string expect = line.Substring(arrow + 2).Trim();

                // L→C 测的是**客户端**如何处置 local-mic 消息，由 TS 侧夹具覆盖
                // （diagnostics/test/l2-protocol.test.mjs）。local-mic 不该替客户端判：
                // 同一份夹具两端各跑各的责任，遇不到自己的方向就跳过，而不是报错。
                if (left.StartsWith("L→C", StringComparison.Ordinal)) continue;

                total++;
                string problem = null;

                if (left.StartsWith("C→L", StringComparison.Ordinal))
                {
                    problem = CheckInbound(left.Substring(3).Trim(), expect);
                }
                else if (left.StartsWith("BUILD", StringComparison.Ordinal))
                {
                    problem = CheckBuilt(left.Substring(5).Trim(), expect);
                }
                else
                {
                    problem = "夹具写法不认识（应以 C→L / L→C / BUILD 开头）";
                }

                if (problem == null)
                {
                    sb.Append("  OK    ").AppendLine(Trunc(left, 52));
                }
                else
                {
                    failed++;
                    sb.Append("  失败  ").Append(Trunc(left, 52)).Append("  ").AppendLine(problem);
                }
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0
                ? string.Format(CultureInfo.InvariantCulture, "协议夹具通过：{0}/{1} 条与 PROTOCOL.md §14 一致", total, total)
                : string.Format(CultureInfo.InvariantCulture, "协议夹具失败：{0}/{1} 条不一致", failed, total));
            return sb.ToString();
        }

        /// <summary>C→L：拿真实消息喂 Classify，比对期望动作。</summary>
        private static string CheckInbound(string json, string expect)
        {
            ClientMsgAction wantAction;
            string wantCode;
            if (!ParseExpect(expect, out wantAction, out wantCode))
                return "夹具的期望值写错了：" + expect;

            var got = Classify(json);
            if (got.Action != wantAction)
                return "动作不符：期望 " + wantAction + " 实得 " + got.Action;
            if (wantCode != null && got.ErrorCode != wantCode)
                return "错误码不符：期望 " + wantCode + " 实得 " + (got.ErrorCode ?? "(无)");
            return null;
        }

        /// <summary>BUILD：构造一条待发消息，断言它含/不含某些字段（防硬件词汇回流）。</summary>
        private static string CheckBuilt(string what, string expect)
        {
            string json;
            switch (what)
            {
                case "ready":
                    json = MiniJson.Ready(LocalMic.ProtoVersion, new string[0], 16000, 1);
                    break;
                case "device":
                    json = MiniJson.Device("C0:5D:39:F8:50:C7", "小米蓝牙遥控器 2 Pro", "RC003", true, 99);
                    break;
                case "capture-start":
                    json = MiniJson.Capture("start", null, "remote");
                    break;
                case "capture-end":
                    json = MiniJson.Capture("end", "released", "remote");
                    break;
                case "error":
                    json = MiniJson.Error("pairing_required", false, "Authentication failed");
                    break;

                // 二进制帧不走 JSON 断言，单独判（真机 2026-09-30：漏了 kind 字节，
                // 客户端把采样值当 kind，整段音频被丢 —— 这条就是为它加的）
                case "audio-frame":
                    return CheckAudioFrame(expect);

                default:
                    return "夹具要构造的消息不认识：" + what;
            }

            foreach (var term in expect.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (term.StartsWith("含:", StringComparison.Ordinal))
                {
                    var key = term.Substring(2);
                    if (json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) < 0)
                        return "缺少字段 " + key + " ⇒ " + json;
                }
                else if (term.StartsWith("不含:", StringComparison.Ordinal))
                {
                    var key = term.Substring(3);
                    if (json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0)
                        return "不该出现字段 " + key + " ⇒ " + json;
                }
                else
                {
                    return "夹具的断言写法不认识：" + term;
                }
            }
            return null;
        }

        /// <summary>
        /// 音频帧断言。拿一段假 PCM 走**真实的发送侧构造**（WsServer.BuildAudioPayload），
        /// 而不是照抄一份格式 —— 只有走生产函数，夹具才守得住这条缝。
        /// </summary>
        private static string CheckAudioFrame(string expect)
        {
            var pcm = new byte[480];                       // 240 样本 = 15 ms
            var payload = WsServer.BuildAudioPayload(pcm);

            foreach (var term in expect.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (term == "含:kind:0x02")
                {
                    if (payload.Length == 0 || payload[0] != WsServer.AudioFrameKind)
                        return "首字节不是 0x02（实为 0x"
                            + (payload.Length > 0 ? payload[0].ToString("x2") : "(空)") + "）⇒ 客户端会把采样值当 kind";
                }
                else if (term == "含:pcm偶数长度")
                {
                    // PCM 部分必须是偶数（一个样本 2 字节）；整帧含 kind 反而是奇数
                    int pcmLen = payload.Length - 1;
                    if (pcmLen <= 0 || pcmLen % 2 != 0)
                        return "PCM 部分长度非偶数：" + pcmLen;
                }
                else
                {
                    return "夹具的断言写法不认识：" + term;
                }
            }
            return null;
        }

        private static bool ParseExpect(string expect, out ClientMsgAction action, out string code)
        {
            code = null;
            if (expect.StartsWith("接受", StringComparison.Ordinal)) { action = ClientMsgAction.Accept; return true; }
            if (expect.StartsWith("忽略", StringComparison.Ordinal)) { action = ClientMsgAction.Ignore; return true; }

            if (expect.StartsWith("拒绝", StringComparison.Ordinal))
            {
                int colon = expect.IndexOf('：');
                string what = colon >= 0 ? expect.Substring(colon + 1).Trim() : "";
                if (what == "malformed") { action = ClientMsgAction.Malformed; return true; }
                if (what == "op_not_supported") { action = ClientMsgAction.RejectKeep; code = what; return true; }
                action = ClientMsgAction.RejectClose;
                code = what.Length > 0 ? what : null;
                return true;
            }

            action = ClientMsgAction.Accept;
            return false;
        }

        private static string Trunc(string s, int n)
        {
            return s.Length <= n ? s.PadRight(n) : s.Substring(0, n);
        }
    }
}
