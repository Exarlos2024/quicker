using System;
using System.IO;
using System.Windows;
using QuickerLite.Core;
using QuickerLite.UI;

namespace QuickerLite
{
    /// <summary>
    /// 应用入口。整个程序是「托盘常驻 + 面板按需弹出」的形态，
    /// 所以 ShutdownMode 必须是 OnExplicitShutdown —— 否则面板一 Hide
    /// 主窗口就算关闭，进程直接退出。
    /// </summary>
    public partial class App : Application
    {
        private SingleInstance? _singleInstance;
        private ConfigStore? _config;
        private IconService? _icons;
        private HotkeyService? _hotkey;
        private InputHookService? _hooks;
        private TrayIconService? _tray;
        private PanelWindow? _panel;

        public App()
        {
            // 这是个没有控制台窗口的 GUI 程序，崩溃时什么都不会显示。
            // 把异常落到文件里，否则出问题只能靠猜。
            DispatcherUnhandledException += (_, args) =>
            {
                WriteCrashLog(args.Exception);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                WriteCrashLog(args.ExceptionObject as Exception);
            };
        }

        internal static void WriteCrashLog(Exception? exception)
        {
            if (exception == null) return;
            Log($"崩溃：{exception}");
        }

        /// <summary>
        /// 轻量诊断日志。这是个无窗口的常驻程序，出了问题没有控制台可看，
        /// 只能靠日志判断「到底启动到哪一步了」「热键注册成功没有」。
        /// </summary>
        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.DataDirectory);
                var path = Path.Combine(ConfigStore.DataDirectory, "quickerlite.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 连日志都写不了就只能放弃了
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                StartUp();
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                MessageBox.Show(
                    "QuickerLite 启动失败：\n\n" + ex.Message +
                    "\n\n详细信息已写入：\n" + Path.Combine(ConfigStore.DataDirectory, "quickerlite.log"),
                    "QuickerLite", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void StartUp()
        {
            // ── 单实例：抢不到就把面板唤出来，然后自己退出 ──
            _singleInstance = new SingleInstance();
            if (!_singleInstance.TryAcquire())
            {
                SingleInstance.NotifyFirstInstance(SingleInstance.ShowPanelMessage);
                Shutdown();
                return;
            }

            _singleInstance.MessageReceived += message =>
            {
                if (message == SingleInstance.ShowPanelMessage)
                    Dispatcher.BeginInvoke(new Action(() => _panel?.ShowPanel()));
            };

            // ── 配置 ──
            _config = new ConfigStore();
            _config.Load();

            // ── 图标服务（独立 STA 线程，UI 永不被阻塞） ──
            _icons = new IconService();

            // ── 面板窗口（构造时就建好 HWND，但不显示） ──
            _panel = new PanelWindow(_config, _icons);
            // 用 _tray?. 是因为托盘在下面才建好，而这个 lambda 只在用户操作时才跑
            _panel.Notify = (title, text) => _tray?.ShowBalloon(title, text);
            _panel.HotkeySettingsChanged += RegisterHotkey;
            _panel.InputSettingsChanged += ApplyHookSettings;

            // ── 全局热键 ──
            _hotkey = new HotkeyService();
            _hotkey.Pressed += () => Dispatcher.BeginInvoke(new Action(() => _panel.Toggle()));
            RegisterHotkey();

            // ── 低级输入钩子：单击 Ctrl / 鼠标中键唤出 ──
            // 必须在 UI 线程构造，钩子回调才会回到 UI 线程
            _hooks = new InputHookService();

            // 钩子内部已经把事件 Post 到消息队列末尾了，这里可以直接调 Toggle：
            // 回调本身必须立刻返回，否则超过 LowLevelHooksTimeout 系统会静默摘掉钩子
            _hooks.SingleCtrlPressed += () => _panel.Toggle();
            _hooks.MiddleClickPressed += () => _panel.Toggle();

            // ── 托盘 ──
            _tray = new TrayIconService();
            _tray.OpenPanelRequested += () => Dispatcher.BeginInvoke(new Action(() => _panel.ShowPanel()));
            _tray.SettingsRequested += () => Dispatcher.BeginInvoke(new Action(() => _panel.OpenSettings()));
            _tray.ExitRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));

            _tray.SetTooltip($"QuickerLite — {_config.Current.Hotkey.Display} 唤出面板");

            // 钩子必须等托盘建好之后再应用：安装失败只能靠托盘气泡告知用户，
            // 否则这个失败是完全静默的，用户只会觉得「按了没反应」。
            ApplyHookSettings();

            Log($"启动完成：热键={_config.Current.Hotkey.Display}，" +
                $"注册={_hotkey.IsRegistered}，" +
                $"动作页={_config.Current.GlobalPages.Count} 全局 + {_config.Current.Scenes.Count} 场景，" +
                $"条目={ConfigStore.CountItems(_config.Current)}，" +
                $"列数={_config.Current.Columns}，" +
                $"单击Ctrl={_config.Current.EnableSingleCtrl}(键盘钩子={_hooks.KeyboardHookInstalled})，" +
                $"中键={_config.Current.EnableMiddleClick}(鼠标钩子={_hooks.MouseHookInstalled})");

            if (_hooks.LastError != null)
                Log($"输入钩子异常：{_hooks.LastError}");

            // 前台进程探测是上下文动作页的基础。它失败时不会报任何错，
            // 只会「场景永远不生效」—— 所以启动时留一行，方便排查。
            var foreground = ForegroundContext.CurrentProcessName();
            var resolved = PageResolver.Resolve(_config.Current, foreground);

            Log($"前台程序探测：{foreground ?? "(读不到)"}" +
                $"，本进程={ForegroundContext.OwnProcessName}" +
                $"，解析={resolved.Pages.Count} 页" +
                (resolved.HasScene
                    ? $"，命中场景「{resolved.SceneProcess}」(场景页 {resolved.ScenePageCount} 个)"
                    : "，无场景命中，只用全局面板"));

            // 首次运行给个提示，让用户知道怎么唤出
            if (!_config.Current.StartWithWindows && ConfigStore.CountItems(_config.Current) > 0)
            {
                _tray.ShowBalloon(
                    "QuickerLite 已在后台运行",
                    $"按 {_config.Current.Hotkey.Display} 或单击 Ctrl 唤出面板，滚轮翻页，双击托盘图标也可以。");
            }
        }

