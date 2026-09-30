using System;
using System.Globalization;
using Microsoft.Win32;

namespace DshRemoteMic
{
    /// <summary>
    /// 配置持久化。存在 HKCU 下，不需要管理员权限，也不需要引入 JSON 库。
    /// </summary>
    internal static class Config
    {
        private const string Root = @"Software\DshRemoteMic";

        public static string GetString(string name, string fallback)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Root))
            {
                var v = k.GetValue(name) as string;
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
        }

        public static void SetString(string name, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Root))
            {
                k.SetValue(name, value ?? string.Empty, RegistryValueKind.String);
            }
        }

        public static int GetInt(string name, int fallback)
        {
            int v;
            return int.TryParse(GetString(name, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static void SetInt(string name, int value)
        {
            SetString(name, value.ToString(CultureInfo.InvariantCulture));
        }

        // ---------- 具体配置项 ----------

        /// <summary>遥控器蓝牙地址（ulong 形式）。0 表示未配置，启动时自动发现。</summary>
        public static ulong Address
        {
            get
            {
                ulong v;
                return ulong.TryParse(GetString("Address", "0"),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
            set { SetString("Address", value.ToString("X12", CultureInfo.InvariantCulture)); }
        }

        public static int Port
        {
            get { return GetInt("Port", 8787); }
            set { SetInt("Port", value); }
        }

        /// <summary>开机自启。写 HKCU\...\Run，同样是免管理员的做法。</summary>
        public static bool AutoStart
        {
            get { return GetInt("AutoStart", 0) != 0; }
            set
            {
                SetInt("AutoStart", value ? 1 : 0);
                ApplyAutoStart(value);
            }
        }

        private static void ApplyAutoStart(bool enable)
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (var k = Registry.CurrentUser.OpenSubKey(runKey, true))
            {
                if (k == null) return;
                if (enable)
                {
                    string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                    k.SetValue("local-mic", "\"" + exe + "\"", RegistryValueKind.String);
                }
                else
                {
                    try { k.DeleteValue("local-mic", false); } catch { }
                }
            }
        }
    }
}
