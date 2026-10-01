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

        /// <summary>
        /// 在资源管理器里打开目录。返回 false = 没能打开（由调用方决定怎么提示）。
        /// 调试日志收尾的入口就靠它——不用让用户自己找。
        /// </summary>
        public static bool OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

            // ⚠ 低完整性进程拉不起 explorer.exe：子进程继承 Low IL，而 explorer 在 Low IL 下
            // 初始化直接失败，弹「explorer.exe - 应用程序错误 0xc0000142」。
            // Process.Start 本身是成功的 —— 崩的是子进程，try/catch 拦不到，
            // 用户只会看到一个莫名其妙的系统错误框。检测到低完整性就直接不拉，由调用方显示路径。
            if (Integrity.TrayIconUnavailable) return false;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                return true;
            }
            catch (Exception e)
            {
                Warn("打不开目录 " + dir + "：" + e.Message);
                return false;
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
