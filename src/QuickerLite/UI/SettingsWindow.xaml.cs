using System;
using System.Windows;
using System.Windows.Input;
using QuickerLite.Core;
using QuickerLite.Models;

namespace QuickerLite.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly ConfigStore _config;

        private uint _modifiers;
        private uint _virtualKey;
        private bool _loading = true;

        /// <summary>用户是否点了保存并产生了实际改动。</summary>
        public bool Changed { get; private set; }

        /// <summary>热键是否发生了变化 —— 只有变了才需要重新注册。</summary>
        public bool HotkeyChanged { get; private set; }

        /// <summary>唤出方式（单击 Ctrl / 中键 / 时间窗）是否变了 —— 变了才需要重配输入钩子。</summary>
        public bool InputChanged { get; private set; }

        public SettingsWindow(ConfigStore config)
        {
            _config = config;

            InitializeComponent();

            var hotkey = _config.Current.Hotkey;
            _modifiers = hotkey.Modifiers;
            _virtualKey = hotkey.VirtualKey;

            HotkeyBox.Text = HotkeyText.Describe(_modifiers, _virtualKey);
            ColumnSlider.Value = Math.Clamp(_config.Current.Columns, 3, 10);
            ColumnValue.Text = ((int)ColumnSlider.Value).ToString();
            StartupCheck.IsChecked = StartupManager.IsEnabled();

            CtrlTapCheck.IsChecked = _config.Current.EnableSingleCtrl;
            MiddleClickCheck.IsChecked = _config.Current.EnableMiddleClick;
            CtrlTimeoutSlider.Value = Math.Clamp(_config.Current.SingleCtrlTimeoutMs, 150, 800);
            CtrlTimeoutValue.Text = $"{(int)CtrlTimeoutSlider.Value} ms";

            DataPathText.Text = $"配置文件：{ConfigStore.ConfigPath}";

            _loading = false;
        }

        // ══════════════════════ 热键录制 ══════════════════════

        private void OnHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HotkeyHint.Text = "现在按下想要的组合键…";
            HotkeyHint.Foreground = System.Windows.Media.Brushes.Gray;
            HotkeyHint.Visibility = Visibility.Visible;
        }

        private void OnHotkeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            // Alt 组合键在 WPF 里会变成 SystemKey，要取真正的那个键
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // 只按下修饰键本身时不算一个完整组合，忽略
            if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin
                or Key.None)
            {
                return;
            }

            var pressed = Keyboard.Modifiers;
            if (pressed == ModifierKeys.None)
            {
                ShowHint("必须包含至少一个修饰键（Ctrl / Alt / Shift / Win）", isError: true);
                return;
            }

            uint modifiers = 0;
            if (pressed.HasFlag(ModifierKeys.Control)) modifiers |= 0x0002;
            if (pressed.HasFlag(ModifierKeys.Alt)) modifiers |= 0x0001;
            if (pressed.HasFlag(ModifierKeys.Shift)) modifiers |= 0x0004;
            if (pressed.HasFlag(ModifierKeys.Windows)) modifiers |= 0x0008;

            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
            {
                ShowHint("这个键不支持，请换一个", isError: true);
                return;
            }

            _modifiers = modifiers;
            _virtualKey = virtualKey;

            HotkeyBox.Text = HotkeyText.Describe(modifiers, virtualKey);
            ShowHint("已记录。点「保存」后生效。", isError: false);
        }

        private void ShowHint(string text, bool isError)
        {
            HotkeyHint.Text = text;
            HotkeyHint.Foreground = isError
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB4, 0x23, 0x18))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x7F, 0x47));
            HotkeyHint.Visibility = Visibility.Visible;
        }

        // ══════════════════════ 其它控件 ══════════════════════

        private void OnColumnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ColumnValue == null) return;
            ColumnValue.Text = ((int)e.NewValue).ToString();
        }

        private void OnCtrlTimeoutChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CtrlTimeoutValue == null) return;
            CtrlTimeoutValue.Text = $"{(int)e.NewValue} ms";
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            HotkeyChanged =
                _modifiers != _config.Current.Hotkey.Modifiers ||
                _virtualKey != _config.Current.Hotkey.VirtualKey;

            var ctrlTap = CtrlTapCheck.IsChecked == true;
            var middleClick = MiddleClickCheck.IsChecked == true;
            var timeout = (int)CtrlTimeoutSlider.Value;

            InputChanged =
                ctrlTap != _config.Current.EnableSingleCtrl ||
                middleClick != _config.Current.EnableMiddleClick ||
                timeout != _config.Current.SingleCtrlTimeoutMs;

            _config.Current.Hotkey.Modifiers = _modifiers;
            _config.Current.Hotkey.VirtualKey = _virtualKey;
            _config.Current.Hotkey.Display = HotkeyText.Describe(_modifiers, _virtualKey);

            _config.Current.Columns = (int)ColumnSlider.Value;

            _config.Current.EnableSingleCtrl = ctrlTap;
            _config.Current.EnableMiddleClick = middleClick;
            _config.Current.SingleCtrlTimeoutMs = timeout;

            var startWithWindows = StartupCheck.IsChecked == true;
            if (startWithWindows != StartupManager.IsEnabled())
                StartupManager.SetEnabled(startWithWindows);

            _config.Current.StartWithWindows = startWithWindows;

            _config.Save();

            Changed = true;

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
