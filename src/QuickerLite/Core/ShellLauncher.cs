using System;
using System.Diagnostics;
using System.IO;
using QuickerLite.Models;

namespace QuickerLite.Core
{
    /// <summary>
    /// 启动目标。这里的核心只有一个属性：UseShellExecute = true。
    ///
    /// 坑 4：如果 UseShellExecute = false，Process.Start 会把 .lnk / .url 当成二进制文件
    /// 直接去执行，结果是报错或什么都没发生。必须是 true，让 Windows Shell 来做
    /// 文件关联解析。.lnk、.url、文件夹、文档、ms-settings: 这类协议 URI 全靠它。
    /// </summary>
    public static class ShellLauncher
    {
        public static bool Launch(ActionItem item, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(item.Target))
            {
                error = "目标路径为空";
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = item.Target,
                    UseShellExecute = true,   // ← 关键，别改
                };

                if (!string.IsNullOrWhiteSpace(item.Arguments))
                    psi.Arguments = item.Arguments;

                var workDir = item.WorkingDirectory;
                if (string.IsNullOrWhiteSpace(workDir) && item.Type != ItemType.Url)
                {
                    // 用目标所在目录当工作目录，行为才和从资源管理器双击一致。
                    // 很多程序依赖这一点来定位自己的资源文件。
                    try
                    {
                        var dir = Path.GetDirectoryName(item.Target);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            workDir = dir;
                    }
                    catch { /* 路径非法就跳过 */ }
                }

                if (!string.IsNullOrWhiteSpace(workDir))
                    psi.WorkingDirectory = workDir;

                if (item.RunAsAdmin)
                    psi.Verb = "runas";   // 触发 UAC 提权

                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = 用户在 UAC 弹窗上点了「否」
                error = "已取消管理员授权";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>在资源管理器中定位到目标文件。</summary>
        public static void RevealInExplorer(string target)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                    return;
                }

                if (File.Exists(target))
                {
                    // /select 会打开父目录并选中该文件
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                    return;
                }

                // 目标不存在（比如是命令或 URI）时退化为打开数据目录
                Process.Start(new ProcessStartInfo(ConfigStore.DataDirectory) { UseShellExecute = true });
            }
            catch { /* 打开失败不弹窗打扰用户 */ }
        }
    }
}
