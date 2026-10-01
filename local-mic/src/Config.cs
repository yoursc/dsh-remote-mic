using System;
using System.Globalization;
using Microsoft.Win32;

namespace DshRemoteMic
{
    /// <summary>
    /// 配置持久化。存在 HKCU 下，不需要管理员权限，也不需要引入 JSON 库。
    ///
    /// ⚠ **读与写必须分开**：读只用 <see cref="RegistryKey.OpenSubKey(string)"/>（只读），
    /// 只有写才用 <c>CreateSubKey</c>。
    ///
    /// 这条纪律是有代价换来的：某些启动环境（低完整性 / 沙箱）**拒绝注册表写入**，
    /// 而 <c>CreateSubKey</c> 即便只是"打开已存在的键"也要求写权限 ⇒ 构造 LocalMic 时
    /// 直接抛 <c>UnauthorizedAccessException</c>；WinExe 没有控制台 ⇒ 用户看到的现象是
    /// **"双击了，托盘里什么都没有"**，没有任何线索。读配置失败必须只退化成默认值。
    /// 写入失败也不能崩：记进 <see cref="LastWriteError"/>，由 UI 决定怎么提示。
    /// </summary>
    internal static class Config
    {
        private const string Root = @"Software\DshRemoteMic";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>上一次写失败的原因，null = 上次写成功（或还没写过）。</summary>
        public static string LastWriteError { get; private set; }

        public static string GetString(string name, string fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Root))
                {
                    if (k == null) return fallback;                     // 还没存过配置
                    var v = k.GetValue(name) as string;
                    return string.IsNullOrEmpty(v) ? fallback : v;
                }
            }
            catch
            {
                return fallback;                                        // 读不到就用默认值，绝不抛
            }
        }

        public static void SetString(string name, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Root))
                {
                    if (k == null) { LastWriteError = @"无法创建 HKCU\" + Root; return; }
                    k.SetValue(name, value ?? string.Empty, RegistryValueKind.String);
                    LastWriteError = null;
                }
            }
            catch (Exception e)
            {
                LastWriteError = e.Message;
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
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null)
                    {
                        // 键不存在或没有写权限：自启动开关落不了地，但绝不能让程序倒在这里
                        if (LastWriteError == null) LastWriteError = @"打不开 HKCU\" + RunKey;
                        return;
                    }
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
            catch (Exception e)
            {
                LastWriteError = e.Message;
            }
        }
    }
}
