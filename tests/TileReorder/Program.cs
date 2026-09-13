using System;
using System.Collections.Generic;
using System.Windows;
using QuickerLite.Core;

namespace QuickerLite.Tests
{
    /// <summary>
    /// TileReorder 的断言用例。用 `./test.sh` 跑，失败时退出码为 1。
    ///
    /// 覆盖的都是「看代码看不出来、写错了也不会崩、只会用起来别扭」的边界：
    /// 行尾空白处松手、行边界的归属、网格外的上下方、往右拖的下标偏移。
    /// </summary>
    internal static class Program
    {
        private static int _failed;
        private static int _total;

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // 6 列 × 3 行（14 个格子），格子尺寸跟面板里的 WrapPanel 一致：92 × 96
            var bounds = new List<Rect>();
            for (var i = 0; i < 14; i++)
            {
                bounds.Add(new Rect(
                    (i % 6) * 92,
                    (i / 6) * 96,
                    92,
                    96));
            }

            Console.WriteLine("=== ComputeInsertIndex ===");

            // 行 0：格子 0-5，中线分别在 x = 46, 138, 230, 322, 414, 506
            Check("行0 最左侧", 0, bounds, 10, 10);
            Check("行0 跨过格子0中线", 1, bounds, 80, 10);
            Check("行0 正好压在中线上（算跨过，插到后面）", 1, bounds, 46, 10);
            Check("行0 刚过中线一点点", 1, bounds, 46.1, 10);
            Check("行0 行尾空白处（应落到本行末尾）", 6, bounds, 547, 10);

            // 行 1：格子 6-11，y 范围 96-191
            Check("行1 最左侧", 6, bounds, 0, 100);
            Check("行1 落在格子10/11之间", 11, bounds, 500, 100);
            Check("行边界 y=96 属于行1", 6, bounds, 0, 96);
            Check("行边界 y=95.9 属于行0", 0, bounds, 0, 95.9);

            // 行 2：格子 12-13，只有两个
            Check("行2 最左侧", 12, bounds, 0, 200);
            Check("行2 两个格子之间", 13, bounds, 100, 200);
            Check("行2 行尾空白处（追加到末尾）", 14, bounds, 500, 200);

            // 整个网格之外
            Check("网格上方", 0, bounds, 0, -50);
            Check("网格下方（追加到末尾）", 14, bounds, 0, 500);
            Check("空网格", 0, new List<Rect>(), 10, 10);

            Console.WriteLine();
            Console.WriteLine("=== ResolveBlockMoveTarget：单格 ===");

            // 往右拖：被拖的格子先被摘出去，目标下标要前移一位
            Move("原地不动（插到自己前面）", -1, 3, 3, 10);
            Move("插到自己后面 = 没动", -1, 3, 4, 10);
            Move("往右挪一位", 4, 3, 5, 10);
            Move("往右挪到末尾", 9, 3, 10, 10);

            // 往左拖：下标不需要调整
            Move("往左挪到最前", 0, 3, 0, 10);
            Move("往左挪一位", 2, 3, 3 - 1, 10);

            // 已经到边界的两种情况
            Move("已经在最前，再插到最前", -1, 0, 0, 10);
            Move("已经在最前，插到第二位前面", -1, 0, 1, 10);
            Move("已经在最前，插到第三位前面", 1, 0, 2, 10);
            Move("已经在最后，追加到末尾", -1, 9, 10, 10);

            // 只有一格
            Move("只有一格", -1, 0, 0, 1);
            Move("只有一格，插到末尾", -1, 0, 1, 1);

            // 非法输入
            Move("from 越界", -1, 99, 3, 10);
            Move("空列表", -1, 0, 0, 0);

            Console.WriteLine();
            Console.WriteLine("=== ResolveBlockMoveTarget：多选 ===");

            // 连续选区：道理跟单格一样，但偏移量按「插入点之前摘掉了几个」算
            MoveBlock("连续块 往右搬", 5, new[] { 3, 4 }, 7, 10);
            MoveBlock("连续块 插到自己前面 = 没动", -1, new[] { 3, 4 }, 3, 10);
            MoveBlock("连续块 插到自己中间 = 没动", -1, new[] { 3, 4 }, 4, 10);
            MoveBlock("连续块 插到自己后面 = 没动", -1, new[] { 3, 4 }, 5, 10);
            MoveBlock("连续块 再往右一格", 4, new[] { 3, 4 }, 6, 10);
            MoveBlock("三连块 插到自己后面 = 没动", -1, new[] { 1, 2, 3 }, 4, 10);
            MoveBlock("三连块 往右搬", 2, new[] { 1, 2, 3 }, 5, 10);
            MoveBlock("连续块 搬到最前", 0, new[] { 8, 9 }, 0, 10);

            // 不连续选区：偏移量只数插入点之前的那几个，按总数算就会偏
            MoveBlock("不连续 {0,9} 往中间搬", 4, new[] { 0, 9 }, 5, 10);
            MoveBlock("不连续 {0,9} 搬到最前", 0, new[] { 0, 9 }, 0, 10);
            MoveBlock("不连续 {1,5} 搬到最前", 0, new[] { 1, 5 }, 0, 10);

