using System;
using System.Runtime.InteropServices;

namespace DshRemoteMic
{
    /// <summary>
    /// 进程完整性级别（Low / Medium / High …）。
    ///
    /// 为什么需要它：**低完整性进程注册不了托盘图标**（实测 <c>Shell_NotifyIcon(NIM_ADD)</c>
    /// 返回 false、<c>GetLastError()=5</c> 拒绝访问）。于是"双击后什么都没有"在 Low 下是必然的：
    /// 图标注册不上，而主窗口又只能从托盘打开 —— 进程活着、WS 在听，用户却看不到也关不掉。
    /// 这时必须直接开主窗口，并让关窗即退出（见 <c>TrayApp.RunWithoutTray</c>）。
    ///
    /// ⚠ 进程 IL = min(用户 IL, **exe 文件自身的强制完整性标签**)。2026-10-01 真机踩过：
    /// 仓库目录被打上 Low 标签（`icacls` 可见 `Mandatory Label\Low Mandatory Level`），
    /// 从那里编译出的 exe 继承该标签 ⇒ 无论怎么启动都是 Low。修复：
    /// <c>icacls "&lt;目录&gt;" /setintegritylevel (OI)(CI)M /T</c> —— 见 docs/PITFALLS.md #26。
    /// </summary>
    internal static class Integrity
    {
        private const int TokenIntegrityLevel = 25;      // TOKEN_INFORMATION_CLASS
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;

        // 完整性 RID
        private const int LowRid = 0x1000;
        private const int MediumRid = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int retLen);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>当前进程的完整性 RID（Low=0x1000 / Medium=0x2000 / High=0x3000）。查不到返回 -1。</summary>
        public static int CurrentRid
        {
            get
            {
                IntPtr p = IntPtr.Zero, tok = IntPtr.Zero, buf = IntPtr.Zero;
                try
                {
                    p = OpenProcess(ProcessQueryLimitedInformation, false,
                        System.Diagnostics.Process.GetCurrentProcess().Id);
                    if (p == IntPtr.Zero) return -1;

                    if (!OpenProcessToken(p, TokenQuery, out tok)) return -1;

                    buf = Marshal.AllocHGlobal(1024);
                    int ret;
                    if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, 1024, out ret)) return -1;

                    // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    if (sid == IntPtr.Zero) return -1;

                    byte subCount = Marshal.ReadByte(sid, 1);           // SID: Revision(1) Count(1) Authority(6) SubAuthority[]
                    if (subCount < 1) return -1;
                    return Marshal.ReadInt32(sid, 8 + 4 * (subCount - 1));   // 最后一个子授权 = 完整性 RID
                }
                catch { return -1; }
                finally
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    if (tok != IntPtr.Zero) CloseHandle(tok);
                    if (p != IntPtr.Zero) CloseHandle(p);
                }
            }
        }

        /// <summary>
        /// 低于 Medium（Low / Untrusted）⇒ Windows 拒绝注册托盘图标，必须改为直接把主窗口开出来。
        /// 查不到级别（-1）时按"图标可用"处理，避免误判。
        /// </summary>
        public static bool TrayIconUnavailable
        {
            get
            {
                int rid = CurrentRid;
                return rid > 0 && rid < MediumRid;              // Low(0x1000) / Untrusted(0x0000)
            }
        }

        /// <summary>给日志/提示用的可读名字。</summary>
        public static string CurrentName
        {
            get
            {
                int rid = CurrentRid;
                if (rid < 0) return "未知";
                if (rid >= 0x4000) return "System";
                if (rid >= 0x3000) return "High";
                if (rid >= MediumRid) return "Medium";
                if (rid >= LowRid) return "Low";
                return "Untrusted";
            }
        }
    }
}
