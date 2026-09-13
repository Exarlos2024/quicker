using System.Windows;
using System.Windows.Media;
using QuickerLite.Models;

namespace QuickerLite.UI
{
    /// <summary>
    /// 给「文本片段」和「按键」这两类格子画图标。
    ///
    /// 为什么不能走 IconService：那两条路都是 SHGetFileInfo，需要一个真实的文件路径。
    /// 而这两类的 Target 是一段文字 / 一个按键串，传进去只会拿到空图标 ——
    /// 界面上表现为一个空荡荡的方框，用户分不清是「没图标」还是「坏了」。
    ///
    /// 用矢量而不是位图：只有两种形状，画出来比内置资源省事，也不会有 DPI 模糊。
    /// 刻意不画文字或符号 —— 字形依赖字体，换台机器可能就缺字变成豆腐块。
    /// </summary>
    internal static class ItemGlyph
    {
        public static ImageSource? For(ItemType type)
        {
            return type switch
            {
                ItemType.Text => Build(TextLines()),
                ItemType.Keys => Build(KeyCaps()),
                _ => null,
            };
        }

        /// <summary>三道横线 = 一段文字。</summary>
        private static DrawingGroup TextLines()
        {
            return new DrawingGroup
            {
                Children = new DrawingCollection
                {
                    Bar(4, 6, 16),
                    Bar(4, 11, 16),
                    Bar(4, 16, 10),
                },
            };
        }

        /// <summary>三颗键帽 = 键盘按键。</summary>
        private static DrawingGroup KeyCaps()
        {
            return new DrawingGroup
            {
                Children = new DrawingCollection
                {
                    Cap(3, 9),
                    Cap(9.5, 9),
                    Cap(16, 9),
                },
            };
        }

        private static GeometryDrawing Bar(double x, double y, double width)
        {
            return new GeometryDrawing
            {
                Brush = Ink,
                Geometry = new RectangleGeometry(new Rect(x, y, width, 2), 1, 1),
            };
        }

        private static GeometryDrawing Cap(double x, double y)
        {
            return new GeometryDrawing
            {
                Brush = Ink,
                Geometry = new RectangleGeometry(new Rect(x, y, 5, 6), 1.2, 1.2),
            };
        }

        /// <summary>中性灰。彩色留给用户自己的程序图标，这两类应该显得「不是程序」。
        /// 用 6.00:1 那一档而不是更浅的辅助灰 —— 图形面积比正文小，需要更强的对比。</summary>
        private static readonly Brush Ink = Frozen(Palette.TextSecondary);

        private static ImageSource Build(DrawingGroup group)
        {
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
