using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 托盘图标与菜单。
    ///
    /// 用 WinForms 的 NotifyIcon 而不是 Hardcodet.NotifyIcon.Wpf：
    /// 后者要引 NuGet 包，而 WinForms 的 NotifyIcon 是框架自带的，够用且零依赖。
    /// 项目里同时开了 UseWPF 和 UseWindowsForms 就能这么干。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
        private readonly Icon _ownedIcon;
        private readonly System.Windows.Forms.ContextMenuStrip _menu;

        public event Action? OpenPanelRequested;
        public event Action? SettingsRequested;
        public event Action? ExitRequested;

        public TrayIconService()
        {
            _ownedIcon = TrayIconFactory.Create();

            _menu = new System.Windows.Forms.ContextMenuStrip();
            _menu.Items.Add("打开面板", null, (_, _) => OpenPanelRequested?.Invoke());
            _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _menu.Items.Add("设置...", null, (_, _) => SettingsRequested?.Invoke());
            _menu.Items.Add("打开数据目录", null, (_, _) => OpenDataFolder());
            _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _ownedIcon,
                Text = "QuickerLite — 快捷启动面板",
                Visible = true,
                ContextMenuStrip = _menu,
            };

            _notifyIcon.DoubleClick += (_, _) => OpenPanelRequested?.Invoke();
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(5000);
            }
            catch { /* 通知被系统禁用时忽略 */ }
        }

        public void SetTooltip(string text)
        {
            try
            {
                // NotifyIcon.Text 上限 63 个字符，超了会抛异常
                _notifyIcon.Text = text.Length > 62 ? text.Substring(0, 62) : text;
            }
            catch { }
        }

        private static void OpenDataFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConfigStore.DataDirectory)
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }

            _menu.Dispose();
            _ownedIcon.Dispose();
        }
    }

    /// <summary>程序内绘制托盘图标，省掉一个 .ico 资源文件。</summary>
    internal static class TrayIconFactory
    {
        public static Icon Create()
        {
            using var bitmap = new Bitmap(32, 32);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var background = new SolidBrush(Color.FromArgb(47, 111, 235));
                g.FillEllipse(background, 1, 1, 30, 30);

                using var pen = new Pen(Color.White, 3f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };

                // 一个闪电，暗示「快速启动」
                g.DrawLines(pen, new[]
                {
                    new PointF(18.5f, 6f),
                    new PointF(11.5f, 17f),
                    new PointF(16f, 17f),
                    new PointF(14f, 26f),
                    new PointF(21.5f, 14.5f),
                    new PointF(16.5f, 14.5f),
                    new PointF(18.5f, 6f),
                });
            }

            var handle = bitmap.GetHicon();
            try
            {
                // Icon.FromHandle 不接管句柄所有权，克隆出一个独立实例再销毁原句柄，
                // 否则这里就是一个 GDI 泄漏点。
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }
}
