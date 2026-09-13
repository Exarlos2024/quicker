using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickerLite.UI
{
    /// <summary>
    /// 一个极简的文本输入对话框。用代码构建而不是 XAML：
    /// 只有一个输入框，单文件更省事，也方便在任意位置复用（重命名、添加网址都用它）。
    /// </summary>
    internal static class InputDialog
    {
        /// <summary>
        /// </summary>
        /// <param name="multiline">
        /// 多行模式（文本片段用）。开启后回车是换行，确认要按 Ctrl+Enter 或点「确定」——
        /// 这是唯一的区别，但忘了处理的话用户会以为回车「没反应」。
        /// </param>
        public static string? Show(Window? owner, string title, string prompt, string initialValue,
            bool multiline = false)
        {
            var window = new Window
            {
                Title = title,
                Width = multiline ? 520 : 420,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Topmost = true,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
                Background = Frozen(Palette.Panel),
            };

            if (owner != null) window.Owner = owner;

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                FontSize = Metrics.FontBody,
                Foreground = Frozen(Palette.TextSecondary),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var textBox = new TextBox
            {
                Text = initialValue,
                FontSize = Metrics.FontBody,
                Padding = new Thickness(10, 7, 10, 7),
                Foreground = Frozen(Palette.TextPrimary),
                Background = Frozen(Palette.Fill),
                CaretBrush = Frozen(Palette.Accent500),
                BorderBrush = Frozen(Palette.BorderStrong),
                BorderThickness = new Thickness(1),
            };

            if (multiline)
            {
                textBox.AcceptsReturn = true;
                textBox.TextWrapping = TextWrapping.Wrap;
                textBox.Height = 150;
                textBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                textBox.VerticalContentAlignment = VerticalAlignment.Top;
                // 用等宽字体：文本片段里放代码片段的场合很多，对齐看得出来
                textBox.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            }

            textBox.SelectAll();
            root.Children.Add(textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            var cancel = new Button { Content = "取消", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0) };
            var ok = new Button { Content = "确定", MinWidth = 82, IsDefault = true };

            // 外观走全局令牌，别再各写一套 —— 这里原来是自己拼的灰白配色
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButtonStyle");
            ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButtonStyle");

            cancel.Click += (_, _) => { window.DialogResult = false; };
            ok.Click += (_, _) => { window.DialogResult = true; };

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            if (multiline)
            {
                // 多行时 Enter 是换行，所以给一个明确写出来的确认键
                var hint = new TextBlock
                {
                    Text = "Ctrl+Enter 确定",
                    FontSize = Metrics.FontCaption,
                    Foreground = Frozen(Palette.TextTertiary),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                buttons.Children.Insert(0, hint);
            }

            window.Content = root;

            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    window.DialogResult = false;
                    e.Handled = true;
                    return;
                }

                if (multiline && e.Key == Key.Enter &&
                    (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    window.DialogResult = true;
                    e.Handled = true;
                }
            };

            window.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

            var confirmed = window.ShowDialog();
            if (confirmed != true) return null;

            var result = textBox.Text.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
