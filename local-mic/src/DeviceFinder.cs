using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DshRemoteMic
{
    internal sealed class FoundDevice
    {
        public ulong Address;
        public string Name;
        public string Mac
        {
            get
            {
                var s = Address.ToString("X12");
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 12; i += 2)
                {
                    if (i > 0) sb.Append(':');
                    sb.Append(s.Substring(i, 2));
                }
                return sb.ToString();
            }
        }
        public override string ToString() { return Name + "  [" + Mac + "]"; }
    }

    /// <summary>
    /// 从注册表枚举已配对的 BLE 设备。
    ///
    /// 为什么读注册表而不是扫描：已配对且已连接的设备不再广播，扫描必然一无所获 ——
    /// 这一点在 Python 版探针里已经踩过并验证。实测 BTHLE\Dev_c05d39f850c7 → C0:5D:39:F8:50:C7，
    /// 字节序不需要反转。
    /// </summary>
    internal static class DeviceFinder
    {
        private static readonly Regex NameHint = new Regex("遥控|Remote|RC003|小米|Mi ", RegexOptions.IgnoreCase);

        public static List<FoundDevice> EnumeratePaired()
        {
            var list = new List<FoundDevice>();
            foreach (var bus in new[] { @"SYSTEM\CurrentControlSet\Enum\BTHLE", @"SYSTEM\CurrentControlSet\Enum\BTHENUM" })
            {
                try
                {
                    using (var root = Registry.LocalMachine.OpenSubKey(bus))
                    {
                        if (root == null) continue;
                        foreach (var sub in root.GetSubKeyNames())
                        {
                            if (!sub.StartsWith("Dev_", StringComparison.OrdinalIgnoreCase)) continue;
                            if (sub.Length < 16) continue;

                            var hex = sub.Substring(4, 12);
                            ulong addr;
                            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
                                continue;

                            string name = ReadFriendlyName(root, sub);
                            list.Add(new FoundDevice { Address = addr, Name = string.IsNullOrEmpty(name) ? "(未知设备)" : name });
                        }
                    }
                }
                catch
                {
                    // 某个总线键不存在或无权读取，跳过即可
                }
            }
            return list;
        }

        /// <summary>优先挑名字像遥控器的那台；挑不到就返回第一台。</summary>
        public static FoundDevice FindRemote()
        {
            var all = EnumeratePaired();
            foreach (var d in all)
            {
                if (NameHint.IsMatch(d.Name)) return d;
            }
            return all.Count > 0 ? all[0] : null;
        }

        private static string ReadFriendlyName(RegistryKey root, string sub)
        {
            string name = "";
            try
            {
                using (var k = root.OpenSubKey(sub))
                {
                    if (k == null) return "";
                    foreach (var inst in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k2 = k.OpenSubKey(inst))
                            {
                                if (k2 == null) continue;
                                var v = k2.GetValue("FriendlyName") as string;
                                if (!string.IsNullOrEmpty(v)) name = v;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return name;
        }
    }
}
