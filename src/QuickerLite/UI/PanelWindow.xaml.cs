using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickerLite.Core;
using QuickerLite.Interop;
using QuickerLite.Models;

namespace QuickerLite.UI
{
    public partial class PanelWindow : Window
    {
        /// <summary>左右各 12 的 Margin + 1px 边框 ×2。</summary>
        private const int HorizontalPadding = 26;

        private readonly ConfigStore _config;
        private readonly IconService _icons;
        private readonly ObservableCollection<ItemViewModel> _view = new();

        /// <summary>当前页的条目。</summary>
        private readonly List<ItemViewModel> _pageItems = new();

        /// <summary>所有已解析页的条目（用于跨页搜索）。</summary>
        private readonly List<ItemViewModel> _searchItems = new();

        // ── 动作页状态 ──
        private List<ActionPage> _pages = new();
        private int _pageIndex;

        /// <summary>本次解析命中的场景进程名，没命中为 null。</summary>
        private string? _sceneProcess;

        /// <summary>场景页的数量（排在 _pages 最前面）。用于在页名后标注所属场景。</summary>
        private int _scenePageCount;

        /// <summary>
        /// 上一次识别到的「外部前台程序」。
        /// 从托盘菜单打开面板时前台是任务栏，从设置窗口返回时前台是我们自己 ——
        /// 这两种情况都不该把上下文场景丢掉，所以记住上一次的结果。
        /// </summary>
        private string? _lastExternalProcess;

        /// <summary>
        /// 唤出面板之前的那个前台窗口。
        /// 文本片段 / 按键这两类格子要把内容打回「用户原来在用的窗口」，
        /// 只记进程名不够 —— 同名程序可能有多个窗口，必须精确到句柄。
        /// </summary>
        private IntPtr _externalHwnd;

        /// <summary>
        /// 往外发通知（目前是托盘气泡）。由 App 接线。
        ///
        /// 为什么需要它：复制文本之后面板会隐藏，状态栏用户看不见，
        /// 而「复制成功」这件事没有任何其他反馈渠道 —— 不做提示的话，
        /// 用户只能靠粘贴出来才知道有没有成功。
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        private bool _isShown;
        private bool _initialized;

        /// <summary>
        /// 键盘焦点光标在 _view 里的下标。-1 表示没有（网格为空）。
        /// 这是「方向键选格子」的状态，跟 WPF 的键盘焦点无关 —— 焦点必须留在搜索框里。
        /// </summary>
        private int _focusIndex = -1;

        /// <summary>几何自检只做一次。</summary>
        private bool _geometryChecked;

        /// <summary>列数。改列数会同时改面板宽度，所以统一从这里取。</summary>
        private int Columns => Math.Clamp(_config.Current.Columns, 1, 12);

        /// <summary>
        /// 系统是否允许动画。关掉「窗口动画」或处于高对比度模式时，所有过渡降级为直接切换。
        ///
        /// 这个面板本来就用颜色和形状表达状态、不靠运动，所以降级几乎没有信息损失 ——
        /// 唯一用到动画的地方是显示时那段淡入，它的作用只是盖住定位校正的跳动。
        /// </summary>
        private static readonly bool AnimationsEnabled =
            SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

        /// <summary>
        /// 「失焦自动隐藏」的抑制计数。打开右键菜单 / 弹对话框时会临时失活，
        /// 如果不抑制，点一下自己的菜单面板就没了 —— 这是这类工具最经典的 bug。
        /// </summary>
        private int _suppressDeactivate;

        /// <summary>设置窗口改了热键后通知宿主重新注册。</summary>
        public event Action? HotkeySettingsChanged;

        /// <summary>唤出方式（单击 Ctrl / 中键 / 判定时间窗）变了，宿主需要重新配置输入钩子。</summary>
        public event Action? InputSettingsChanged;

        public PanelWindow(ConfigStore config, IconService icons)
        {
            _config = config;
            _icons = icons;

            InitializeComponent();

            ItemHost.ItemsSource = _view;

            ApplyColumns();
            BuildBackgroundMenu();

            // 提前建出 HWND，好在第一次显示前就把圆角与投影设好
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            ApplyWindowChrome(hwnd);

            ResolvePages();
            ReloadItems();
            ApplyFilter("");
            UpdatePageChrome();

            _initialized = true;
            UpdateStatus();
        }

        // ══════════════════════ 显示 / 隐藏 ══════════════════════

        public void Toggle()
        {
            if (_isShown && IsVisible) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel()
        {
            SearchBox.Text = "";

            // 记下唤出面板之前的前台窗口。必须在 Show() 之前取 ——
            // 一旦面板显示出来，前台就是我们自己了。
            var foreground = NativeMethods.GetForegroundWindow();
            var own = new WindowInteropHelper(this).Handle;
            if (foreground != IntPtr.Zero && foreground != own)
                _externalHwnd = foreground;

            // 每次打开都重新解析上下文 —— 这正是「在 Chrome 里打开就显示网页动作」的实现点
            ResolvePages();
            ReloadItems();
            ApplyFilter("");
            UpdatePageChrome();

            // 先按估算位置摆好，减少显示瞬间的跳动。
            // 混合 DPI 多屏下估算可能不准，紧接着的 SetWindowPos 会用物理像素校正。
            PrePosition();

            // 位置是「先估算、再校正」的，两帧之间会有一次微小跳动。
            // 开启系统动画时用一段短淡入把它盖掉；关掉动画或高对比度模式时直接显示。
            Opacity = AnimationsEnabled ? 0 : 1;

            Show();
            UpdateLayout();

            PositionAtCursor();
            ForceForeground();

            _isShown = true;

            if (AnimationsEnabled)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                    TimeSpan.FromMilliseconds(Metrics.MotionPanelMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }), DispatcherPriority.Input);

            UpdateStatus();
            CheckGeometryOnce();
        }

        /// <summary>
        /// 几何自检，每个进程只做一次。
        ///
        /// Metrics 里的 HeaderHeight / FooterHeight 是按 XAML 的 Margin 手算出来的常量，
        /// 改了 XAML 却忘了同步，面板就会静默地多出或少掉几个像素 ——
        /// 这种错位肉眼很难发现，但会让「固定几何」这条设计原则悄悄失效。
        /// 一行日志换一个长期保险。
        /// </summary>
        private void CheckGeometryOnce()
        {
            if (_geometryChecked) return;
            _geometryChecked = true;

            App.Log($"面板几何：期望 {Width:0}×{Height:0}，实际 {ActualWidth:0}×{ActualHeight:0}，" +
                    $"列={Columns}，条目={_view.Count}，行数={Metrics.VisibleRows}");
        }

        public void HidePanel()
        {
            _isShown = false;
            DragOverlay.Visibility = Visibility.Collapsed;

            // 淡入被打断时 Opacity 可能停在中间值，这里必须显式复位，
            // 否则下次显示会从一个半透明的状态开始
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;

            SearchBox.Text = "";
            ItemScroller.ScrollToTop();
            Hide();
        }

        // ══════════════════════ 动作页 ══════════════════════

        private ActionPage? CurrentPage =>
            _pageIndex >= 0 && _pageIndex < _pages.Count ? _pages[_pageIndex] : null;

        /// <summary>识别前台程序，解析出这次要展示的动作页列表。</summary>
        private void ResolvePages()
        {
            var process = ForegroundContext.CurrentProcessName();

            if (string.IsNullOrEmpty(process) ||
                ForegroundContext.SameProcess(process, ForegroundContext.OwnProcessName))
            {
                // 前台是我们自己（托盘/设置窗口）或读不到 → 沿用上一次的外部程序
                process = _lastExternalProcess;
            }
            else
            {
                _lastExternalProcess = process;
            }

            var resolved = PageResolver.Resolve(_config.Current, process);

            _pages = resolved.Pages;
            _sceneProcess = resolved.SceneProcess;
            _scenePageCount = resolved.ScenePageCount;
            _pageIndex = resolved.StartIndex;

            // 兜底：配置被改坏时也保证面板能显示
            if (_pages.Count == 0)
            {
                _pages = new List<ActionPage> { new() { Name = "默认" } };
                _sceneProcess = null;
                _scenePageCount = 0;
            }
        }

        private void NextPage() => GoToPage(_pageIndex + 1);

        private void PrevPage() => GoToPage(_pageIndex - 1);

        private void GoToPage(int index)
        {
            if (_pages.Count == 0) return;

            // 翻到头就绕回去。页数一般不多，循环比「到头卡住」更顺手。
            var target = ((index % _pages.Count) + _pages.Count) % _pages.Count;
            if (target == _pageIndex) return;

            _pageIndex = target;

            ItemScroller.ScrollToTop();
            ReloadItems();
            ApplyFilter(SearchBox.Text);
            UpdatePageChrome();
        }

        /// <summary>
        /// 刷新腰栏上的页签。
        ///
        /// 页签和拖拽时的「移到哪一页」放置区共用 ApplyTabVisual 这一套外观，
        /// 所以平时看到的页签和拖拽时拖上去的目标长得一模一样 —— 不需要重新学习。
        /// 之前是 8px 圆点加一个当前页名，既看不出有几页，也说不出每页叫什么。
        /// </summary>
        private void UpdatePageChrome()
        {
            PageTabs.Children.Clear();
            _pageTabs.Clear();

            // 只有一页时整块隐藏，免得一个孤零零的页签白占位置
            if (_pages.Count <= 1)
            {
                PageNav.Visibility = Visibility.Collapsed;
                return;
            }

            PageNav.Visibility = Visibility.Visible;

            for (var i = 0; i < _pages.Count; i++)
            {
                var index = i;

                var tab = new Border
                {
                    CornerRadius = new CornerRadius(Metrics.TabRadius),
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    Tag = index,
                    ToolTip = DescribePage(i),
                    Child = new TextBlock
                    {
                        Text = _pages[i].Name,
                        FontSize = Metrics.FontLabel,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 110,
                    },
                };

                ApplyTabVisual(tab, isCurrent: i == _pageIndex, isHot: false);

                // 左键跳页；右键直接开动作页菜单（新建 / 重命名 / 删除都在里面），
                // 原来那个「点页名弹菜单」的入口就由右键接管了
                tab.MouseLeftButtonUp += OnPageTabClick;
                tab.MouseRightButtonUp += OnPageTabContextMenu;

                _pageTabs.Add(tab);
                PageTabs.Children.Add(tab);
            }
        }

        private string DescribePage(int index)
        {
            var header = _pages[index].Name;
            if (_sceneProcess != null && index < _scenePageCount)
                header += $"（{_sceneProcess} 场景）";

            return index == _pageIndex ? $"当前页：{header}" : $"切换到{header}";
        }

