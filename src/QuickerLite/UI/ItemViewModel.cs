using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using QuickerLite.Models;

namespace QuickerLite.UI
{
    /// <summary>
    /// 面板格子的视图模型。包一层的原因：图标是异步提取的，
    /// 需要在提取完成后通知界面刷新，而 ActionItem 本身是纯数据模型。
    /// </summary>
    public sealed class ItemViewModel : INotifyPropertyChanged
    {
        public ActionItem Model { get; }

        public ItemViewModel(ActionItem model)
        {
            Model = model;
        }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; Raise(); }
        }

        private bool _isSelected;

        /// <summary>
        /// 多选状态。放在视图模型上，而不是在窗口里另存一个选中集合 ——
        /// 集合需要在每次重建视图模型时重新对齐，而这个字段天然跟着格子走。
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise();
                Raise(nameof(SelectionVisibility));
            }
        }

        /// <summary>选中高亮的显隐。做成属性而不是转换器，省掉一个要注册的资源。</summary>
        public Visibility SelectionVisibility =>
            _isSelected ? Visibility.Visible : Visibility.Collapsed;

        private bool _isFocusCursor;

        /// <summary>
        /// 键盘焦点光标（方向键移动的那一格）。
        ///
        /// 刻意不复用 WPF 的键盘焦点：格子按钮是 Focusable=False，真正的焦点必须留在
        /// 搜索框里，否则用户一按方向键就打不了字。所以这里和 IsSelected 一样，
        /// 只是视图模型上的一个状态位，由窗口维护 _focusIndex 来同步。
        /// </summary>
        public bool IsFocusCursor
        {
            get => _isFocusCursor;
            set
            {
                if (_isFocusCursor == value) return;
                _isFocusCursor = value;
                Raise();
            }
        }

        private string _indexText = "";

        /// <summary>左上角的数字角标。只有前 9 格有值，对应 Ctrl+1..9。</summary>
        public string IndexText
        {
            get => _indexText;
            set
            {
                if (_indexText == value) return;
                _indexText = value;
                Raise();
                Raise(nameof(IndexVisibility));
            }
        }

        public Visibility IndexVisibility =>
            string.IsNullOrEmpty(_indexText) ? Visibility.Collapsed : Visibility.Visible;

        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name == value) return;
                Model.Name = value;
                Raise();
                Raise(nameof(TooltipText));
            }
        }

        public string TooltipText
        {
            get
            {
                var type = Model.Type switch
                {
                    ItemType.App => "程序",
                    ItemType.Folder => "文件夹",
                    ItemType.File => "文件",
                    ItemType.Url => "链接",
                    ItemType.Command => "命令",
                    ItemType.Text => "文本片段",
                    ItemType.Keys => "按键",
                    _ => "项目",
                };
                var text = $"{Model.Name}\n{Model.Target}\n类型：{type}";

                // 这两类的「单击」做什么不直观，直接写进提示里 ——
                // 否则用户只能靠试，而试错一次就是面板消失一次
                return Model.Type switch
                {
                    ItemType.Text => text + "\n单击复制到剪贴板（右键可直接发送到光标处）",
                    ItemType.Keys => text + "\n单击发给原来的窗口",
                    _ => text,
                };
            }
        }

        public bool IsDirectory => Model.Type == ItemType.Folder;

        /// <summary>
        /// 目标内容变了（编辑文本片段 / 按键之后）用来刷新提示文字。
        /// Target 没有对应的包装属性，所以只能外部显式通知一次。
        /// </summary>
        public void NotifyTargetChanged() => Raise(nameof(TooltipText));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>根据路径推断条目类型。</summary>
        public static ItemType DetectType(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ItemType.File;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ItemType.Url;

            if (Directory.Exists(path)) return ItemType.Folder;

            var ext = "";
            try { ext = Path.GetExtension(path); } catch { }

            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                return ItemType.App;

            return ItemType.File;
        }

        /// <summary>从路径推出一个像样的显示名。</summary>
        public static string DetectName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(未命名)";

            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(path).Host; } catch { return path; }
            }

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                // 形如 "C:\" 的根目录，GetFileNameWithoutExtension 会返回空
                name = path.TrimEnd('\\', '/');
                var idx = name.LastIndexOf('\\');
                if (idx >= 0) name = name.Substring(idx + 1);
            }

            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }
}
