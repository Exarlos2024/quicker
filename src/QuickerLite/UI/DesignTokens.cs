using System.Windows.Media;

namespace QuickerLite.UI
{
    /// <summary>
    /// 设计令牌的唯一来源。
    ///
    /// 颜色放在 C# 而不是 XAML 里，是因为有两类消费者：Themes/Tokens.xaml 里的
    /// 画刷，以及运行时动态生成的元素（页签、拖拽提示条、拖拽页签）。
    /// 两边各写一份十六进制必然会漂移，所以 XAML 用 {x:Static} 反过来引用这里。
    /// </summary>
    public static class Palette
    {
        // ── 中性色阶 ──
        public static readonly Color Panel = Color.FromRgb(0xFF, 0xFF, 0xFF);
        public static readonly Color Page = Color.FromRgb(0xF7, 0xF8, 0xFA);
        public static readonly Color Fill = Color.FromRgb(0xF2, 0xF3, 0xF5);
        public static readonly Color Pressed = Color.FromRgb(0xE7, 0xEA, 0xEE);
        public static readonly Color Hover = Color.FromRgb(0xF5, 0xF7, 0xFA);
        public static readonly Color BorderSoft = Color.FromRgb(0xE3, 0xE7, 0xED);
        public static readonly Color BorderStrong = Color.FromRgb(0xD3, 0xD5, 0xDA);

        // ── 文字。对比度按 WCAG 2.1 实算，白底 ──
        /// <summary>15.8:1</summary>
        public static readonly Color TextPrimary = Color.FromRgb(0x1F, 0x23, 0x29);
        /// <summary>6.00:1</summary>
        public static readonly Color TextSecondary = Color.FromRgb(0x5A, 0x64, 0x72);
        /// <summary>4.83:1 —— 正文的达标下限，不要再往下调</summary>
        public static readonly Color TextTertiary = Color.FromRgb(0x6B, 0x72, 0x80);
        /// <summary>仅用于禁用态，不参与对比度要求</summary>
        public static readonly Color TextDisabled = Color.FromRgb(0xC9, 0xCD, 0xD3);

        // ── 强调色 ──
        /// <summary>键盘焦点底</summary>
        public static readonly Color Accent50 = Color.FromRgb(0xEF, 0xF4, 0xFE);
        /// <summary>多选选中底、当前页签</summary>
        public static readonly Color Accent100 = Color.FromRgb(0xDC, 0xE7, 0xFD);
        /// <summary>描边环、勾选角标。白底 4.57:1，刚好过 AA</summary>
        public static readonly Color Accent500 = Color.FromRgb(0x2F, 0x6F, 0xEB);
        /// <summary>淡蓝底上的文字</summary>
        public static readonly Color Accent700 = Color.FromRgb(0x1F, 0x5F, 0xD8);

        // ── 语义色（仅用于设置窗口的校验提示）──
        public static readonly Color Danger = Color.FromRgb(0xB4, 0x23, 0x18);
    }

    /// <summary>
    /// 尺寸与时长令牌。XAML 通过 {x:Static} 引用，代码里直接用。
    /// 所有数值都从 4px 基数推导。
    /// </summary>
    public static class Metrics
    {
        // ── 格子 ──
        /// <summary>格子槽位宽。6 列 = 552，正好铺满面板内容区</summary>
        public const double TileSlotWidth = 92;
        /// <summary>格子槽位高</summary>
        public const double TileSlotHeight = 100;
        /// <summary>格子本体宽（槽位内缩 4）</summary>
        public const double TileWidth = 84;
        /// <summary>格子本体高</summary>
        public const double TileHeight = 92;
        public const double TileMargin = 4;
        public const double TileRadius = 10;

        /// <summary>图标容器边长。透明底 PNG 也能看清，是网格韵律的来源</summary>
        public const double IconBox = 44;
        public const double IconBoxRadius = 11;
        /// <summary>图标实际绘制区，容器内留 6 内边距</summary>
        public const double IconGlyph = 32;

        public const double BadgeSize = 15;
        public const double BadgeRadius = 5;

        // ── 面板骨架 ──
        /// <summary>固定 3 行。改这个值就等于改面板高度，别让它跟着内容走</summary>
        public const int VisibleRows = 3;
        /// <summary>顶部命令栏：上 10 + 输入框 32 + 下 4</summary>
        public const double HeaderHeight = 46;
        /// <summary>底部腰栏：上下各 5 + 页签 26，另加 8 撑到 44</summary>
        public const double FooterHeight = 44;
        /// <summary>根 Border 上下各 1px</summary>
        public const double PanelBorder = 2;
        public const double SidePadding = 12;
        public const double PanelRadius = 14;
        public const double ControlRadius = 6;
        public const double InputRadius = 9;
        public const double TabHeight = 26;
        public const double TabRadius = 13;
        public const double InputHeight = 32;

        // ── 字阶。只用 400 / 500 两档字重 ──
        public const double FontCaption = 11;
        public const double FontLabel = 12;
        public const double FontBody = 13;
        public const double FontTitle = 15;
        public const double FontHeading = 17;

        // ── 动效时长（毫秒）──
        public const double MotionHoverMs = 100;
        public const double MotionSelectMs = 120;
        public const double MotionPanelMs = 110;
    }
}