        /// <summary>
        /// 页签的三种外观。边框宽度恒为 1.5px（未命中时透明），
        /// 这样拖拽高亮切换不会引起 1.5px 的布局抖动。
        /// </summary>
        private static void ApplyTabVisual(Border tab, bool isCurrent, bool isHot)
        {
            tab.Background = isCurrent || isHot ? TabBgActive : Brushes.Transparent;
            tab.BorderBrush = isHot ? TabStrokeHot : Brushes.Transparent;
            tab.BorderThickness = new Thickness(1.5);

            if (tab.Child is not TextBlock text) return;

            text.Foreground = isCurrent || isHot ? TabTextActive : TextSecondaryBrush;
            text.FontWeight = isCurrent || isHot ? FontWeights.Medium : FontWeights.Normal;
        }

        private void OnPageTabClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: int index }) GoToPage(index);
            e.Handled = true;
        }

        private void OnPageTabContextMenu(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || _pages.Count == 0) return;

            var menu = new ContextMenu { PlacementTarget = element };
            menu.SetResourceReference(FrameworkElement.StyleProperty, "PanelContextMenuStyle");
            menu.Opened += OnContextMenuOpened;
            menu.Closed += OnContextMenuClosed;

            PopulatePageMenu(menu.Items);

            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// 填充动作页菜单。抽出来是因为有两个入口：腰栏的页名按钮，
        /// 以及面板空白处右键菜单里的「动作页」子菜单。
        /// </summary>
        private void PopulatePageMenu(ItemCollection items)
        {
            items.Clear();

            // ── ① 跳到某一页 ──
            for (var i = 0; i < _pages.Count; i++)
            {
                var index = i;
                var header = _pages[i].Name;

                // 场景页加个后缀，免得场景里有个同名页时看不出来
                if (_sceneProcess != null && i < _scenePageCount)
                    header += $"（{_sceneProcess}）";

                var entry = new MenuItem
                {
                    Header = header,
                    IsCheckable = true,
                    IsChecked = i == _pageIndex,
                };

                entry.Click += (_, _) => GoToPage(index);
                items.Add(entry);
            }

            items.Add(new Separator());

            // ── ② 新建页 ──
            var newGlobal = new MenuItem { Header = "新建全局面板页…" };
            newGlobal.Click += (_, _) => CreatePage(sceneKey: null);
            items.Add(newGlobal);

            // 场景的键是前台进程名，取不到就只给全局面板页
            var foreground = _lastExternalProcess;
            if (!string.IsNullOrEmpty(foreground))
            {
                var newScene = new MenuItem { Header = $"为「{foreground}」新建场景页…" };
                newScene.Click += (_, _) => CreatePage(foreground);
                items.Add(newScene);
            }

            // ── ③ 管理当前页 ──
            var page = CurrentPage;
            if (page == null) return;

            items.Add(new Separator());

            var rename = new MenuItem { Header = $"重命名「{page.Name}」…" };
            rename.Click += (_, _) => RenameCurrentPage();
            items.Add(rename);

            // 归属切换：全局 ↔ 场景。这两个方向互斥，同时只显示一个。
            var sceneOfPage = ConfigStore.FindSceneOf(_config.Current, page);

            if (sceneOfPage != null)
            {
                var moveOut = new MenuItem { Header = $"从「{sceneOfPage}」场景移出，放回全局面板" };
                moveOut.Click += (_, _) => MoveCurrentPageTo(null);
                items.Add(moveOut);
            }
            else if (!string.IsNullOrEmpty(foreground))
            {
                var moveIn = new MenuItem { Header = $"把这一页移到「{foreground}」场景" };
                moveIn.Click += (_, _) => MoveCurrentPageTo(foreground);
                items.Add(moveIn);
            }

            var remove = new MenuItem
            {
                Header = $"删除「{page.Name}」",
                // 至少留一页，否则面板会没有落脚点
                IsEnabled = ConfigStore.CountPages(_config.Current) > 1,
                ToolTip = ConfigStore.CountPages(_config.Current) > 1
                    ? null
                    : "至少要保留一个动作页",
            };
            remove.Click += (_, _) => DeleteCurrentPage();
            items.Add(remove);
        }

        // ══════════════════════ 动作页管理 ══════════════════════

        /// <summary>新建一页。sceneKey 为 null 表示加到全局面板区。</summary>
        private void CreatePage(string? sceneKey)
        {
            WithSuppressedDeactivate(() =>
            {
                var where = sceneKey == null ? "全局面板" : $"「{sceneKey}」场景";
                var name = InputDialog.Show(this, "新建动作页",
                    $"给新动作页起个名字（会加到{where}）：", "新动作页");

                if (string.IsNullOrWhiteSpace(name)) return;

                var page = new ActionPage { Name = name.Trim() };
                ConfigStore.MovePage(_config.Current, page, sceneKey);
                _config.Save();

                ReloadAfterPageChange(page, $"已新建动作页「{page.Name}」");
            });
        }

        private void RenameCurrentPage()
        {
            var page = CurrentPage;
            if (page == null) return;

            WithSuppressedDeactivate(() =>
            {
                var name = InputDialog.Show(this, "重命名动作页", "新的页名：", page.Name);
                if (string.IsNullOrWhiteSpace(name)) return;

                page.Name = name.Trim();
                _config.Save();

                UpdatePageChrome();
                UpdateStatus();
            });
        }

        /// <summary>把当前页移出/移入场景。sceneKey 为 null 表示移回全局面板。</summary>
        private void MoveCurrentPageTo(string? sceneKey)
        {
            var page = CurrentPage;
            if (page == null) return;

            ConfigStore.MovePage(_config.Current, page, sceneKey);
            _config.Save();

            ReloadAfterPageChange(page, sceneKey == null
                ? "已移回全局面板区"
                : $"已移到「{sceneKey}」场景");
        }

        private void DeleteCurrentPage()
        {
            var page = CurrentPage;
            if (page == null) return;

            if (ConfigStore.CountPages(_config.Current) <= 1)
            {
                SetStatus("至少要保留一个动作页");
                return;
            }

            WithSuppressedDeactivate(() =>
            {
                var detail = page.Items.Count == 0
                    ? "这一页是空的。"
                    : $"页里的 {page.Items.Count} 个条目会一起删掉。";

                var answer = MessageBox.Show(
                    this,
                    $"确定要删除动作页「{page.Name}」吗？\n\n{detail}\n（不会删除任何原文件）",
                    "QuickerLite",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (answer != MessageBoxResult.OK) return;

                ConfigStore.Detach(_config.Current, page);
                _config.Save();

                ReloadAfterPageChange(null, $"已删除动作页「{page.Name}」");
            });
        }

        /// <summary>
        /// 页面结构改动后重新解析并刷新。传入 preferred 时会尽量切到那一页 ——
        /// 新建或移动完页面之后停在原地，用户会以为操作没生效。
        /// </summary>
        private void ReloadAfterPageChange(ActionPage? preferred, string status)
        {
            ResolvePages();

            if (preferred != null)
            {
                var index = _pages.IndexOf(preferred);
                _pageIndex = index >= 0 ? index : 0;
            }
            else
            {
                _pageIndex = 0;
            }

            SearchBox.Text = "";
            ItemScroller.ScrollToTop();
            ReloadItems();
            ApplyFilter("");
            UpdatePageChrome();
            SetStatus(status);
        }

        /// <summary>
        /// 滚轮翻页。
        /// 关键细节：内容超出可视区时，滚轮必须先用来滚动内容，滚到边界才翻页。
        /// 一上来就翻页的话，条目多的页永远看不到底部。
        /// </summary>
        private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_pages.Count <= 1) return;   // 只有一页，滚轮交给 ScrollViewer

            // 搜索状态下网格里装的是跨页的搜索结果，「翻页」没有意义，
            // 滚轮应该老老实实用来滚动结果列表。
            if (!string.IsNullOrEmpty(SearchBox.Text)) return;

            var scrollingDown = e.Delta < 0;

            var canScroll = scrollingDown
                ? ItemScroller.VerticalOffset < ItemScroller.ScrollableHeight - 0.5
                : ItemScroller.VerticalOffset > 0.5;

            if (canScroll) return;

            if (scrollingDown) NextPage();
            else PrevPage();

            e.Handled = true;
        }

        // ══════════════════════ 定位（坑 7：多屏 + 高 DPI） ══════════════════════

        /// <summary>
        /// 用物理像素摆放窗口。GetCursorPos / SetWindowPos 走的都是物理坐标，
        /// 绕开了 WPF 逻辑单位在不同缩放比显示器之间换算的麻烦。
        /// </summary>
        private void PositionAtCursor()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            // 用 WinForms 的 Screen 拿光标所在显示器的工作区，比自己做多屏枚举可靠
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(cursor.X, cursor.Y));
            var work = screen.WorkingArea;

            const int gap = 14;
            const int edge = 8;

            var x = cursor.X - width / 2;
            var y = cursor.Y + gap;

            // 下方放不下就翻到光标上方，避免面板被任务栏裁掉
            if (y + height > work.Bottom - edge) y = cursor.Y - height - gap;

            x = Math.Max(work.Left + edge, Math.Min(x, work.Right - width - edge));
            y = Math.Max(work.Top + edge, Math.Min(y, work.Bottom - height - edge));

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        /// <summary>显示前的粗略预定位，纯粹为了减少视觉跳动。</summary>
        private void PrePosition()
        {
            if (!NativeMethods.GetCursorPos(out var cursor)) return;

            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var scale = 1.0;

            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }

            var w = double.IsNaN(Width) ? 0 : Width;
            Left = cursor.X / scale - w / 2;
            Top = cursor.Y / scale + 14;
        }

        // ══════════════════════ 前台焦点（坑 1） ══════════════════════

        /// <summary>
        /// Windows 的「前台窗口锁定」策略会让后台进程的 SetForegroundWindow 静默失败——
        /// 任务栏图标闪一下，窗口却没到前台，键盘焦点还在别的程序上。
        /// 绕过办法：临时把自己的输入线程 attach 到当前前台窗口的线程上，
        /// 这样系统认为「是同一个输入上下文在请求前台」，就放行了。
        /// </summary>
        private void ForceForeground()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var currentThread = NativeMethods.GetCurrentThreadId();

            var attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

            try
            {
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.BringWindowToTop(hwnd);
                Activate();
                Focus();
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        /// <summary>
        /// 圆角与投影全部交给 DWM。
        ///
        /// 为什么不用 WPF 的 AllowsTransparency + DropShadowEffect：那会让整个窗口
        /// 退回软件渲染，而这是个热键高频唤出的面板，滚动和拖动会明显掉帧。
        ///
        /// 投影走两条路，因为单独一条都不够稳：
        ///   ① DwmExtendFrameIntoClientArea 把四条边各扩展 1px ——
        ///      DWM 因此认为窗口有非客户区，从而绘制系统标准投影。
        ///   ② CS_DROPSHADOW 作为兜底 —— 关掉「透明效果」的机器上 ① 会失效。
        /// 两条都失败也不会更糟：面板本身还有 1px 描边。
        /// </summary>
        private static void ApplyWindowChrome(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                var margins = new NativeMethods.MARGINS
                {
                    cxLeftWidth = 1,
                    cxRightWidth = 1,
                    cyTopHeight = 1,
                    cyBottomHeight = 1,
                };
                NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch
            {
                // 老系统上没有这个导出，忽略
            }

            try
            {
                var style = NativeMethods.SetClassLongPtr(
                    hwnd, NativeMethods.GCL_STYLE,
                    new IntPtr(NativeMethods.CS_DROPSHADOW));
                _ = style;
            }
            catch
            {
                try
                {
                    NativeMethods.SetClassLong(hwnd, NativeMethods.GCL_STYLE, NativeMethods.CS_DROPSHADOW);
                }
                catch
                {
                    // 两条都不可用就维持现状
                }
            }

            try
            {
                var preference = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Win10 不支持这个属性，忽略；面板保持直角
            }
        }

        // ══════════════════════ 数据绑定 ══════════════════════

        /// <summary>
        /// 面板尺寸。高度是按令牌算出来的常量，不跟内容走。
        ///
        /// 之前用的是 SizeToContent.Height：同一个面板，8 个条目和 16 个条目的高度
        /// 不一样，而且靠近屏幕底部时 PositionAtCursor 会把整个面板翻到光标上方 ——
        /// 格子位置在两种情况下完全不同，练出来的肌肉记忆直接失效。
        /// </summary>
        private void ApplyColumns()
        {
            var columns = Columns;

            ItemHost.Width = columns * Metrics.TileSlotWidth;
            Width = columns * Metrics.TileSlotWidth + HorizontalPadding;

            Height = Metrics.PanelBorder
                   + Metrics.HeaderHeight
                   + Metrics.VisibleRows * Metrics.TileSlotHeight
                   + Metrics.FooterHeight;

            SizeToContent = SizeToContent.Manual;
        }

        /// <summary>
        /// 重建所有已解析页的视图模型。
        /// 之所以一次把全部页都建出来，是为了让搜索能跨页 —— 只建当前页的话，
        /// 用户在「系统工具」页搜「计算器」会搜不到，然后以为程序坏了。
        /// </summary>
        private void ReloadItems()
        {
            _pageItems.Clear();
            _searchItems.Clear();

            foreach (var page in _pages)
            {
                var isCurrent = ReferenceEquals(page, CurrentPage);

                foreach (var model in page.Items)
                {
                    var vm = CreateViewModel(model);
                    _searchItems.Add(vm);

                    if (isCurrent) _pageItems.Add(vm);
                }
            }
        }

        private ItemViewModel CreateViewModel(ActionItem model)
        {
            var vm = new ItemViewModel(model);

            // 文本片段 / 按键的 Target 不是路径，走不了 IconService，直接给矢量图标
            var glyph = ItemGlyph.For(model.Type);
            if (glyph != null)
            {
                vm.Icon = glyph;
                return vm;
            }

            var isDirectory = model.Type == ItemType.Folder;

            // 异步取图标：先给空图占位，提取完成后回调在 UI 线程刷新
            _icons.Request(model.Target, isDirectory, image => vm.Icon = image);
            return vm;
        }

        private void ApplyFilter(string? query)
        {
            if (!_initialized) return;

            var text = (query ?? "").Trim();

            _view.Clear();

            if (text.Length == 0)
            {
                // 空查询：严格保持网格原始顺序，一个格子都不许动。
                // 按使用频次重排会让用户练出来的肌肉记忆失效，详见 SearchRanker 的注释。
                foreach (var vm in _pageItems)
                    _view.Add(vm);
            }
            else
            {
                // 开始输入：跨页搜，按「匹配度 → 使用频次 → 最近使用」排序
                var ranked = new List<(ItemViewModel Vm, long Rank)>(_searchItems.Count);

                foreach (var vm in _searchItems)
                {
                    var score = SearchRanker.Score(vm.Model, text);
                    if (score == SearchRanker.NoMatch) continue;

                    ranked.Add((vm, SearchRanker.Rank(score, vm.Model)));
                }

                ranked.Sort((a, b) =>
                {
                    var byRank = b.Rank.CompareTo(a.Rank);
                    return byRank != 0
                        ? byRank
                        : SearchRanker.CompareRecency(a.Vm.Model, b.Vm.Model);
                });

                foreach (var entry in ranked)
                    _view.Add(entry.Vm);
            }

            AssignIndexes();
            ResetFocusCursor();
            UpdateEmptyHint(text);
            UpdateAddTile();
            UpdateStatus();
        }

        /// <summary>
        /// 给前 9 格打上数字角标，对应 Ctrl+1..9。
        /// 顺序就是当前网格顺序，所以搜索状态下角标跟着排序结果走 —— 这是对的：
        /// 用户看到的就是「第 3 个格子」，按 Ctrl+3 命中的也应该是它。
        /// </summary>
        private void AssignIndexes()
        {
            for (var i = 0; i < _view.Count; i++)
                _view[i].IndexText = i < 9 ? (i + 1).ToString() : "";
        }

        /// <summary>把键盘焦点光标收回第一格；网格为空时清掉。</summary>
        private void ResetFocusCursor()
        {
            _focusIndex = _view.Count > 0 ? 0 : -1;
            SyncFocusCursor(scrollIntoView: false);
        }

        private void SyncFocusCursor(bool scrollIntoView)
        {
            for (var i = 0; i < _view.Count; i++)
                _view[i].IsFocusCursor = i == _focusIndex;

            if (scrollIntoView && _focusIndex >= 0) EnsureFocusVisible();
        }

        /// <summary>
        /// 焦点移出可视区就没有反馈了，所以跟着滚一下。
        /// 按行算而不是去问容器：格子等高、行高是常量，这样更便宜也更稳。
        /// </summary>
        private void EnsureFocusVisible()
        {
            var row = _focusIndex / Math.Max(1, Columns);
            var top = row * Metrics.TileSlotHeight;
            var bottom = top + Metrics.TileSlotHeight;

            if (top < ItemScroller.VerticalOffset)
                ItemScroller.ScrollToVerticalOffset(top);
            else if (bottom > ItemScroller.VerticalOffset + ItemScroller.ViewportHeight)
                ItemScroller.ScrollToVerticalOffset(bottom - ItemScroller.ViewportHeight);
        }

        /// <summary>方向键移动键盘焦点光标。</summary>
        private void MoveFocus(int dx, int dy)
        {
            var count = _view.Count;
            if (count == 0) return;

            var index = _focusIndex >= 0 && _focusIndex < count ? _focusIndex : 0;

            if (dy != 0)
            {
                var columns = Math.Max(1, Columns);
                var target = index + columns * dy;

                // 往上出界就不动，比绕到最后一行更符合预期；
                // 往下出界落到最后一项 —— 最后一行常常是不满的。
                if (target < 0) return;
                if (target >= count) target = count - 1;

                index = target;
            }
            else if (dx != 0)
            {
                // 左右换行。网格里换行比在行首卡住顺手。
                index = ((index + dx) % count + count) % count;
            }
            else
            {
                return;
            }

            _focusIndex = index;
            SyncFocusCursor(scrollIntoView: true);
            UpdateStatus();
        }

        /// <summary>键盘焦点对应的条目；还没移动过焦点时退化为第一项。</summary>
        private ItemViewModel? FocusedItem()
        {
            if (_focusIndex >= 0 && _focusIndex < _view.Count) return _view[_focusIndex];
            return _view.FirstOrDefault();
        }

        private void UpdateEmptyHint(string text)
        {
            // ① 面板整个是空的 —— 用户的问题是「不知道从哪开始」，给两条具体的路
            if (_searchItems.Count == 0)
            {
                EmptyArt.Visibility = Visibility.Visible;
                EmptyTitle.Text = "面板还是空的";
                EmptySubtitle.Text = "扫描开始菜单把已装的软件一次性收进来，或者把程序、文件夹直接拖到这里";
                EmptyScanButton.Visibility = Visibility.Visible;
                EmptyPickButton.Content = "从文件添加";
                EmptyPickButton.Tag = null;
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            // ② 搜不到 —— 搜索失败的那一刻，恰好是用户最想添加东西的时刻
            if (_view.Count == 0)
            {
                EmptyArt.Visibility = Visibility.Visible;
                EmptyTitle.Text = $"没有匹配「{Shorten(text, 20)}」的条目";

                if (LooksLikeUrl(text))
                {
                    EmptySubtitle.Text = "它看起来是个网址，可以直接加进来";
                    EmptyPickButton.Content = "添加为网址";
                    EmptyPickButton.Tag = text;
                }
                else
                {
                    EmptySubtitle.Text = "换个关键词，或者把它作为文件加进来";
                    EmptyPickButton.Content = "添加文件…";
                    EmptyPickButton.Tag = null;
                }

                EmptyScanButton.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
        }

        /// <summary>够不够像网址。判断放宽一点没关系，加错了用户还能删。</summary>
        private static bool LooksLikeUrl(string text)
        {
            if (text.Contains("://", StringComparison.Ordinal)) return true;
            if (text.Contains(' ') || text.Contains('\\') || text.Contains('/')) return false;

            var dot = text.IndexOf('.');
            return dot > 0 && dot < text.Length - 1;
        }

        private static string Shorten(string text, int max)
            => text.Length <= max ? text : text.Substring(0, max) + "…";

        private void UpdateStatus()
        {
            var text = SearchBox?.Text?.Trim() ?? "";

            if (text.Length > 0)
            {
                SetStatus($"匹配 {_view.Count} / {_searchItems.Count} 项　·　按匹配度与使用频次排序");
                return;
            }

            // 有选中时整块状态变成淡蓝胶囊 —— 这一刻用户关心的是能对这些格子做什么
            var selected = SelectedCount();
            if (selected > 0)
            {
                SetStatus($"已选 {selected} 项　·　Ctrl+点击增减　·　拖动可一起搬　·　右键批量操作",
                    highlighted: true);
                return;
            }

            var total = CurrentPage?.Items.Count ?? 0;
            var pageName = CurrentPage?.Name ?? "默认";

            var parts = new List<string>
            {
                _sceneProcess != null ? $"{pageName} · {_sceneProcess} 场景" : pageName,
                $"{total} 项",
            };

            parts.Add(_pages.Count > 1
                ? $"{_pageIndex + 1}/{_pages.Count} 页　·　滚轮或 PgUp/PgDn 翻页"
                : $"{_config.Current.Hotkey.Display} 唤出");

            if (total == 0) parts.Add("拖入文件即可添加");

            SetStatus(string.Join("　·　", parts));
        }

        /// <summary>highlighted 会把状态整块渲染成淡蓝胶囊，用于「有选中」这种需要跳出来的时刻。</summary>
        private void SetStatus(string text, bool highlighted = false)
        {
            StatusText.Text = text;
            StatusText.Foreground = highlighted ? StatusSelectedText : TextTertiaryBrush;
            StatusChip.Background = highlighted ? StatusSelectedBg : Brushes.Transparent;
        }

        /// <summary>
        /// 把「＋ 添加」格摆到第一个空槽位上；网格满了就藏起来。
        ///
        /// 它刻意不放进 ItemsControl：塞进去会让 _view 多出一个假条目，
        /// 拖拽下标、多选、搜索排序全都要跟着特判，那种 bug 很难查。
        /// </summary>
        private void UpdateAddTile()
        {
            var columns = Math.Max(1, Columns);
            var index = _view.Count;

            // 面板全空时由空状态负责引导，再摆个「＋」是重复的
            if (index == 0 || index >= columns * Metrics.VisibleRows)
            {
                AddTile.Visibility = Visibility.Collapsed;
                return;
            }

            Canvas.SetLeft(AddTile, index % columns * Metrics.TileSlotWidth + Metrics.TileMargin);
            Canvas.SetTop(AddTile, index / columns * Metrics.TileSlotHeight + Metrics.TileMargin);
            AddTile.Visibility = Visibility.Visible;
        }

        // ══════════════════════ 添加条目 ══════════════════════

        /// <summary>取当前可写入的页。配置被改坏到一页都没有时现建一页。</summary>
        private ActionPage? TargetPage()
        {
            var page = CurrentPage;
            if (page != null) return page;

            page = new ActionPage { Name = "默认" };
            _config.Current.GlobalPages.Add(page);

            _pages = new List<ActionPage> { page };
            _pageIndex = 0;
            _sceneProcess = null;
            _scenePageCount = 0;

            return page;
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            var page = TargetPage();
            if (page == null) return;

            var added = 0;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                // 去重范围是整个配置，不只是当前页 ——
                // 否则同一个程序能在「常用」和「系统工具」里各加一份
                var exists = ConfigStore.AllPages(_config.Current)
                    .Any(p => p.Items.Any(i => string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase)));
                if (exists) continue;

                page.Items.Add(new ActionItem
                {
                    Name = ItemViewModel.DetectName(path),
                    Target = path,
                    Type = ItemViewModel.DetectType(path),
                });

                added++;
            }

            if (added == 0)
            {
                SetStatus("这些条目已经在面板里了");
                return;
            }

            _config.Save();
            ReloadItems();
            ApplyFilter(SearchBox.Text);
            UpdatePageChrome();
            SetStatus($"已添加 {added} 项");
        }

        private void AddItem(ActionItem item)
        {
            var page = TargetPage();
            if (page == null) return;

            page.Items.Add(item);
            _config.Save();
            ReloadItems();
            ApplyFilter(SearchBox.Text);
            UpdatePageChrome();
        }

        private void BuildBackgroundMenu()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "PanelContextMenuStyle");
            menu.Opened += OnContextMenuOpened;
            menu.Closed += OnContextMenuClosed;

            var addFile = new MenuItem { Header = "添加文件..." };
            addFile.Click += (_, _) => PickFiles();

            var addFolder = new MenuItem { Header = "添加文件夹..." };
            addFolder.Click += (_, _) => PickFolder();

            var addUrl = new MenuItem { Header = "添加网址..." };
            addUrl.Click += (_, _) => PickUrl();

            var addText = new MenuItem { Header = "添加文本片段..." };
            addText.Click += (_, _) => PickText();

            var addKeys = new MenuItem { Header = "添加按键组合..." };
            addKeys.Click += (_, _) => PickKeys();

            var importFromStartMenu = new MenuItem { Header = "从开始菜单批量导入…" };
            importFromStartMenu.Click += (_, _) => ImportFromStartMenu();

            var openData = new MenuItem { Header = "打开数据目录" };
            openData.Click += (_, _) => ShellLauncher.RevealInExplorer(ConfigStore.DataDirectory);

            // 动作页管理。内容依赖当前状态（前台程序名、当前页归属），
            // 所以等到子菜单展开的那一刻才填充。
            var pages = new MenuItem { Header = "动作页" };
            pages.SubmenuOpened += (_, _) => PopulatePageMenu(pages.Items);

            menu.Items.Add(addFile);
            menu.Items.Add(addFolder);
            menu.Items.Add(addUrl);
            menu.Items.Add(addText);
            menu.Items.Add(addKeys);
            menu.Items.Add(importFromStartMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(pages);
            menu.Items.Add(new Separator());
            menu.Items.Add(openData);

            ContextMenu = menu;
        }

        private void PickFiles()
        {
            WithSuppressedDeactivate(() =>
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "选择要添加到面板的文件或程序",
                    Filter = "所有文件 (*.*)|*.*",
                    Multiselect = true,
                    CheckFileExists = true,
                };

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                AddPaths(dialog.FileNames);
            });
        }

        private void PickFolder()
        {
            WithSuppressedDeactivate(() =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择要添加到面板的文件夹",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                };

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                AddPaths(new[] { dialog.SelectedPath });
            });
        }

        private void PickUrl()
        {
            WithSuppressedDeactivate(() =>
            {
                var url = InputDialog.Show(this, "添加网址", "请输入完整网址（含 http:// 或 https://）：", "https://");
                if (string.IsNullOrWhiteSpace(url)) return;

                AddItem(new ActionItem
                {
                    Name = ItemViewModel.DetectName(url),
                    Target = url.Trim(),
                    Type = ItemType.Url,
                });
            });
        }

        /// <summary>添加文本片段。名字从第一行推出来，之后可以随时右键重命名。</summary>
        private void PickText()
        {
            WithSuppressedDeactivate(() =>
            {
                var text = InputDialog.Show(this, "添加文本片段",
                    "点这个格子时，会把下面的文字打到当前光标处：", "", multiline: true);
                if (string.IsNullOrWhiteSpace(text)) return;

                AddItem(new ActionItem
                {
                    Name = SnippetName(text),
                    Target = text,
                    Type = ItemType.Text,
                });

                SetStatus("已添加。点它会把这段文字打到你正在输入的地方");
            });
        }

        /// <summary>添加按键组合。</summary>
        private void PickKeys()
        {
            WithSuppressedDeactivate(() =>
            {
                var combo = InputDialog.Show(this, "添加按键",
                    "支持 Ctrl / Alt / Shift / Win 组合，例如：\n" +
                    "Ctrl+Shift+T　Alt+Tab　F5　Win+V　Ctrl+S", "");
                if (string.IsNullOrWhiteSpace(combo)) return;

                // 在这里就拦住格式错误，而不是等到点格子的时候 ——
                // 那时面板已经隐藏了，报错用户根本看不见，只会觉得「按了没反应」。
                if (!KeyComboParser.TryParse(combo, out _, out var error))
                {
                    MessageBox.Show(error, "按键格式不对",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddItem(new ActionItem
                {
                    Name = combo.Trim(),
                    Target = combo.Trim(),
                    Type = ItemType.Keys,
                });

                SetStatus($"已添加「{combo.Trim()}」，点它会发给当前的窗口");
            });
        }

        /// <summary>
        /// 从一段文本推出一个像样的格子名：取第一个非空行，太长就截断。
        /// 不另外问一次名字 —— 多用一次对话框，换来的只是少点一次右键重命名。
        /// </summary>
        private static string SnippetName(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;

                // 中文字宽约为英文两倍，按 16 个英文字符的视觉宽度截
                return line.Length <= 16 ? line : line[..15] + "…";
            }

            return "(文本片段)";
        }

        /// <summary>扫描开始菜单 / 桌面，勾选后批量导入到当前动作页。</summary>
        private void ImportFromStartMenu()
        {
            WithSuppressedDeactivate(() =>
            {
                // 把已有的目标路径都传进去，导入列表里可以标出「已在面板里」
                var existing = ConfigStore.AllPages(_config.Current)
                    .SelectMany(p => p.Items)
                    .Select(i => i.Target)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                var dialog = new ImportWindow(existing, CurrentPage?.Name ?? "当前页");

                if (IsVisible) dialog.Owner = this;

                if (dialog.ShowDialog() != true) return;

                var page = TargetPage();
                if (page == null) return;

                var added = 0;

                foreach (var entry in dialog.Selected)
                {
                    // 去重范围是整个配置，不只是当前页 ——
                    // 否则同一个快捷方式能在两页里各加一份
                    var duplicate = ConfigStore.AllPages(_config.Current).Any(p =>
                        p.Items.Any(i => string.Equals(i.Target, entry.Path, StringComparison.OrdinalIgnoreCase)));
                    if (duplicate) continue;

                    page.Items.Add(new ActionItem
                    {
                        Name = entry.Name,
                        Target = entry.Path,
                        Type = ItemViewModel.DetectType(entry.Path),
                    });

                    added++;
                }

                if (added == 0)
                {
                    SetStatus("没有新增条目（选中的都已经在面板里了）");
                    return;
                }

                _config.Save();
                ReloadItems();
                ApplyFilter("");
                UpdatePageChrome();
                SetStatus($"已从开始菜单导入 {added} 项到「{page.Name}」");
            });
        }

        // ══════════════════════ 多选 ══════════════════════

        /// <summary>Shift 连选的锚点（_view 里的下标）。-1 表示还没有锚点。</summary>
        private int _selectionAnchor = -1;

        /// <summary>
        /// 搜索状态下不做多选。那时网格里装的是跨页的排序结果，顺序是算出来的，
        /// 「选中的是哪几个」会随输入变化，没有稳定含义。
        /// </summary>
        private bool SelectionEnabled => string.IsNullOrEmpty(SearchBox.Text);

        private List<ItemViewModel> SelectedVms()
        {
            var result = new List<ItemViewModel>();
            foreach (var vm in _pageItems)
            {
                if (vm.IsSelected) result.Add(vm);
            }
            return result;
        }

        private int SelectedCount()
        {
            var count = 0;
            foreach (var vm in _pageItems)
            {
                if (vm.IsSelected) count++;
            }
            return count;
        }

        private void ClearSelection()
        {
            if (SelectedCount() == 0) return;

            foreach (var vm in _pageItems) vm.IsSelected = false;
            _selectionAnchor = -1;
            UpdateStatus();
        }

        private void SelectOnly(ItemViewModel vm)
        {
            foreach (var item in _pageItems) item.IsSelected = ReferenceEquals(item, vm);
            _selectionAnchor = _view.IndexOf(vm);
            UpdateStatus();
        }

        private void ToggleSelection(ItemViewModel vm)
        {
            vm.IsSelected = !vm.IsSelected;
            _selectionAnchor = _view.IndexOf(vm);
            UpdateStatus();
        }

        /// <summary>Shift+点击：从锚点连选到这一格。锚点不存在时以这一格为锚点。</summary>
        private void SelectRangeTo(ItemViewModel vm)
        {
            var to = _view.IndexOf(vm);
            if (to < 0) return;

            var anchor = _selectionAnchor >= 0 && _selectionAnchor < _view.Count
                ? _selectionAnchor
                : to;

            var lo = Math.Min(anchor, to);
            var hi = Math.Max(anchor, to);

            for (var i = 0; i < _view.Count; i++)
                _view[i].IsSelected = i >= lo && i <= hi;

            _selectionAnchor = anchor;
            UpdateStatus();
        }

        private void SelectAllOnPage()
        {
            foreach (var vm in _pageItems) vm.IsSelected = true;
            _selectionAnchor = 0;
            UpdateStatus();
        }

        /// <summary>从命中的元素往上找，看这次点击是不是落在某个格子上。</summary>
        private static bool IsInsideTile(object? source)
        {
            var node = source as DependencyObject;

            while (node != null)
            {
                if (node is Button { Tag: ItemViewModel }) return true;

                node = node is Visual
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>点面板空白处 = 取消选择。</summary>
        private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (SelectedCount() == 0) return;

            // 点在格子上时按钮自己会处理，这里只兜住真正落在空白处的点击
            if (IsInsideTile(e.OriginalSource)) return;

            ClearSelection();
        }

        // ══════════════════════ 启动 ══════════════════════

        private void LaunchItem(ItemViewModel? vm)
        {
            if (vm == null) return;

            // 文本片段 / 按键不走 ShellLauncher —— 它们不是「启动某个东西」。
            //
            // 两者的默认动作刻意不同：
            //   文本片段 → 复制到剪贴板。这是可预期的、不依赖焦点的行为。
            //   按键组合 → 发到原窗口。它的全部意义就是「发出去」，复制一串
            //              "Ctrl+Shift+T" 到剪贴板没有任何用处。
            // 想要「把文本直接打到光标处」的话，右键菜单里有。
            if (vm.Model.Type == ItemType.Text)
            {
                CopySnippet(vm);
                return;
            }

            if (vm.Model.Type == ItemType.Keys)
            {
                SendToPreviousWindow(vm.Model);
                return;
            }

            if (ShellLauncher.Launch(vm.Model, out var error))
            {
                // 记使用次数。只用于将来给搜索结果排序，绝不改变网格位置 ——
                // 格子顺序一变，肌肉记忆就废了。
                vm.Model.UseCount++;
                vm.Model.LastUsedUtc = DateTime.UtcNow;
                _config.Save();

                // 钉住状态下不自动隐藏，方便连续启动多个
                if (PinToggle.IsChecked != true) HidePanel();
            }
            else
            {
                SetStatus($"启动失败：{error}");
            }
        }

        /// <summary>
        /// 把文本片段复制到剪贴板。这是「文本片段」格子的默认动作。
        ///
        /// 为什么默认不是「直接打到光标处」：那需要把焦点交回原窗口，
        /// 而这一步受系统前台调度规则限制，做不到在所有场合都成立（见
        /// <see cref="SendToPreviousWindow"/>）。复制到剪贴板则永远成功，
        /// 用户自己 Ctrl+V —— 粘到哪、什么时候粘，都由他决定，可预期得多。
        /// 想直接打进去的话，右键菜单里有「发送到当前光标处」。
        /// </summary>
        private void CopySnippet(ItemViewModel vm)
        {
            if (!TrySetClipboard(vm.Model.Target))
            {
                SetStatus("复制失败：剪贴板被别的程序占用，再点一次试试");
                return;
            }

            vm.Model.UseCount++;
            vm.Model.LastUsedUtc = DateTime.UtcNow;
            _config.Save();

            if (PinToggle.IsChecked == true)
            {
                // 钉住时不隐藏，方便连着复制好几个
                SetStatus($"已复制「{vm.Name}」");
            }
            else
            {
                HidePanel();
            }

            // 面板藏起来之后状态栏就看不见了，只能走托盘气泡
            Notify?.Invoke("已复制到剪贴板", Preview(vm.Model.Target));
        }

        /// <summary>
        /// 写剪贴板，带重试。剪贴板是全局资源，别的程序（远程桌面、
        /// 剪贴板管理器、Office 的粘贴板）可能正锁着它，此时 <c>SetText</c>
        /// 会抛 COMException —— 直接失败的话用户只会觉得「点了没反应」。
        /// </summary>
        private static bool TrySetClipboard(string text)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(30);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>气泡里给一行预览，多行文本压成一行。</summary>
        private static string Preview(string text)
        {
            var flat = text.Replace("\r\n", " ")
                           .Replace('\n', ' ')
                           .Replace('\r', ' ')
                           .Trim();

            return flat.Length <= 40 ? flat : flat[..39] + "…";
        }

        /// <summary>
        /// 把按键发到「唤出面板之前」的那个窗口。
        ///
        /// ── 这里有两个必须遵守的顺序，反了就是静默失败 ──
        ///
        /// ① **先交焦点，再藏面板**。SetForegroundWindow 只在「调用方自己还是
        /// 前台进程」时被系统接受。先 HidePanel() 的话，本进程立刻失去前台身份，
        /// 这个调用会被直接拒绝：返回 false，不抛异常、不打日志。焦点于是落到
        /// 某个随机窗口上，按键全打飞 —— 用户看到的就是「点了完全没反应」。
        ///
        /// ② **等焦点真的到了再发**。交出焦点是异步的，立刻发会打进面板自己的
        /// 搜索框。用轮询而不是固定延时：不同机器、不同目标程序差别很大，
        /// 固定 200ms 在慢机器上不够，在快机器上又白等。
        /// </summary>
        private void SendToPreviousWindow(ActionItem item)
        {
            var target = _externalHwnd;

            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                // 记不下原窗口（比如程序刚启动、面板是第一个窗口）就没法发。
                // 与其静默失败，不如说清楚。
                SetStatus("不知道该发给谁：没记下唤出面板之前的窗口");
                return;
            }

            var activated = NativeMethods.SetForegroundWindow(target);
            if (!activated) App.Log($"SetForegroundWindow 被拒绝，hwnd={target}");

            var waited = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };

            timer.Tick += (_, _) =>
            {
                if (!NativeMethods.IsWindow(target))
                {
                    // 目标窗口已经关掉了，别再等
                    timer.Stop();
                    return;
                }

                waited += 25;

                if (NativeMethods.GetForegroundWindow() != target && waited < 500) return;

                timer.Stop();

                // 焦点已经交出去了，这时藏面板不会再把焦点抢回来
                if (PinToggle.IsChecked != true) HidePanel();

                DeliverToForeground(item, target, waited >= 500);
            };

            timer.Start();
        }

        private void DeliverToForeground(ActionItem item, IntPtr target, bool timedOut)
        {
            bool ok;

            if (item.Type == ItemType.Text)
            {
                ok = InputSender.SendText(item.Target);
            }
            else
            {
                // 格式在添加时已经校验过，这里再解析一次只是为了拿结果
                ok = KeyComboParser.TryParse(item.Target, out var combo, out _)
                     && InputSender.SendKeys(combo);
            }

            if (ok)
            {
                // 和启动程序一样，只记使用次数用于搜索排序，不动网格位置
                item.UseCount++;
                item.LastUsedUtc = DateTime.UtcNow;
                _config.Save();
                return;
            }

            // 面板多半已经隐藏了，状态栏用户看不见 —— 落日志，并且等超时这种
            // 可诊断的情况要把面板叫回来说明白
            var reason = timedOut
                ? $"等焦点回到目标窗口超时（{item.Type}）"
                : $"SendInput 被系统拒绝（{item.Type}）";

            App.Log($"发送失败：{reason}，名称={item.Name}");

            if (timedOut)
            {
                ShowPanel();
                SetStatus($"没能打进去：{reason}。可以再点一次试试");
            }
        }

        // ══════════════════════ 格子拖拽排序 ══════════════════════

        /// <summary>内部拖拽用的数据格式。自定义格式是为了跟「从资源管理器拖文件进来」区分开。</summary>
        private const string TileDragFormat = "QuickerLite.Tile";

        /// <summary>
        /// 触发拖拽的位移阈值。取系统值和 6px 里较大的那个 ——
        /// 阈值太小的话，点格子启动程序时手一抖就变成拖拽，程序反而不启动了。
        /// </summary>
        private static readonly double DragThreshold =
            Math.Max(SystemParameters.MinimumHorizontalDragDistance, 6);

        private Point _tileMouseDownPoint;
        private ItemViewModel? _tileDragCandidate;
        private ItemViewModel? _dragSource;

        /// <summary>本次拖拽实际带着走的格子。单选时只有一个，多选时是整个选区。</summary>
        private List<ItemViewModel> _dragSet = new();

        private int _dropIndex = -1;

        /// <summary>拖拽刚结束时要吞掉一次 Click，免得拖完顺手把程序启动了。</summary>
        private bool _suppressNextClick;

        private DispatcherTimer? _autoScrollTimer;
        private Point _dragPointInScroller;

        private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 任何一次新的按下都清掉上次拖拽留下的抑制标记
            _suppressNextClick = false;

            if (e.ChangedButton != MouseButton.Left) return;
            if (sender is not Button { Tag: ItemViewModel vm }) return;

            _tileDragCandidate = vm;
            _tileMouseDownPoint = e.GetPosition(this);
        }

        private void OnTileMouseMove(object sender, MouseEventArgs e)
        {
            var candidate = _tileDragCandidate;
            if (candidate == null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _tileDragCandidate = null;
                return;
            }

            // 搜索状态下网格里装的是跨页的排序结果，重排没有意义
            if (!string.IsNullOrEmpty(SearchBox.Text) || _view.Count <= 1) return;

            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _tileMouseDownPoint.X) < DragThreshold &&
                Math.Abs(now.Y - _tileMouseDownPoint.Y) < DragThreshold)
                return;

            _tileDragCandidate = null;
            StartTileDrag(candidate);
        }

        private void StartTileDrag(ItemViewModel vm)
        {
            if (!_pageItems.Contains(vm)) return;

            // 拖的是一格没被选中的格子 → 只拖它自己，顺手把原来的选择收掉。
            // 跟资源管理器一致：拖一个没选中的东西，不会把别的选中项一起带走。
            if (vm.IsSelected)
            {
                _dragSet = SelectedVms();
            }
            else
            {
                if (SelectedCount() > 0) ClearSelection();
                _dragSet = new List<ItemViewModel> { vm };
            }

            if (_dragSet.Count == 0) _dragSet.Add(vm);

            _dragSource = vm;
            _dropIndex = -1;
            _suppressNextClick = true;

            foreach (var item in _dragSet) SetContainerOpacity(item, 0.35);

            // 腰栏换成页签，拖到上面就能把格子挪到别的页
            EnterDropMode();

            // 拖拽期间面板绝不能因为「失焦」自己藏起来
            _suppressDeactivate++;

            try
            {
                var data = new DataObject(TileDragFormat, vm.Model.Id);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
            }
            finally
            {
                // DoDragDrop 是阻塞的，不管落位、取消还是按 Esc 都会走到这里，
                // 所以清理放在 finally 里最稳，不用去猜 DragLeave 的时机。
                _suppressDeactivate = Math.Max(0, _suppressDeactivate - 1);
                EndDragVisuals();
            }
        }

        /// <summary>算出插入位置并摆好指示条。</summary>
        private void UpdateDropIndicator(Point pointInHost)
        {
            if (_dragSource == null) return;

            _dropIndex = ComputeInsertIndex(pointInHost);
            ShowDropMarker(_dropIndex);
        }

        /// <summary>
        /// 由鼠标位置反推「应该插到第几个格子前面」。
        /// 数学部分在 TileReorder 里（纯函数，可以脱离界面单独验证），
        /// 这里只负责把各个格子的边界收集出来。
        /// </summary>
        private int ComputeInsertIndex(Point pointInHost)
        {
            var bounds = new List<Rect>(_view.Count);

            for (var i = 0; i < _view.Count; i++)
            {
                // WrapPanel 不做虚拟化，容器一定都在。真取不到就跳过 ——
                // 算出来的位置可能偏一格，但不会抛异常把面板搞崩。
                if (TryGetContainerBounds(i, out var b)) bounds.Add(b);
            }

            return TileReorder.ComputeInsertIndex(bounds, pointInHost);
        }

        private bool TryGetContainerBounds(int index, out Rect bounds)
        {
            bounds = default;
            if (index < 0 || index >= _view.Count) return false;

            if (ItemHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                return false;

            if (container.ActualWidth <= 0 || container.ActualHeight <= 0) return false;

            var origin = container.TransformToAncestor(DropLayer).Transform(new Point(0, 0));
            bounds = new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
            return true;
        }

        private void ShowDropMarker(int index)
        {
            var count = _view.Count;
            if (count == 0)
            {
                DropMarker.Visibility = Visibility.Collapsed;
                return;
            }

            double x;
            Rect bounds;

            if (index < count)
            {
                if (!TryGetContainerBounds(index, out bounds))
                {
                    DropMarker.Visibility = Visibility.Collapsed;
                    return;
                }
                x = bounds.Left - 2.5;
            }
            else
            {
                if (!TryGetContainerBounds(count - 1, out bounds))
                {
                    DropMarker.Visibility = Visibility.Collapsed;
                    return;
                }
                x = bounds.Right + 0.5;
            }

            DropMarker.Height = Math.Max(16, bounds.Height - 8);
            Canvas.SetLeft(DropMarker, x);
            Canvas.SetTop(DropMarker, bounds.Top + 4);
            DropMarker.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 真正落位：把一组格子搬到 insertIndex 前面，并落盘。
        /// 单个格子也走这条路（传一个元素的列表），省得维护两套下标数学。
        /// </summary>
        private void MoveTiles(IReadOnlyList<ItemViewModel> vms, int insertIndex)
        {
            var page = CurrentPage;
            if (page == null || vms.Count == 0) return;

            var from = new List<int>(vms.Count);
            foreach (var vm in vms)
            {
                var index = _view.IndexOf(vm);
                if (index >= 0) from.Add(index);
            }

            if (from.Count == 0) return;
            from.Sort();

            var moving = new List<ItemViewModel>(from.Count);
            foreach (var index in from) moving.Add(_view[index]);

            var target = TileReorder.ResolveBlockMoveTarget(from, insertIndex, _view.Count);
            if (target < 0)
            {
                SetStatus("顺序没有变化");
                return;
            }

            // 先算出搬完之后的样子。不连续的选区有可能算出跟现在一样的顺序，
            // 所以「有没有变」只能靠比较，不能只看 target 等不等于起始下标。
            var after = new List<ItemViewModel>(_view);
            for (var i = from.Count - 1; i >= 0; i--) after.RemoveAt(from[i]);
            after.InsertRange(target, moving);

            if (after.SequenceEqual(_view))
            {
                SetStatus("顺序没有变化");
                return;
            }

            // 从后往前摘，否则摘掉前面的之后，后面的下标就全错位了
            for (var i = from.Count - 1; i >= 0; i--) _view.RemoveAt(from[i]);
            for (var i = 0; i < moving.Count; i++) _view.Insert(target + i, moving[i]);

            // 无搜索时 _pageItems 和 _view 是同一批对象、同一顺序，一起同步
            _pageItems.Clear();
            _pageItems.AddRange(_view);

            // 回写模型。原地改这个 List，不换引用 —— 别处可能还攥着它。
            // 注意不要顺手调 ReloadItems：那会重新请求一遍图标，白闪一下。
            page.Items.Clear();
            foreach (var item in _pageItems) page.Items.Add(item.Model);

            _config.Save();

            SetStatus(moving.Count == 1
                ? $"「{moving[0].Name}」已移到第 {target + 1} 位"
                : $"已把 {moving.Count} 项移到第 {target + 1} 位起");
        }

        private void SetContainerOpacity(ItemViewModel vm, double opacity)
        {
            if (ItemHost.ItemContainerGenerator.ContainerFromItem(vm) is UIElement container)
                container.Opacity = opacity;
        }

        private void EndDragVisuals()
        {
            StopAutoScroll();
            ExitDropMode();

            foreach (var item in _dragSet) SetContainerOpacity(item, 1.0);

            _dragSet.Clear();
            _dragSource = null;
            _dropIndex = -1;
            DropMarker.Visibility = Visibility.Collapsed;
        }

        // ── 拖到边缘时自动滚动 ──

        private void UpdateAutoScroll(Point pointInScroller)
        {
            _dragPointInScroller = pointInScroller;

            if (ItemScroller.ScrollableHeight <= 0.5)
            {
                StopAutoScroll();
                return;
            }

            var height = ItemScroller.ViewportHeight;
            if (pointInScroller.Y >= 28 && pointInScroller.Y <= height - 28)
            {
                StopAutoScroll();
                return;
            }

            _autoScrollTimer ??= CreateAutoScrollTimer();
            _autoScrollTimer.Start();
        }

        private DispatcherTimer CreateAutoScrollTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };

            timer.Tick += (_, _) =>
            {
                var height = ItemScroller.ViewportHeight;
                var y = _dragPointInScroller.Y;

                var delta = y < 28 ? -16.0 : y > height - 28 ? 16.0 : 0.0;
                if (delta == 0)
                {
                    StopAutoScroll();
                    return;
                }

                var target = Math.Clamp(
                    ItemScroller.VerticalOffset + delta, 0, ItemScroller.ScrollableHeight);

                if (Math.Abs(target - ItemScroller.VerticalOffset) < 0.5)
                {
                    StopAutoScroll();
                    return;
                }

                ItemScroller.ScrollToVerticalOffset(target);

                // 内容滚了，指示条得跟着内容走
                if (_dropIndex >= 0) ShowDropMarker(_dropIndex);
            };

            return timer;
        }

        private void StopAutoScroll() => _autoScrollTimer?.Stop();

        // ── 页签：腰栏平时用它跳页，拖拽时它变成「移到哪一页」的放置区 ──

        /// <summary>腰栏里的文字页签。</summary>
        private readonly List<Border> _pageTabs = new();

        /// <summary>拖拽放置模式下的页签（另一组实例，外观共用 ApplyTabVisual）。</summary>
        private readonly List<Border> _pageChips = new();
        private int _highlightedChip = -1;

        /// <summary>
        /// 腰栏当前是否被页签顶替。需要这个门闩是因为「落位」和「拖拽结束」
        /// 都会调到 ExitDropMode，第二次跑会把刚设好的「已移到…」提示冲掉。
        /// </summary>
        private bool _dropModeActive;

        // ── 代码里动态生成的元素用的共享画刷 ──
        // 颜色一律来自 Palette，XAML 那份从同一个地方取，不会漂移。
        // 全部 Freeze 掉：这些元素每次重建都会用到，不能每次都分配新对象。

        private static readonly Brush TabBgActive = Frozen(Palette.Accent100);
        private static readonly Brush TabStrokeHot = Frozen(Palette.Accent500);
        private static readonly Brush TabTextActive = Frozen(Palette.Accent700);
        private static readonly Brush TextSecondaryBrush = Frozen(Palette.TextSecondary);
        private static readonly Brush TextTertiaryBrush = Frozen(Palette.TextTertiary);
        private static readonly Brush StatusSelectedBg = Frozen(Palette.Accent100);
        private static readonly Brush StatusSelectedText = Frozen(Palette.Accent700);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 进入「放置模式」：腰栏让位给页签。只在页数 &gt; 1 时才有意义 ——
        /// 只有一页的时候「移到哪一页」是个空问题，腰栏保持原样。
        /// </summary>
        private void EnterDropMode()
        {
            if (_pages.Count <= 1) return;
            if (_dropModeActive) return;

            _dropModeActive = true;

            PageDropBar.Visibility = Visibility.Visible;
            PageNav.Visibility = Visibility.Collapsed;
            StatusChip.Visibility = Visibility.Collapsed;
            FooterButtons.Visibility = Visibility.Collapsed;

            RenderPageChips();
        }

        private void ExitDropMode()
        {
            if (!_dropModeActive) return;
            _dropModeActive = false;

            PageDropBar.Visibility = Visibility.Collapsed;
            PageDropChips.Children.Clear();
            _pageChips.Clear();
            _highlightedChip = -1;

            StatusChip.Visibility = Visibility.Visible;
            FooterButtons.Visibility = Visibility.Visible;

            // PageNav 的显隐归 UpdatePageChrome 管，这里交给它恢复
            UpdatePageChrome();
            UpdateStatus();
        }

        /// <summary>
        /// 为每一页渲染一个放置目标。当前页也显示（拖上去会提示「已经在里面了」），
        /// 外观和腰栏平时的页签完全一致。
        /// </summary>
        private void RenderPageChips()
        {
            PageDropChips.Children.Clear();
            _pageChips.Clear();
            _highlightedChip = -1;

            for (var i = 0; i < _pages.Count; i++)
            {
                var index = i;
                var isCurrent = i == _pageIndex;

                var chip = new Border
                {
                    AllowDrop = true,
                    CornerRadius = new CornerRadius(Metrics.TabRadius),
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    Tag = index,
                    ToolTip = isCurrent
                        ? $"「{_pages[i].Name}」就是当前页"
                        : $"移到「{_pages[i].Name}」",
                    Child = new TextBlock
                    {
                        Text = _pages[i].Name,
                        FontSize = Metrics.FontLabel,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 110,
                    },
                };

                ApplyTabVisual(chip, isCurrent: isCurrent, isHot: false);

                chip.DragOver += OnPageChipDragOver;
                chip.DragLeave += OnPageChipDragLeave;
                chip.Drop += OnPageChipDrop;

                _pageChips.Add(chip);
                PageDropChips.Children.Add(chip);
            }
        }

        private void OnPageChipDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (sender is Border { Tag: int index }) HighlightChip(index);
        }

        private void OnPageChipDragLeave(object sender, DragEventArgs e)
        {
            if (sender is not Border chip || chip.Tag is not int index) return;
            if (index != _highlightedChip) return;

            // 移到 chip 内部的 TextBlock 上也会触发 DragLeave，先确认真的出界了
            var p = e.GetPosition(chip);
            if (p.X >= 0 && p.Y >= 0 && p.X <= chip.ActualWidth && p.Y <= chip.ActualHeight) return;

            HighlightChip(-1);
        }

        private void OnPageChipDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Handled = true;

            var moving = _dragSet.ToList();
            var index = sender is Border { Tag: int i } ? i : -1;

            EndDragVisuals();
            if (index >= 0) MoveTilesToPage(moving, index);
        }

        private void HighlightChip(int index)
        {
            if (index == _highlightedChip) return;
            _highlightedChip = index;

            for (var i = 0; i < _pageChips.Count; i++)
                ApplyTabVisual(_pageChips[i], isCurrent: i == _pageIndex, isHot: i == index);
        }

        /// <summary>把一组格子从当前页搬到另一页。</summary>
        private void MoveTilesToPage(IReadOnlyList<ItemViewModel> vms, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _pages.Count) return;
            if (vms.Count == 0) return;

            var target = _pages[pageIndex];

            if (pageIndex == _pageIndex)
            {
                SetStatus(vms.Count == 1
                    ? $"「{vms[0].Name}」已经在「{target.Name}」里了"
                    : $"这 {vms.Count} 项已经在「{target.Name}」里了");
                return;
            }

            var source = CurrentPage;
            if (source == null) return;

            var models = new List<ActionItem>(vms.Count);
            foreach (var vm in vms)
            {
                if (source.Items.Remove(vm.Model)) models.Add(vm.Model);
            }

            if (models.Count == 0)
            {
                SetStatus("没找到要移动的条目");
                return;
            }

            target.Items.AddRange(models);
            _config.Save();

            // 源页少了几个、目标页多了几个，两边的视图模型都要重建
            ReloadItems();
            ApplyFilter("");
            UpdatePageChrome();

            SetStatus($"已把 {models.Count} 项移到「{target.Name}」");
        }

        /// <summary>腰栏空白处：拖拽时这里是「放置区」，落空不算数。</summary>
        private void OnFooterDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnFooterDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Handled = true;

            // 页签模式下落在页签之间的空隙上：什么都不做。
            // 这里必须 Handled，否则会冒泡到窗口级被当成「移到末尾」。
            if (_dropModeActive) return;

            // 没有页签（只有一页）时腰栏还是个正常的落点，按「移到末尾」处理，
            // 免得面板底部凭空多出一条拖不进去的死区。
            var moving = _dragSet.ToList();
            var end = _view.Count;

            EndDragVisuals();
            MoveTiles(moving, end);
        }

        // ── 面板内的放置目标 ──

        private void OnSurfaceDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            UpdateDropIndicator(e.GetPosition(ItemHost));
            UpdateAutoScroll(e.GetPosition(ItemScroller));
        }

        private void OnSurfaceDragLeave(object sender, DragEventArgs e)
        {
            // DragLeave 在「从背景移到子元素上」时也会触发，
            // 所以得先确认指针真的出界了，否则指示条会一直闪。
            var p = e.GetPosition(DropSurface);
            if (p.X >= 0 && p.Y >= 0 && p.X <= DropSurface.ActualWidth && p.Y <= DropSurface.ActualHeight)
                return;

            DropMarker.Visibility = Visibility.Collapsed;
            StopAutoScroll();
        }

        private void OnSurfaceDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(TileDragFormat) || _dragSource == null) return;

            e.Handled = true;

            var moving = _dragSet.ToList();
            var index = ComputeInsertIndex(e.GetPosition(ItemHost));

            EndDragVisuals();
            MoveTiles(moving, index);
        }

        // ══════════════════════ 事件处理 ══════════════════════

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            var hasText = !string.IsNullOrEmpty(SearchBox.Text);

            SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

            ApplyFilter(SearchBox.Text);
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            FocusSearchBox();
            e.Handled = true;
        }

        private void FocusSearchBox()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            // 搜索框里有内容时，左右方向键属于文本编辑，不能抢。
            // 上下方向键没这个顾虑 —— 单行输入框本来就用不到。
            var editing = SearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(SearchBox.Text);

            switch (e.Key)
            {
                case Key.Escape:
                    // 有选中就先取消选择，再按一次才关面板 ——
                    // 否则「选到一半想收手」会直接把面板关掉，前面的选择全白费。
                    if (SelectedCount() > 0) ClearSelection();
                    else HidePanel();

                    e.Handled = true;
                    return;

                case Key.Enter:
                    // 运行键盘焦点所在的那一格。没移动过焦点时就是第一项，
                    // 和原来的行为完全一致。
                    LaunchItem(FocusedItem());
                    e.Handled = true;
                    return;

                case Key.Up:
                    MoveFocus(0, -1);
                    e.Handled = true;
                    return;

                case Key.Down:
                    MoveFocus(0, 1);
                    e.Handled = true;
                    return;

                case Key.Left when !editing:
                    MoveFocus(-1, 0);
                    e.Handled = true;
                    return;

                case Key.Right when !editing:
                    MoveFocus(1, 0);
                    e.Handled = true;
                    return;

                case Key.PageUp:
                    PrevPage();
                    e.Handled = true;
                    return;

                case Key.PageDown:
                    NextPage();
                    e.Handled = true;
                    return;

                case Key.F when ctrl:
                    // 从网格里快速回到输入
                    FocusSearchBox();
                    e.Handled = true;
                    return;

                // 下面两个只在搜索框为空时接管：框里有字时
                // Ctrl+A / Delete 属于文本编辑，不能抢。
                case Key.A when ctrl && SelectionEnabled:
                    SelectAllOnPage();
                    e.Handled = true;
                    return;

                case Key.Delete when SelectionEnabled && SelectedCount() > 0:
                    RemoveSelected();
                    e.Handled = true;
                    return;
            }

            // Ctrl+1..9 直接运行第 N 格。放在 switch 后面是因为 Key.D1..D9
            // 和 NumPad1..9 都是连续的，用算术映射比写 18 个 case 干净。
            if (!ctrl) return;

            var slot = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
                _ => 0,
            };

            if (slot <= 0 || slot > _view.Count) return;

            LaunchItem(_view[slot - 1]);
            e.Handled = true;
        }

        private void OnEmptyScanClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ImportFromStartMenu();
        }

        private void OnEmptyPickClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            // 搜索无结果、且那段文本看起来像网址时，Tag 里存着原文
            if (EmptyPickButton.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                AddItem(new ActionItem
                {
                    Name = ItemViewModel.DetectName(url),
                    Target = url,
                    Type = ItemType.Url,
                });
                return;
            }

            PickFiles();
        }

        /// <summary>
        /// 「＋ 添加」格。直接复用面板空白处那份右键菜单 ——
        /// 这样「怎么加东西」只有一套答案，不会出现两个入口行为不一致。
        /// </summary>
        private void OnAddTileClick(object sender, RoutedEventArgs e)
        {
            if (ContextMenu is not { } menu) return;

            menu.PlacementTarget = AddTile;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            if (_suppressDeactivate > 0) return;
            if (PinToggle.IsChecked == true) return;
            if (!_isShown) return;

            HidePanel();
        }

        private void OnItemClick(object sender, RoutedEventArgs e)
        {
            // 拖拽排序结束时按钮可能还会补一个 Click，这里吞掉，
            // 否则「挪个位置」会顺手把程序启动起来。
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }

            if (sender is not Button { Tag: ItemViewModel vm }) return;

            if (SelectionEnabled)
            {
                var modifiers = Keyboard.Modifiers;

                // Ctrl+点击 = 切换这一格的选中状态
                if ((modifiers & ModifierKeys.Control) != 0)
                {
                    ToggleSelection(vm);
                    return;
                }

                // Shift+点击 = 从锚点连选
                if ((modifiers & ModifierKeys.Shift) != 0)
                {
                    SelectRangeTo(vm);
                    return;
                }
            }

            // 普通点击仍然是「启动」。多选只走 Ctrl / Shift ——
            // 「点一下就启动」是这个工具的核心动作，不能被多选拖慢。
            if (SelectedCount() > 0) ClearSelection();

            LaunchItem(vm);
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            // 内部拖拽（格子排序）已经由 DropSurface 处理过了；
            // 万一指针在搜索框 / 腰栏上方，就由这里兜住，别让它变成「禁止」光标。
            if (e.Data.GetDataPresent(TileDragFormat))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DragOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
                DragOverlay.Visibility = Visibility.Collapsed;
            }

            e.Handled = true;
        }

        private void OnWindowDragLeave(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;

            // 松手的位置不在格子区域（搜索框、腰栏），一律当成「移到末尾」
            if (e.Data.GetDataPresent(TileDragFormat) && _dragSource != null)
            {
                var moving = _dragSet.ToList();
                var end = _view.Count;

                EndDragVisuals();
                MoveTiles(moving, end);

                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddPaths(paths);
            }

            e.Handled = true;
        }

        private void OnContextMenuOpened(object sender, RoutedEventArgs e) => _suppressDeactivate++;

        private void OnContextMenuClosed(object sender, RoutedEventArgs e)
        {
            _suppressDeactivate = Math.Max(0, _suppressDeactivate - 1);

            // 菜单关掉之后把键盘焦点抢回搜索框
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && _suppressDeactivate == 0)
                {
                    ForceForeground();
                    SearchBox.Focus();
                }
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// 格子右键菜单。选中多个时换成批量菜单 ——
        /// 选中 8 个格子却只能一个一个删，多选就没有意义了。
        /// </summary>
        private void OnItemMenuOpened(object sender, RoutedEventArgs e)
        {
            _suppressDeactivate++;

            if (sender is not ContextMenu menu) return;

            var vm = menu.PlacementTarget is FrameworkElement { DataContext: ItemViewModel target }
                ? target
                : null;

            menu.Items.Clear();
            if (vm == null) return;

            var selection = SelectedVms();

            // 右键落在选区里 → 批量菜单；否则退回单项菜单
            if (selection.Count > 1 && selection.Contains(vm))
                PopulateBatchMenu(menu.Items, selection);
            else
                PopulateItemMenu(menu.Items, vm);
        }

        private void PopulateItemMenu(ItemCollection items, ItemViewModel vm)
        {
            // 文本片段 / 按键没有「路径」可言。「打开所在位置」会退化为打开数据目录，
            // 「以管理员身份运行」会把一段文字当程序去启动 —— 全都莫名其妙。
            // 所以这两类走一套自己的菜单项。
            if (vm.Model.Type is ItemType.Text or ItemType.Keys)
            {
                var isText = vm.Model.Type == ItemType.Text;

                var send = new MenuItem
                {
                    Header = isText ? "发送到当前光标处" : "发送这组按键",
                };
                send.Click += (_, _) => LaunchItem(vm);

                var copyContent = new MenuItem
                {
                    Header = isText ? "复制这段文本" : "复制按键",
                };
                copyContent.Click += (_, _) => CopyPath(vm.Model.Target);

                var edit = new MenuItem { Header = isText ? "编辑文本…" : "编辑按键…" };
                edit.Click += (_, _) => EditSendable(vm);

                items.Add(send);
                items.Add(copyContent);
                items.Add(new Separator());
                items.Add(edit);
            }
            else
            {
                var open = new MenuItem { Header = "打开" };
                open.Click += (_, _) => LaunchItem(vm);

                var runAs = new MenuItem { Header = "以管理员身份运行" };
                runAs.Click += (_, _) => RunAsAdmin(vm);

                var reveal = new MenuItem { Header = "打开所在位置" };
                reveal.Click += (_, _) => ShellLauncher.RevealInExplorer(vm.Model.Target);

                var copy = new MenuItem { Header = "复制路径" };
                copy.Click += (_, _) => CopyPath(vm.Model.Target);

                items.Add(open);
                items.Add(runAs);
                items.Add(new Separator());
                items.Add(reveal);
                items.Add(copy);
            }

            var rename = new MenuItem { Header = "重命名…" };
            rename.Click += (_, _) => RenameItem(vm);

            var remove = new MenuItem { Header = "移除" };
            remove.Click += (_, _) => RemoveItem(vm);

            items.Add(new Separator());
            items.Add(rename);
            items.Add(remove);

            // 已经有选中、而右键点的却是选区外的格子时，顺手给一个「删选中的」入口
            var selection = SelectedVms();
            if (selection.Count == 0 || selection.Contains(vm)) return;

            items.Add(new Separator());
            var removeSelected = new MenuItem { Header = $"移除选中的 {selection.Count} 项" };
            removeSelected.Click += (_, _) => RemoveSelected();
            items.Add(removeSelected);
        }

        private void PopulateBatchMenu(ItemCollection items, List<ItemViewModel> selection)
        {
            var count = selection.Count;

            var remove = new MenuItem { Header = $"移除选中的 {count} 项" };
            remove.Click += (_, _) => RemoveSelected();
            items.Add(remove);

            var move = new MenuItem { Header = $"把选中的 {count} 项移到" };
            PopulateMoveToMenu(move.Items, selection);
            items.Add(move);

            items.Add(new Separator());

            var copyPaths = new MenuItem { Header = $"复制 {count} 个路径" };
            copyPaths.Click += (_, _) => CopyPaths(selection);
            items.Add(copyPaths);

            items.Add(new Separator());

            var clear = new MenuItem { Header = "取消选择" };
            clear.Click += (_, _) => ClearSelection();
            items.Add(clear);
        }

        /// <summary>「移到 → 某一页」子菜单。当前页不列（移到自己身上没有意义）。</summary>
        private void PopulateMoveToMenu(ItemCollection items, List<ItemViewModel> selection)
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                var index = i;
                var page = _pages[i];

                if (i == _pageIndex) continue;

                var header = page.Name;
                if (_sceneProcess != null && i < _scenePageCount) header += $"（{_sceneProcess}）";

                var entry = new MenuItem { Header = header };
                entry.Click += (_, _) => MoveTilesToPage(selection, index);
                items.Add(entry);
            }

            if (items.Count == 0)
                items.Add(new MenuItem { Header = "（没有别的动作页）", IsEnabled = false });
        }

        private void RunAsAdmin(ItemViewModel vm)
        {
            var original = vm.Model.RunAsAdmin;
            vm.Model.RunAsAdmin = true;
            LaunchItem(vm);
            vm.Model.RunAsAdmin = original;
        }

        private void CopyPath(string target)
        {
            try
            {
                Clipboard.SetText(target);
                SetStatus("路径已复制");
            }
            catch
            {
                SetStatus("复制失败");
            }
        }

        private void CopyPaths(List<ItemViewModel> vms)
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, vms.Select(v => v.Model.Target)));
                SetStatus($"已复制 {vms.Count} 个路径");
            }
            catch
            {
                SetStatus("复制失败");
            }
        }

        private void RenameItem(ItemViewModel vm)
        {
            WithSuppressedDeactivate(() =>
            {
                var name = InputDialog.Show(this, "重命名", "给这个条目起个名字：", vm.Name);
                if (name == null) return;

                vm.Name = name.Trim();
                _config.Save();
                ApplyFilter(SearchBox.Text);
            });
        }

        /// <summary>编辑文本片段的内容，或按键组合。</summary>
        private void EditSendable(ItemViewModel vm)
        {
            var isText = vm.Model.Type == ItemType.Text;

            WithSuppressedDeactivate(() =>
            {
                var value = InputDialog.Show(this,
                    isText ? "编辑文本片段" : "编辑按键",
                    isText
                        ? "点这个格子时会把下面的文字打到当前光标处："
                        : "支持 Ctrl / Alt / Shift / Win 组合，例如 Ctrl+Shift+T：",
                    vm.Model.Target,
                    multiline: isText);

                if (string.IsNullOrWhiteSpace(value)) return;

                if (!isText && !KeyComboParser.TryParse(value, out _, out var error))
                {
                    MessageBox.Show(error, "按键格式不对",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var old = vm.Model.Target;
                vm.Model.Target = value;
                vm.NotifyTargetChanged();

                // 名字只在「还是自动生成的那个」时才跟着改 ——
                // 用户已经自己起过名字的话，覆盖掉就是破坏他的输入。
                var autoName = isText
                    ? (SnippetName(old) == vm.Name)
                    : (old.Trim() == vm.Name);

                if (autoName) vm.Name = isText ? SnippetName(value) : value.Trim();

                _config.Save();
                SetStatus(isText ? "文本片段已更新" : $"按键已改为「{value.Trim()}」");
            });
        }

        private void RemoveItem(ItemViewModel vm)
        {
            WithSuppressedDeactivate(() =>
            {
                var answer = MessageBox.Show(
                    this,
                    $"确定要从面板移除「{vm.Name}」吗？\n\n（只会移除这个快捷方式，不会删除原文件）",
                    "QuickerLite",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.OK) return;

                // 搜索结果里点进来的条目可能来自别的页，所以全配置找一遍再删
                foreach (var page in ConfigStore.AllPages(_config.Current))
                {
                    if (page.Items.Remove(vm.Model)) break;
                }

                _config.Save();
                ReloadItems();
                ApplyFilter(SearchBox.Text);
                UpdatePageChrome();
            });
        }

        /// <summary>批量移除选中的格子。</summary>
        private void RemoveSelected()
        {
            var selection = SelectedVms();
            if (selection.Count == 0) return;

            WithSuppressedDeactivate(() =>
            {
                var answer = MessageBox.Show(
                    this,
                    $"确定要从面板移除选中的 {selection.Count} 项吗？\n\n（只会移除这些快捷方式，不会删除原文件）",
                    "QuickerLite",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.OK) return;

                // 先把模型抓出来：ReloadItems 之后这些视图模型就作废了
                var models = new HashSet<ActionItem>(selection.Select(v => v.Model));

                foreach (var page in ConfigStore.AllPages(_config.Current))
                    page.Items.RemoveAll(models.Contains);

                _config.Save();
                ReloadItems();
                ApplyFilter(SearchBox.Text);
                UpdatePageChrome();
                SetStatus($"已移除 {selection.Count} 项");
            });
        }

        private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

        public void OpenSettings()
        {
            WithSuppressedDeactivate(() =>
            {
                var dialog = new SettingsWindow(_config);

                // 面板不可见时（从托盘菜单进入）不设 Owner，否则对话框会跟着一起不显示
                if (IsVisible) dialog.Owner = this;

                dialog.ShowDialog();

                if (!dialog.Changed) return;

                ApplyColumns();
                ReloadItems();
                ApplyFilter(SearchBox.Text);
                UpdatePageChrome();

                if (dialog.HotkeyChanged)
                    HotkeySettingsChanged?.Invoke();

                if (dialog.InputChanged)
                    InputSettingsChanged?.Invoke();
            });
        }

        /// <summary>临时抑制「失焦隐藏」，并在结束后把焦点抢回面板。</summary>
        private void WithSuppressedDeactivate(Action action)
        {
            _suppressDeactivate++;
            try
            {
                action();
            }
            finally
            {
                _suppressDeactivate = Math.Max(0, _suppressDeactivate - 1);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (IsVisible && _suppressDeactivate == 0)
                    {
                        ForceForeground();
                        SearchBox.Focus();
                    }
                }), DispatcherPriority.Input);
            }
        }
    }
}