        /// <summary>
        /// 按配置安装/卸载输入钩子。
        /// 两个开关都关掉时会主动卸载 —— 没必要的全局钩子只会白白招杀软。
        /// </summary>
        private void ApplyHookSettings()
        {
            if (_hooks == null || _config == null) return;

            var cfg = _config.Current;

            _hooks.EnableSingleCtrl = cfg.EnableSingleCtrl;
            _hooks.EnableMiddleClick = cfg.EnableMiddleClick;
            _hooks.SingleCtrlTimeoutMs = cfg.SingleCtrlTimeoutMs;

            _hooks.Apply(cfg.EnableSingleCtrl, cfg.EnableMiddleClick);

            if (_hooks.LastError != null)
            {
                // 钩子装不上是完全静默的：用户只会觉得「按了没反应」。
                // 必须主动告知，并给出可操作的下一步（去设置里关掉、或换回热键）。
                Log($"输入钩子异常：{_hooks.LastError}");
                _tray?.ShowBalloon(
                    "唤出方式未能生效",
                    $"{_hooks.LastError}\n\n可以先继续用 {cfg.Hotkey.Display}，或在设置里关掉这一项。");
            }
            else if (!cfg.EnableSingleCtrl && !cfg.EnableMiddleClick)
            {
                Log("单击 Ctrl / 中键 均已关闭，输入钩子已卸载");
            }
        }

        /// <summary>
        /// 注册热键，失败时明确告诉用户原因。
        /// 坑 5：RegisterHotKey 的失败是静默的，如果不主动提示，
        /// 用户会以为程序坏了。
        /// </summary>
        private void RegisterHotkey()
        {
            if (_hotkey == null || _config == null) return;

            var hotkey = _config.Current.Hotkey;
            var ok = _hotkey.Register(hotkey.Modifiers, hotkey.VirtualKey);

            if (ok)
            {
                _tray?.SetTooltip($"QuickerLite — {hotkey.Display} 唤出面板");
            }
            else
            {
                _tray?.ShowBalloon("快捷键注册失败", _hotkey.LastError ?? "未知原因，请在设置里换一个组合键");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("退出");

            _hotkey?.Dispose();
            _hooks?.Dispose();
            _tray?.Dispose();
            _icons?.Dispose();
            _singleInstance?.Dispose();

            base.OnExit(e);
        }
    }
}
