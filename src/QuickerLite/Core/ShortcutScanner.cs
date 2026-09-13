using System;
using System.Collections.Generic;
using System.IO;

namespace QuickerLite.Core
{
    /// <summary>开始菜单 / 桌面里的一个快捷方式。</summary>
    public sealed class ShortcutEntry
    {
        /// <summary>.lnk / .url 的完整路径。直接拿它当启动目标即可 ——
        /// ShellLauncher 用 UseShellExecute=true，Shell 会自己解析快捷方式。</summary>
        public string Path { get; init; } = "";

        /// <summary>不带扩展名的显示名。</summary>
        public string Name { get; init; } = "";

        /// <summary>来源，例如「开始菜单（所有用户）」。</summary>
        public string Source { get; init; } = "";

        /// <summary>相对来源根目录的子目录，用于分组辨识，可为空。</summary>
        public string SubFolder { get; init; } = "";

        public string Detail => string.IsNullOrEmpty(SubFolder)
            ? Source
            : $"{Source} / {SubFolder}";
    }

    /// <summary>一个扫描根目录。</summary>
    public sealed class ScanRoot
    {
        public string Directory { get; init; } = "";
        public string Label { get; init; } = "";
    }

    /// <summary>
    /// 扫描开始菜单和桌面上的快捷方式，供批量导入用。
    ///
    /// 三个设计决定：
    ///   1. <b>不解析 .lnk 的真实目标。</b>解析要走 COM（IShellLink / WScript.Shell），
    ///      而目标路径其实用不上 —— 直接把 .lnk 路径当启动目标，Shell 自己会解析
    ///      （这正是 ShellLauncher 用 UseShellExecute=true 的原因）。
    ///      省掉 COM 依赖，也省掉一类「快捷方式指向的目标被删了」的边界情况。
    ///   2. <b>不在这里提图标。</b>调用方（导入窗口）刻意不显示图标：
    ///      200 个快捷方式逐个 SHGetFileInfo，只要有一个指向断开的网络共享，
    ///      就会拖住整个图标工作线程几十秒。导入列表用名字和来源目录辨识就够了。
    ///   3. <b>根目录在调用前解析好。</b>Known Folder API 有线程亲和性方面的讲究，
    ///      所以 <see cref="DefaultRoots"/> 由调用方在 UI 线程调一次，
    ///      再把这个结果丢进后台线程去扫 —— <see cref="Scan(IReadOnlyList{ScanRoot})"/>
    ///      本身是纯函数，不碰任何环境状态。
    /// </summary>
    public static class ShortcutScanner
    {
        private const int MaxDepth = 6;

        /// <summary>解析默认的扫描根目录。请在 UI 线程调用。</summary>
        public static List<ScanRoot> DefaultRoots()
        {
            var roots = new List<ScanRoot>();

            // 开始菜单的两个根：所有用户 + 当前用户。
            // Known Folder API 一般都能拿到，但精简环境下可能返回空串，所以给了硬编码兜底。
            var commonStart = SafeFolder(Environment.SpecialFolder.CommonStartMenu);
            if (string.IsNullOrEmpty(commonStart))
                commonStart = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";

            var userStart = SafeFolder(Environment.SpecialFolder.StartMenu);
            if (string.IsNullOrEmpty(userStart))
            {
                var appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                    userStart = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs");
            }

            Add(roots, commonStart, "开始菜单（所有用户）");
            Add(roots, userStart, "开始菜单（当前用户）");
            Add(roots, SafeFolder(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面");
            Add(roots, SafeFolder(Environment.SpecialFolder.DesktopDirectory), "桌面");

            return roots;
        }

        private static void Add(List<ScanRoot> roots, string directory, string label)
        {
            if (string.IsNullOrEmpty(directory)) return;
            if (!Directory.Exists(directory)) return;

            roots.Add(new ScanRoot { Directory = directory, Label = label });
        }

        public static List<ShortcutEntry> Scan() => Scan(DefaultRoots());

        public static List<ShortcutEntry> Scan(IReadOnlyList<ScanRoot> roots)
        {
            var results = new List<ShortcutEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 目录级别的去重集合，键是目录的「真实身份」（见 RealPathOf）
            var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);

            if (roots != null)
            {
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root.Directory)) continue;
                    if (!Directory.Exists(root.Directory)) continue;

                    Walk(root.Directory, root.Label, "", 0, results, seen, visitedDirectories);
                }
            }

            results.Sort((a, b) =>
            {
                var bySource = string.CompareOrdinal(a.Source, b.Source);
                if (bySource != 0) return bySource;

                var byFolder = string.Compare(a.SubFolder, b.SubFolder, StringComparison.CurrentCulture);
                if (byFolder != 0) return byFolder;

                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });

            return results;
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static void Walk(string directory, string source, string relative,
            int depth, List<ShortcutEntry> results, HashSet<string> seen,
            HashSet<string> visitedDirectories)
        {
            // 深度上限纯粹是防御性的：正常开始菜单不会超过三层，
            // 但目录联接（junction）造成的循环会直接把递归打爆。
            if (depth > MaxDepth) return;

            // 坑：中文版 Windows 的开始菜单里同时存在「程序」和「Programs」两个目录，
            // 前者是指向后者的目录联接。按路径字符串去重挡不住它 ——
            // 两个路径长得完全不一样，指向的却是同一批文件，
            // 结果是每个快捷方式都被扫出两遍，导入列表里全是重复项。
            // 必须按目录的「真实身份」去重。
            if (!visitedDirectories.Add(RealPathOf(directory))) return;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.lnk");
            }
            catch
            {
                // 权限不足、目录刚好被删掉 —— 跳过这一层，不要让整次扫描失败
                return;
            }

            foreach (var file in files)
            {
                if (!seen.Add(file)) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                results.Add(new ShortcutEntry
                {
                    Path = file,
                    Name = name,
                    Source = source,
                    SubFolder = relative,
                });
            }

            // .url 也一起收 —— 开始菜单里「网站」那类就是它，指向浏览器
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.url"))
                {
                    if (!seen.Add(file)) continue;

                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    results.Add(new ShortcutEntry
                    {
                        Path = file,
                        Name = name,
                        Source = source,
                        SubFolder = relative,
                    });
                }
            }
            catch
            {
                // 同上
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                return;
            }

            foreach (var sub in subdirectories)
            {
                var child = System.IO.Path.GetFileName(sub);
                if (string.IsNullOrEmpty(child)) continue;

                var childRelative = string.IsNullOrEmpty(relative) ? child : $"{relative}/{child}";
                Walk(sub, source, childRelative, depth + 1, results, seen, visitedDirectories);
            }
        }

        /// <summary>
        /// 目录的「真实身份」：目录联接（junction）/ 符号链接解析到最终目标，
        /// 普通目录就用自身路径。返回小写形式，可以直接当去重键用。
        /// </summary>
        private static string RealPathOf(string directory)
        {
            try
            {
                var info = new DirectoryInfo(directory);

                // ResolveLinkTarget 对「不是链接的普通目录」返回 null，这时用自身路径。
                // 两边的最终形态必须一致才能撞上 —— 联接解析出来的正好就是
                // 被指向目录的 FullName，所以能对上。
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                var path = target?.FullName ?? info.FullName;

                return path.TrimEnd('\\', '/').ToLowerInvariant();
            }
            catch
            {
                // 权限不足 / 路径过长 / 目录刚好被删 —— 退化成按原路径去重
                return directory.TrimEnd('\\', '/').ToLowerInvariant();
            }
        }
    }
}
