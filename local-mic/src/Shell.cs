using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace DshRemoteMic
{
    /// <summary>打开外部目标的小工具。托盘与主窗口共用，避免两处各写一遍。</summary>
    internal static class Shell
    {
        /// <summary>
        /// 配对交给 Windows 自己。
        ///
        /// 未打包的桌面应用主动调 PairAsync 可能抛 UnauthorizedAccessException，
        /// 还可能弹系统同意框；用户已明确要求与 Windows 自己的配对流程对齐，
        /// 所以这里只是把用户送到系统的蓝牙设置页。
        /// </summary>
        public static void OpenWindowsBluetooth()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Warn("打不开系统蓝牙设置：" + e.Message);
            }
        }

        /// <summary>在资源管理器里打开目录。调试日志的入口就靠它——不用让用户自己找。</summary>
        public static void OpenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Warn("打不开目录 " + dir + "：" + e.Message);
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Warn("打不开 " + url + "：" + e.Message);
            }
        }

        public static void Warn(string message)
        {
            MessageBox.Show(message, "local-mic", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
