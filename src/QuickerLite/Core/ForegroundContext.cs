using System;
using System.Diagnostics;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 识别当前前台程序。上下文动作页（Scenes）就是靠这个决定该显示哪一页 ——
    /// 你在 Chrome 里唤出面板，就该看到网页相关的动作，而不是一堆没关系的快捷方式。
    /// </summary>
    public static class ForegroundContext
    {
        private static string? _ownProcessName;

        /// <summary>本程序自己的进程名（小写、不含 .exe）。</summary>
        public static string OwnProcessName =>
            _ownProcessName ??= SafeOwnName();

        /// <summary>
        /// 当前前台窗口所属进程的名字，形如 "chrome"、"explorer"。
        /// 取不到就返回 null —— 调用方要能接受这个结果，不能当成错误。
        /// </summary>
        public static string? CurrentProcessName()
        {
            try
            {
                var hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;

                if (NativeMethods.GetWindowProcessId(hwnd, out var pid) == 0 || pid == 0)
                    return null;

                // ProcessName 已经是小写且不含 .exe 的形式，正好可以直接当字典键用
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                // 前台程序刚好在这一瞬间退出了；或者对方是提权进程，读不到。
                // 这两种都不该影响面板打开，返回 null 让调用方退回到全局面板。
                return null;
            }
        }

        private static string SafeOwnName()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.ProcessName;
            }
            catch
            {
                return "quickerlite";
            }
        }

        /// <summary>两个进程名是否指同一个程序。</summary>
        public static bool SameProcess(string? a, string? b)
            => !string.IsNullOrEmpty(a)
               && !string.IsNullOrEmpty(b)
               && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
