using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace QuickerLite.Core
{
    /// <summary>开机自启：写 HKCU 的 Run 项，不需要管理员权限。</summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "QuickerLite";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key == null) return;

                if (enabled)
                {
                    var exe = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName
                              ?? "";
                    if (string.IsNullOrEmpty(exe)) return;

                    // 带引号，避免路径里有空格时被拆成多个参数
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // 注册表被策略锁定时静默失败
            }
        }
    }
}
