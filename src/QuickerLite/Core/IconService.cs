using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 图标提取与缓存。这个类集中处理了三个 Windows 平台坑：
    ///
    /// 坑 1（UI 卡死）：SHGetFileInfo 遇到断开的网络共享 / 离线移动硬盘会同步等待超时，
    ///   最长能卡几十秒。所以所有提取都跑在独立的 STA 线程上，UI 线程永远不等。
    ///
    /// 坑 2（GDI 句柄泄漏）：SHGetFileInfo / IImageList.GetIcon 返回的 HICON 是非托管资源，
    ///   转换完必须 DestroyIcon。单进程 GDI 句柄上限约 1 万，一个面板来回刷新几百个图标就炸。
    ///
    /// 坑 3（重复提取）：按「路径 + 最后修改时间 + 文件大小」做磁盘缓存，
    ///   第二次启动直接读 PNG，不再碰 shell API。
    /// </summary>
    public sealed class IconService : IDisposable
    {
        private sealed class IconRequest
        {
            public string Path = "";
            public bool IsDirectory;
            public Action<ImageSource?> Callback = _ => { };
        }

        private readonly BlockingCollection<IconRequest> _queue = new();
        private readonly ConcurrentDictionary<string, ImageSource?> _memory = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _cacheDir;
        private readonly Thread _worker;
        private volatile bool _disposed;

        public IconService()
        {
            _cacheDir = Path.Combine(ConfigStore.DataDirectory, "icons");
            Directory.CreateDirectory(_cacheDir);

            // 必须是 STA：shell 的图标 API 依赖 COM，MTA 线程上会偶发失败
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "QuickerLite.IconWorker",
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        /// <summary>异步请求图标。callback 一定在 UI 线程上被调用（可能为 null 表示提取失败）。</summary>
        public void Request(string path, bool isDirectory, Action<ImageSource?> callback)
        {
            if (_disposed) return;

            if (string.IsNullOrWhiteSpace(path))
            {
                callback(null);
                return;
            }

            if (_memory.TryGetValue(path, out var cached))
            {
                callback(cached);
                return;
            }

            try
            {
                _queue.Add(new IconRequest { Path = path, IsDirectory = isDirectory, Callback = callback });
            }
            catch (InvalidOperationException)
            {
                // 队列已关闭（正在退出）
                callback(null);
            }
        }

        private void WorkerLoop()
        {
            NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_APARTMENTTHREADED);
            try
            {
                foreach (var req in _queue.GetConsumingEnumerable())
                {
                    ImageSource? img = null;
                    try
                    {
                        img = LoadOrExtract(req.Path, req.IsDirectory);
                    }
                    catch
                    {
                        // 单个图标失败不该中断整个工作线程
                    }

                    _memory[req.Path] = img;

                    var cb = req.Callback;
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.HasShutdownStarted)
                    {
                        try { dispatcher.BeginInvoke(new Action(() => cb(img))); }
                        catch { /* 退出过程中 dispatcher 可能已关闭 */ }
                    }
                }
            }
            finally
            {
                NativeMethods.CoUninitialize();
            }
        }

        // ─────────────────────────── 缓存 ───────────────────────────

        private ImageSource? LoadOrExtract(string path, bool isDirectory)
        {
            var cacheFile = Path.Combine(_cacheDir, CacheKey(path, isDirectory) + ".png");

            if (File.Exists(cacheFile))
            {
                var fromDisk = TryLoadPng(cacheFile);
                if (fromDisk != null) return fromDisk;
            }

            var extracted = Extract(path, isDirectory);
            if (extracted != null) TrySavePng(extracted, cacheFile);
            return extracted;
        }

        private static string CacheKey(string path, bool isDirectory)
        {
            var stamp = "";
            try
            {
                if (isDirectory)
                {
                    var di = new DirectoryInfo(path);
                    if (di.Exists) stamp = di.LastWriteTimeUtc.Ticks.ToString();
                }
                else
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) stamp = fi.LastWriteTimeUtc.Ticks + "_" + fi.Length;
                }
            }
            catch { /* 网络路径取元信息也可能失败，忽略，用路径本身做 key */ }

            var raw = (isDirectory ? "D|" : "F|") + path.ToLowerInvariant() + "|" + stamp;
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
        }

        private static ImageSource? TryLoadPng(string file)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();   // 冻结后才能从工作线程交给 UI 线程
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static void TrySavePng(ImageSource image, string file)
        {
            try
            {
                // 显式转成 BitmapSource：BitmapFrame.Create 有 Stream / Uri / ImageSource
                // 多个重载，不写清楚编译器会挑错。
                if (image is not BitmapSource bitmap) return;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var fs = File.Create(file);
                encoder.Save(fs);
            }
            catch { /* 缓存写失败无所谓，下次重新提取 */ }
        }

        // ─────────────────────────── 提取 ───────────────────────────

        private static ImageSource? Extract(string path, bool isDirectory)
        {
            IntPtr hIcon = IntPtr.Zero;

            // 第一步：非文件夹时，尝试从系统图像列表拿 48x48 的「特大图标」，
            // 比 SHGetFileInfo 默认的 32x32 清晰得多。
            if (!isDirectory)
            {
                hIcon = TryGetExtraLargeIcon(path);
            }

            // 第二步：回退到 SHGetFileInfo。
            // 文件夹一律加 SHGFI_USEFILEATTRIBUTES —— 直接返回通用文件夹图标，
            // 不去访问磁盘。这样即使目标在网络共享上也不会卡住工作线程。
            if (hIcon == IntPtr.Zero)
            {
                uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
                uint attr = 0;

                if (isDirectory)
                {
                    flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
                    attr = NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
                }
                else if (!File.Exists(path))
                {
                    // 文件不存在（比如 .exe 还没装）时也别去碰磁盘
                    flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
                    attr = NativeMethods.FILE_ATTRIBUTE_NORMAL;
                }

                var shfi = new SHFILEINFO();
                var result = NativeMethods.SHGetFileInfo(
                    path, attr, ref shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (result != IntPtr.Zero) hIcon = shfi.hIcon;
            }

            if (hIcon == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                // ← 这一行是坑 2 的解药，漏掉就泄漏 GDI 句柄
                NativeMethods.DestroyIcon(hIcon);
            }
        }

        private static IntPtr TryGetExtraLargeIcon(string path)
        {
            try
            {
                var shfi = new SHFILEINFO();
                var result = NativeMethods.SHGetFileInfo(
                    path, 0, ref shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    NativeMethods.SHGFI_SYSICONINDEX);

                if (result == IntPtr.Zero) return IntPtr.Zero;

                var iid = NativeMethods.IID_IImageList;
                NativeMethods.SHGetImageList(NativeMethods.SHIL_EXTRALARGE, ref iid, out var list);
                if (list == null) return IntPtr.Zero;

                try
                {
                    if (list.GetIcon(shfi.iIcon, NativeMethods.ILD_TRANSPARENT, out var hIcon) == 0)
                        return hIcon;
                }
                finally
                {
                    Marshal.ReleaseComObject(list);
                }

                return IntPtr.Zero;
            }
            catch
            {
                // 系统不支持特大图像列表（或 COM 出错）时安静回退
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _queue.CompleteAdding(); } catch { }
            try { _worker.Join(1500); } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
