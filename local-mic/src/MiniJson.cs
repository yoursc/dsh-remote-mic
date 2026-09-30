using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DshRemoteMic
{
    /// <summary>
    /// 极简 JSON 构造/解析 —— 只为 proto 1 线缆协议的五条消息服务。
    ///
    /// 为什么手写而不引 Newtonsoft：那是几 MB 的依赖，而产物目标是 91 KB 单文件 exe。
    /// 协议消息扁平、字段固定，正则足够，而且每一条输出都被 PROTOCOL.md §14 的夹具约束着。
    ///
    /// ⚠ 解析侧的纪律（PROTOCOL.md §4.2）：**取不到就当缺失，按缺省值处理，绝不抛异常。**
    /// 字段类型错误与字段缺失走同一条路 —— 客户端少写一个字段不该让它连不上，
    /// 但 `proto` 取不到会按 0 处理 ⇒ 被拒绝（fail-closed，这是有意的）。
    /// </summary>
    internal static class MiniJson
    {
        // ---------- 构造：local-mic → 客户端 ----------

        public static string State(string state, string detail)
        {
            return "{\"op\":\"state\",\"state\":\"" + Esc(state) + "\",\"detail\":\"" + Esc(detail) + "\"}";
        }

        /// <summary>协议能力。**不含任何设备信息** —— 那些走 device 消息（§4.1 已裁决二者分离）。</summary>
        public static string Ready(int proto, string[] caps, int rate, int channels)
        {
            var sb = new StringBuilder();
            sb.Append("{\"op\":\"ready\",\"proto\":").Append(proto.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"caps\":[");
            if (caps != null)
            {
                for (int i = 0; i < caps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(caps[i])).Append('"');
                }
            }
            sb.Append("],\"audio\":{\"format\":\"s16le\",\"rate\":")
              .Append(rate.ToString(CultureInfo.InvariantCulture))
              .Append(",\"channels\":").Append(channels.ToString(CultureInfo.InvariantCulture))
              .Append("}}");
            return sb.ToString();
        }

        /// <summary>设备身份。battery 是「最后已知值」，新鲜度由 state 表达（§7）。</summary>
        public static string Device(string id, string name, string model, bool paired, int battery)
        {
            return "{\"op\":\"device\",\"id\":\"" + Esc(id) + "\",\"name\":\"" + Esc(name) +
                   "\",\"model\":\"" + Esc(model) + "\",\"paired\":" + (paired ? "true" : "false") +
                   ",\"battery\":" + battery.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string Capture(string phase, string reason, string source)
        {
            var sb = new StringBuilder();
            sb.Append("{\"op\":\"capture\",\"phase\":\"").Append(Esc(phase)).Append('"');
            if (reason != null) sb.Append(",\"reason\":\"").Append(Esc(reason)).Append('"');
            if (source != null) sb.Append(",\"source\":\"").Append(Esc(source)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 结构化错误。没有 args 字段（2026-09-29 删除，理由见 §9）。
        /// message 是诊断兜底，可能是 Windows 异常原文 —— 客户端已知 code 时不应展示它。
        /// </summary>
        public static string Error(string code, bool retryable, string message)
        {
            return "{\"op\":\"error\",\"code\":\"" + Esc(code) + "\",\"retryable\":" +
                   (retryable ? "true" : "false") + ",\"message\":\"" + Esc(message) + "\"}";
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        // ---------- 解析：客户端 → local-mic ----------

        public static string GetString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// 取整数字段。取不到（缺失或类型不对）返回 false —— 调用方按缺省值处理。
        /// hello.proto 走这条路：取不到 ⇒ 视为 0 ⇒ 拒绝（§6）。
        /// </summary>
        public static bool TryGetInt(string json, string key, out int value)
        {
            value = 0;
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>取布尔字段。取不到返回 false —— 调用方自行决定缺省（audio 的缺省是 true）。</summary>
        public static bool TryGetBool(string json, string key, out bool value)
        {
            value = false;
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            if (!m.Success) return false;
            value = m.Groups[1].Value == "true";
            return true;
        }
    }
}
