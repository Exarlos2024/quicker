using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace QuickerLite.Models
{
    public enum ItemType
    {
        App,
        Folder,
        File,
        Url,
        Command,

        /// <summary>文本片段：点一下把 Target 里的文字打到当前光标处。</summary>
        Text,

        /// <summary>按键组合：Target 是 "Ctrl+Shift+T" 这样的字符串。</summary>
        Keys,
    }

    /// <summary>面板上的一个格子。</summary>
    public sealed class ActionItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";

        /// <summary>目标路径：exe / 文件夹 / 文档 / 网址 / 命令。</summary>
        public string Target { get; set; } = "";

        public ItemType Type { get; set; } = ItemType.File;

        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }

        /// <summary>true 时走 UAC 提权（ShellExecute 的 runas verb）。</summary>
        public bool RunAsAdmin { get; set; }

        /// <summary>搜索用的额外别名，比如给「微信」加 wechat / wx。</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>启动次数。用于给搜索结果排序，不改变网格里的位置
        /// —— 网格顺序一旦变了，肌肉记忆就废了。</summary>
        public int UseCount { get; set; }

        public DateTime? LastUsedUtc { get; set; }
    }

    /// <summary>
    /// 一页动作格子。
    /// 刻意不叫 Page —— WPF 里 System.Windows.Controls.Page 已经占了这个名字，
    /// 同名的后果是每个 UI 文件都得写别名，迟早有人漏掉。
    /// </summary>
    public sealed class ActionPage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "默认";
        public List<ActionItem> Items { get; set; } = new();

        public override string ToString() => Name;
    }

    public sealed class HotkeyConfig
    {
        /// <summary>MOD_ALT | MOD_CONTROL = 0x0003</summary>
        public uint Modifiers { get; set; } = 0x0001 | 0x0002;

        /// <summary>虚拟键码，0x51 = 'Q'</summary>
        public uint VirtualKey { get; set; } = 0x51;

        public string Display { get; set; } = "Ctrl+Alt+Q";
    }

    public sealed class AppConfig
    {
        public int Columns { get; set; } = 6;
        public HotkeyConfig Hotkey { get; set; } = new();
        public bool StartWithWindows { get; set; }

        // ───────── 唤出方式 ─────────

        /// <summary>单击 Ctrl 唤出 / 隐藏（需要低级键盘钩子）。</summary>
        public bool EnableSingleCtrl { get; set; } = true;

        /// <summary>单击鼠标中键唤出 / 隐藏（需要低级鼠标钩子）。</summary>
        public bool EnableMiddleClick { get; set; } = true;

        /// <summary>单击 Ctrl 的判定时间窗（毫秒）。超过这个时长算「按住」不算「单击」。</summary>
        public int SingleCtrlTimeoutMs { get; set; } = 400;

        // ───────── 动作页 ─────────

        /// <summary>全局面板区：不随前台程序切换，任何场景下都能翻到。</summary>
        public List<ActionPage> GlobalPages { get; set; } = new();

        /// <summary>
        /// 上下文场景：前台进程名（小写、不含 .exe）→ 该程序专用的动作页。
        /// 面板打开时会先看当前前台程序有没有场景，有就优先展示。
        /// </summary>
        public Dictionary<string, List<ActionPage>> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 旧版（单页扁平）结构。只用于从老配置迁移，迁移后置空不再写入。
        /// </summary>
        [JsonPropertyName("Items")]
        public List<ActionItem>? LegacyItems { get; set; }
    }

    internal static class HotkeyText
    {
        public static string Describe(uint modifiers, uint virtualKey)
        {
            var sb = new StringBuilder();
            if ((modifiers & 0x0002) != 0) sb.Append("Ctrl+");
            if ((modifiers & 0x0001) != 0) sb.Append("Alt+");
            if ((modifiers & 0x0004) != 0) sb.Append("Shift+");
            if ((modifiers & 0x0008) != 0) sb.Append("Win+");
            sb.Append(KeyName(virtualKey));
            return sb.ToString();
        }

        private static string KeyName(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
            switch (vk)
            {
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x1B: return "Esc";
                case 0xC0: return "`";
                case 0xBD: return "-";
                case 0xBB: return "=";
                case 0xDB: return "[";
                case 0xDD: return "]";
                case 0xDC: return "\\";
                case 0xBA: return ";";
                case 0xDE: return "'";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBF: return "/";
                default: return "0x" + vk.ToString("X2");
            }
        }
    }
}
