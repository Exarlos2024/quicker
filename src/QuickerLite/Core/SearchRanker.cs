using System;
using QuickerLite.Models;

namespace QuickerLite.Core
{
    /// <summary>
    /// 搜索匹配与排序。
    ///
    /// 这里有一个刻意的取舍，值得写下来：<b>使用频次只用来「打平手」，
    /// 绝不参与决定网格顺序。</b>
    ///
    /// 网格一旦按频次重排，用户练出来的肌肉记忆就废了 —— 这类工具最忌讳这个。
    /// 「第三个格子是浏览器」这种记忆一旦被打破，用户每次都要重新找，
    /// 那还不如用搜索框。所以频次只影响<b>搜索结果</b>的前后顺序，
    /// 而搜索结果本来就是一个临时的、每次都不同的列表。
    ///
    /// 实现方式是把得分和频次合成一个 long：得分乘以 1000 后叠加频次，
    /// 频次上限 999，因此<b>永远越不过得分档的边界</b>。
    /// </summary>
    public static class SearchRanker
    {
        public const int NoMatch = 0;

        // 得分档。数值本身不重要，重要的是档与档之间的相对高低。
        private const int ExactName = 1000;      // 名字完全等于输入
        private const int NamePrefix = 800;      // 名字以输入开头
        private const int NameContains = 700;    // 名字里含输入
        private const int KeywordContains = 600; // 别名里含输入
        private const int KeywordFuzzy = 500;    // 别名子序列命中
        private const int TargetContains = 400;  // 路径里含输入
        private const int NameFuzzy = 300;       // 名字子序列命中

        /// <summary>匹配得分，0 表示不匹配。分数越高越靠前。</summary>
        public static int Score(ActionItem item, string query)
        {
            if (string.IsNullOrEmpty(query)) return NoMatch;

            var name = item.Name ?? "";
            var target = item.Target ?? "";

            // 按档位从高到低依次尝试，命中就返回。
            // 顺序就是优先级，调整顺序等于调整搜索体感。
            if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return ExactName;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return NamePrefix;
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return NameContains;

            if (item.Keywords != null)
            {
                foreach (var keyword in item.Keywords)
                {
                    if (string.IsNullOrEmpty(keyword)) continue;

                    if (keyword.Contains(query, StringComparison.OrdinalIgnoreCase))
                        return KeywordContains;

                    if (IsSubsequence(keyword, query)) return KeywordFuzzy;
                }
            }

            if (target.Contains(query, StringComparison.OrdinalIgnoreCase)) return TargetContains;
            if (IsSubsequence(name, query)) return NameFuzzy;

            return NoMatch;
        }

        /// <summary>把得分和「最近使用时间」合成一个可比较的排序键。</summary>
        public static long Rank(int score, ActionItem item)
        {
            var uses = Math.Clamp(item.UseCount, 0, 999);
            return (long)score * 1000 + uses;
        }

        /// <summary>同分同频次时的最终裁决：最近用过的排前面。</summary>
        public static int CompareRecency(ActionItem a, ActionItem b)
            => Nullable.Compare(b.LastUsedUtc, a.LastUsedUtc);

        /// <summary>子序列匹配：输入 "wjll" 能命中 "文件浏览器" 里的字符顺序。</summary>
        public static bool IsSubsequence(string? text, string query)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var index = 0;
            foreach (var c in query)
            {
                if (char.IsWhiteSpace(c)) continue;

                var found = false;
                while (index < text.Length)
                {
                    if (char.ToLowerInvariant(text[index]) == char.ToLowerInvariant(c))
                    {
                        index++;
                        found = true;
                        break;
                    }
                    index++;
                }

                if (!found) return false;
            }

            return true;
        }
    }
}