            // 边界
            MoveBlock("全选 = 没动", -1, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0, 10);
            MoveBlock("单元素走同一条路", 4, new[] { 3 }, 5, 10);
            MoveBlock("下标越界", -1, new[] { 99 }, 3, 10);
            MoveBlock("空选区", -1, new int[0], 3, 10);

            Console.WriteLine();
            Console.WriteLine("=== KeyComboParser ===");

            // 基本组合。修饰键顺序不要求，大小写不敏感
            KeyOk("Ctrl+Shift+T", "Ctrl+Shift+T", 0x54, ctrl: true, shift: true);
            KeyOk("小写也认", "ctrl+shift+t", 0x54, ctrl: true, shift: true);
            KeyOk("四个修饰键全上", "Ctrl+Alt+Shift+Win+A", 0x41,
                ctrl: true, alt: true, shift: true, win: true);
            KeyOk("Ctrl+Alt+Delete", "Ctrl+Alt+Delete", 0x2E, ctrl: true, alt: true);

            // 单键（没有修饰键）
            KeyOk("单个字母", "S", 0x53);
            KeyOk("小写字母", "s", 0x53);
            KeyOk("数字键", "5", 0x35);
            KeyOk("F5", "F5", 0x74);
            KeyOk("Win 单独出现时是键不是修饰键", "Win", 0x5B);

            // 命名键与别名
            KeyOk("Alt+Tab", "Alt+Tab", 0x09, alt: true);
            KeyOk("Ctrl+Enter", "Ctrl+Enter", 0x0D, ctrl: true);
            KeyOk("return 是 enter 的别名", "return", 0x0D);
            KeyOk("esc 别名", "esc", 0x1B);
            KeyOk("escape 全称", "escape", 0x1B);
            KeyOk("PageUp", "pageup", 0x21);
            KeyOk("prtsc 别名", "prtsc", 0x2C);
            KeyOk("Ctrl+Home", "Ctrl+Home", 0x24, ctrl: true);

            // F 键的边界：只有 F1..F24
            KeyOk("F1 下界", "F1", 0x70);
            KeyOk("F24 上界", "F24", 0x87);
            KeyFail("F25 越界", "F25");
            KeyFail("F0 越界", "F0");

            // 格式错误。这些才是真正会咬人的：写错了界面上没有任何提示
            KeyFail("空串", "");
            KeyFail("只有空格", "   ");
            KeyFail("结尾多一个加号", "Ctrl+");
            KeyFail("开头多一个加号", "+S");
            KeyFail("中间空一段", "Ctrl++S");
            KeyFail("重复 Ctrl", "Ctrl+Ctrl+S");
            KeyFail("重复 Shift", "Shift+S+Shift");
            KeyFail("不认识的修饰键", "Hyper+S");
            KeyFail("不认识的键", "Ctrl+Foo");
            KeyFail("标点键不支持", "Ctrl+;");

            Console.WriteLine();
            Console.WriteLine($"断言总数：{_total}　" + (_failed == 0
                ? "全部通过"
                : $"失败 {_failed} 项"));

            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string label, int expected, List<Rect> bounds, double x, double y)
        {
            var actual = TileReorder.ComputeInsertIndex(bounds, new Point(x, y));
            Report(label, expected, actual);
        }

        private static void Move(string label, int expected, int from, int insertIndex, int count)
        {
            var actual = TileReorder.ResolveBlockMoveTarget(new[] { from }, insertIndex, count);
            Report(label, expected, actual);
        }

        private static void MoveBlock(string label, int expected, int[] from, int insertIndex, int count)
        {
            var actual = TileReorder.ResolveBlockMoveTarget(from, insertIndex, count);
            Report(label, expected, actual);
        }

        private static void KeyOk(string label, string input, int vk,
            bool ctrl = false, bool alt = false, bool shift = false, bool win = false)
        {
            var parsed = KeyComboParser.TryParse(input, out var c, out var error);
            var match = parsed
                        && c.Vk == vk
                        && c.Ctrl == ctrl
                        && c.Alt == alt
                        && c.Shift == shift
                        && c.Win == win;

            Report(label, true, match, parsed ? null : error);
        }

        private static void KeyFail(string label, string input)
        {
            var parsed = KeyComboParser.TryParse(input, out _, out _);
            Report(label, false, parsed);
        }

        private static void Report(string label, int expected, int actual)
        {
            _total++;
            var ok = expected == actual;
            if (!ok) _failed++;

            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label,-34} 期望={expected,3}  实际={actual,3}");
        }

        private static void Report(string label, bool expected, bool actual, string? detail = null)
        {
            _total++;
            var ok = expected == actual;
            if (!ok) _failed++;

            var extra = detail == null ? "" : $"  {detail}";
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label,-34} 期望={expected,-5} 实际={actual,-5}{extra}");
        }
    }
}
