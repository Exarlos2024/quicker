using System;
using System.Collections.Generic;
using System.Windows;

namespace QuickerLite.Core
{
    /// <summary>
    /// 换行网格里「松手应该插到第几位」的计算。
    ///
    /// 抽成纯函数是为了能脱离界面单独验证 —— 这段下标数学是拖拽排序里最容易错的地方：
    /// 只按最近距离算的话，在行尾空白处松手会插到上一行去。
    /// </summary>
    public static class TileReorder
    {
        /// <summary>
        /// 由鼠标位置反推插入位置。
        ///
        /// bounds 要按格子的排布顺序给（WrapPanel 的规则：先从左到右，再从上到下）。
        /// 返回值范围是 [0, bounds.Count]，含义是「插到第几个格子前面」，
        /// 等于 Count 表示追加到末尾。
        /// </summary>
        public static int ComputeInsertIndex(IReadOnlyList<Rect> bounds, Point point)
        {
            var count = bounds.Count;
            if (count == 0) return 0;

            // ① 先按 Y 定位到行。格子等高，所以「落在某一行的纵向范围内」就是同一行。
            var rowStart = -1;
            var rowEnd = -1;

            for (var i = 0; i < count; i++)
            {
                var b = bounds[i];
                if (point.Y < b.Top || point.Y >= b.Bottom) continue;

                if (rowStart < 0) rowStart = i;
                rowEnd = i;
            }

            if (rowStart < 0)
            {
                // 不在任何一行里：比第一行还高就插到最前，否则插到最后。
                // 不能简单地「离谁近插谁」—— 那样在最后一行下方松手会插到行中间。
                return point.Y < bounds[0].Top ? 0 : count;
            }

            // ② 行内按水平中线比较：第一个中线在指针右边的格子，就是要插到它前面。
            for (var i = rowStart; i <= rowEnd; i++)
            {
                var b = bounds[i];
                if (point.X < b.Left + b.Width / 2) return i;
            }

            // 整行中线都在指针左边 → 插到这一行末尾
            return rowEnd + 1;
        }

        /// <summary>
        /// 把「一组要搬走的格子 + 插入点」换算成搬完之后这组格子在新列表里的起始下标。
        /// sortedFrom 必须是升序的原始下标。返回 -1 表示位置没变，不需要动。
        ///
        /// 跟单个格子是同一套道理：这组格子会先被摘出去，插入点前面每摘掉一个就前移一位。
        /// 但多选时要按「插入点之前被摘掉了几个」来算偏移，不能按选中的总数算 ——
        /// 这就是它比单格版本更容易写错的地方。
        ///
        /// 另外「没动」的判定也变严了：只有选区本来就连续、而且落回原位才算没动。
        /// 不连续的选区（比如第 1 个和第 9 个）即便起始下标相同，插进去也会改变顺序。
        /// </summary>
        public static int ResolveBlockMoveTarget(IReadOnlyList<int> sortedFrom, int insertIndex, int count)
        {
            if (count <= 0 || sortedFrom.Count == 0) return -1;

            for (var i = 0; i < sortedFrom.Count; i++)
            {
                if (sortedFrom[i] < 0 || sortedFrom[i] >= count) return -1;
            }

            var removedBefore = 0;
            for (var i = 0; i < sortedFrom.Count; i++)
            {
                if (sortedFrom[i] < insertIndex) removedBefore++;
            }

            // 全选就没有「搬到别处」可言了
            var remaining = count - sortedFrom.Count;
            if (remaining <= 0) return -1;

            var target = Math.Clamp(insertIndex - removedBefore, 0, remaining);

            var contiguous = true;
            for (var i = 1; i < sortedFrom.Count; i++)
            {
                if (sortedFrom[i] == sortedFrom[i - 1] + 1) continue;
                contiguous = false;
                break;
            }

            return contiguous && target == sortedFrom[0] ? -1 : target;
        }
    }
}
