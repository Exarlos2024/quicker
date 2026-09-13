using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuickerLite.Core;

namespace QuickerLite.UI
{
    /// <summary>
    /// 从开始菜单 / 桌面批量导入快捷方式。
    ///
    /// 这个窗口刻意不显示图标。200 个快捷方式逐个 SHGetFileInfo，
    /// 只要有一个指向断开的网络共享就会阻塞几十秒 —— 而列表里靠名字和来源目录
    /// 已经足够辨识了。图标等导入进面板之后再异步提取，那时候是在图标工作线程上，
    /// 卡住也不影响任何界面。
    /// </summary>
    public partial class ImportWindow : Window
    {
        private readonly ObservableCollection<ImportCandidate> _view = new();
        private readonly List<ImportCandidate> _all = new();
        private readonly HashSet<string> _alreadyAdded;

        /// <summary>用户勾选并确认导入的条目。</summary>
        public IReadOnlyList<ShortcutEntry> Selected { get; private set; } = Array.Empty<ShortcutEntry>();

        public ImportWindow(IEnumerable<string> alreadyAddedTargets, string targetPageName)
        {
            _alreadyAdded = new HashSet<string>(alreadyAddedTargets, StringComparer.OrdinalIgnoreCase);

            InitializeComponent();

            CandidateList.ItemsSource = _view;
            TargetText.Text = $"导入到动作页「{targetPageName}」";

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 根目录先在 UI 线程解析好，再丢给后台线程去扫 ——
            // Known Folder API 有线程亲和性方面的讲究，而 Scan 本身保持纯函数。
            var roots = ShortcutScanner.DefaultRoots();

            List<ShortcutEntry> entries;

            try
            {
                // 扫描放到后台线程：目录枚举碰上网络路径或坏联接会慢到肉眼可见
                entries = await Task.Run(() => ShortcutScanner.Scan(roots));
            }
            catch (Exception ex)
            {
                IntroText.Text = $"扫描失败：{ex.Message}";
                return;
            }

            foreach (var entry in entries)
            {
                var candidate = new ImportCandidate(entry, _alreadyAdded.Contains(entry.Path));

                // 勾选状态一变就要刷新底部计数
                candidate.PropertyChanged += OnCandidateChanged;
                _all.Add(candidate);
            }

            ApplyFilter("");

            var fresh = _all.Count(c => !c.AlreadyAdded);

            IntroText.Text = _all.Count == 0
                ? "没找到任何快捷方式。开始菜单目录可能是空的，或者没有读取权限。"
                : $"找到 {_all.Count} 个快捷方式，其中 {fresh} 个还没加进面板。" +
                  "导入的是快捷方式本身（.lnk），不解析它的目标路径。";
        }

        private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImportCandidate.IsSelected))
                UpdateCount();
        }

        // ══════════════════════ 过滤 ══════════════════════

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            ApplyFilter(SearchBox.Text);
        }

        private void ApplyFilter(string? query)
        {
            var text = (query ?? "").Trim();

            _view.Clear();

            foreach (var candidate in _all)
            {
                if (text.Length == 0 || Matches(candidate, text))
                    _view.Add(candidate);
            }

            UpdateCount();
        }

        private static bool Matches(ImportCandidate candidate, string query)
            => candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || SearchRanker.IsSubsequence(candidate.Name, query)
               || candidate.Entry.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);

        private void UpdateCount()
        {
            var selected = _all.Count(c => c.IsSelected);

            CountText.Text = $"已选 {selected} 项　·　当前显示 {_view.Count} 项";
            ImportButton.Content = selected > 0 ? $"导入 {selected} 项" : "导入";
            ImportButton.IsEnabled = selected > 0;
        }

        // ══════════════════════ 批量勾选 ══════════════════════

        /// <summary>批量操作只作用于「当前显示的」行 —— 配合过滤框先缩小范围再全选，
        /// 比在一堆条目里逐个点要快得多。</summary>
        private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetSelectionOnView(true);

        private void OnSelectNoneClick(object sender, RoutedEventArgs e) => SetSelectionOnView(false);

        private void OnInvertClick(object sender, RoutedEventArgs e)
        {
            foreach (var candidate in _view.ToList())
                candidate.IsSelected = !candidate.IsSelected;
        }

        private void SetSelectionOnView(bool selected)
        {
            foreach (var candidate in _view.ToList())
                candidate.IsSelected = selected;
        }

        // ══════════════════════ 确认 ══════════════════════

        private void OnImportClick(object sender, RoutedEventArgs e)
        {
            Selected = _all.Where(c => c.IsSelected).Select(c => c.Entry).ToList();

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
