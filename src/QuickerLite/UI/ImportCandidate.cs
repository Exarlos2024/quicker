using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using QuickerLite.Core;

namespace QuickerLite.UI
{
    /// <summary>批量导入列表里的一行。</summary>
    public sealed class ImportCandidate : INotifyPropertyChanged
    {
        public ShortcutEntry Entry { get; }

        /// <summary>面板里已经有指向同一个快捷方式的条目了。</summary>
        public bool AlreadyAdded { get; }

        public ImportCandidate(ShortcutEntry entry, bool alreadyAdded)
        {
            Entry = entry;
            AlreadyAdded = alreadyAdded;
        }

        public string Name => Entry.Name;

        public string Detail => AlreadyAdded ? $"{Entry.Detail}　·　已在面板里" : Entry.Detail;

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
