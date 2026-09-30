using System;
using System.Collections.Generic;

namespace DshRemoteMic
{
    /// <summary>
    /// 已验证的设备型号白名单。将来支持新设备，只需要在这里加一行。
    /// </summary>
    internal sealed class DeviceProfile
    {
        /// <summary>
        /// DIS Model Number 的前缀。必须按前缀匹配，不能写死完整型号 ——
        /// 实测 RC003 在 DIS 里报的是 <c>RC003</c>，<b>没有 -MS 后缀</b>；
        /// 如果按 == "RC003-MS" 精确匹配，会永远匹配不上然后静默退化成未知设备。
        /// </summary>
        public string[] ModelPrefixes;

        public string DisplayName;
        public string Note;
    }

    internal sealed class DeviceInfo
    {
        public string Model = "";
        public string Manufacturer = "";
        public string Firmware = "";
        public string Hardware = "";
        public string Serial = "";

        /// <summary>是否命中白名单。false 表示未验证，但设备仍然可用。</summary>
        public bool IsKnown;

        public string DisplayName = "";
        public string Note = "";

        /// <summary>界面上那行次要信息：厂商 · 固件 · 序列号。</summary>
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Manufacturer)) parts.Add(Manufacturer);
                if (!string.IsNullOrEmpty(Firmware)) parts.Add("固件 " + Firmware);
                if (!string.IsNullOrEmpty(Hardware)) parts.Add(Hardware);
                if (!string.IsNullOrEmpty(Serial)) parts.Add("SN " + Serial);
                return string.Join(" · ", parts.ToArray());
            }
        }
    }

    internal static class DeviceProfiles
    {
        private static readonly List<DeviceProfile> Known = new List<DeviceProfile>
        {
            new DeviceProfile
            {
                // 2026-09-29 真机实测：Model=RC003 Manufacturer=MIOM HW=V2.0 FW=2671 SN=250519
                ModelPrefixes = new[] { "RC003" },
                DisplayName = "小米蓝牙遥控器 2 Pro",
                Note = "v1 唯一验证过的设备",
            },
        };

        /// <summary>
        /// 按 DIS 读到的型号查白名单。
        ///
        /// 查不到不会拒绝使用 —— ATVV 是 Google 的通用协议，别家遥控器可能完全可用，
        /// 因为型号不认识就断开是把路走窄了。这里只标注「未验证」，由用户自行承担。
        /// </summary>
        public static DeviceInfo Identify(string model, string manufacturer, string firmware,
            string hardware, string serial, string fallbackName)
        {
            var info = new DeviceInfo
            {
                Model = Clean(model),
                Manufacturer = Clean(manufacturer),
                Firmware = Clean(firmware),
                Hardware = Clean(hardware),
                Serial = Clean(serial),
            };

            if (info.Model.Length > 0)
            {
                foreach (var p in Known)
                {
                    foreach (var prefix in p.ModelPrefixes)
                    {
                        if (info.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            info.IsKnown = true;
                            info.DisplayName = p.DisplayName;
                            info.Note = p.Note;
                            return info;
                        }
                    }
                }
            }

            // 降级：型号没读到或不在表里。设备照用，但明确标注未验证。
            info.IsKnown = false;
            info.DisplayName = info.Model.Length > 0
                ? info.Model
                : (string.IsNullOrEmpty(fallbackName) ? "未知 ATVV 设备" : fallbackName);
            info.Note = "未验证的设备型号 —— ATVV 协议通用，可能能用，但没测过";
            return info;
        }

        /// <summary>
        /// DIS 字符串收尾常有 NUL 填充。实测 Device Name 是 '小米蓝牙语音遥控器\0'，
        /// 不清掉的话界面上会出现一个看不见的字符，还可能破坏字符串比较。
        /// </summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim().TrimEnd('\0').Trim();
        }
    }
}
