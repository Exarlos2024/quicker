using System;
using System.Collections.Generic;
using QuickerLite.Models;

namespace QuickerLite.Core
{
    /// <summary>
    /// 把「当前前台程序 + 配置」解析成一组可翻页的动作页。
    ///
    /// 规则很简单，但要明确写下来，否则以后自己都会忘：
    ///   1. 前台程序命中了某个场景（Scenes）→ 场景页排在前面，全局面板页跟在后面。
    ///      面板一打开就在场景第一页上，往后翻能翻到全局面板。
    ///   2. 没命中场景 → 只有全局面板页。
    ///
    /// 为什么场景页在前而不是在后？因为上下文才是「此刻最可能想用的东西」，
    /// 全局面板是兜底。Quicker 也是这个顺序。
    /// </summary>
    public static class PageResolver
    {
        public sealed class Resolution
        {
            public List<ActionPage> Pages { get; init; } = new();

            /// <summary>命中的场景进程名；没命中为 null。</summary>
            public string? SceneProcess { get; init; }

            /// <summary>排在最前面的场景页数量。0 表示这次没有上下文场景。</summary>
            public int ScenePageCount { get; init; }

            /// <summary>面板打开时应该停在第几页。有场景时是 0（场景首页），无场景时也是 0。</summary>
            public int StartIndex => 0;

            public bool HasScene => SceneProcess != null;
        }

        public static Resolution Resolve(AppConfig config, string? foregroundProcess)
        {
            var pages = new List<ActionPage>();
            var scenePageCount = 0;
            string? scene = null;

            if (!string.IsNullOrEmpty(foregroundProcess))
            {
                // Scenes 在 ConfigStore 里被重建成了 OrdinalIgnoreCase 的字典，
                // 这里不用再管大小写
                if (config.Scenes.TryGetValue(foregroundProcess, out var scenePages)
                    && scenePages is { Count: > 0 })
                {
                    scene = foregroundProcess;
                    scenePageCount = scenePages.Count;
                    pages.AddRange(scenePages);
                }
            }

            pages.AddRange(config.GlobalPages);

            return new Resolution
            {
                Pages = pages,
                SceneProcess = scene,
                ScenePageCount = scenePageCount,
            };
        }
    }
}
