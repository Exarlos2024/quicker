using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickerLite.Models;

namespace QuickerLite.Core
{
    /// <summary>
    /// 配置持久化。存到 %AppData%\QuickerLite\config.json。
    /// 用 JSON 而不是 SQLite：这个量级（几十到几百个条目）JSON 完全够，
    /// 而且用户可以直接手改、方便排错。
    /// </summary>
    public sealed class ConfigStore
    {
        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickerLite");

        public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // 不转义中文，否则配置文件里全是 \u5FAE\u4FE1 这种，没法手改
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public AppConfig Current { get; private set; } = new();

        public void Load()
        {
            Directory.CreateDirectory(DataDirectory);

            if (!File.Exists(ConfigPath))
            {
                Current = CreateDefault();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                Current = loaded ?? CreateDefault();

                var migrated = Normalize(Current);

                // 迁移过就立刻把新结构落盘。不写的话每次启动都要重新迁移一遍，
                // 而且用户打开 config.json 会看到旧格式，以为程序没升级。
                if (migrated) Save();
            }
            catch
            {
                // 配置文件损坏：备份一份再从默认配置开始，别让用户直接卡在启动失败
                try
                {
                    File.Copy(ConfigPath,
                        Path.Combine(DataDirectory, $"config.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json"),
                        overwrite: true);
                }
                catch { /* 备份失败也不影响继续 */ }

                Current = CreateDefault();
                Save();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);

                // 先写临时文件再替换，避免写一半崩溃导致配置全丢
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, ConfigPath, overwrite: true);
            }
            catch
            {
                // 保存失败不该让程序崩掉
            }
        }

        /// <summary>
        /// 反序列化之后的兜底修补。这里做四件事：
        ///   1. 补上 JSON 里缺的 / 为 null 的字段
        ///   2. 把旧版「扁平 Items」迁移成「默认」动作页
        ///   3. 重建 Scenes 字典，恢复大小写不敏感的比较器
        ///   4. 保证至少存在一页，让上层不用到处判空
        /// </summary>
        /// <returns>是否发生了旧格式迁移（发生了就应该立刻保存）。</returns>
        private static bool Normalize(AppConfig cfg)
        {
            cfg.Hotkey ??= new HotkeyConfig();

            if (cfg.Columns < 1 || cfg.Columns > 12) cfg.Columns = 6;
            if (cfg.SingleCtrlTimeoutMs < 100 || cfg.SingleCtrlTimeoutMs > 2000)
                cfg.SingleCtrlTimeoutMs = 400;

            cfg.GlobalPages ??= new List<ActionPage>();
            cfg.Scenes ??= new Dictionary<string, List<ActionPage>>(StringComparer.OrdinalIgnoreCase);

            // ── 旧版迁移：以前所有条目都平铺在 Items 里，现在装进第一页 ──
            var migrated = false;

            if (cfg.LegacyItems is { Count: > 0 })
            {
                var existing = cfg.GlobalPages.Count > 0 ? cfg.GlobalPages[0] : null;

                if (existing == null)
                {
                    cfg.GlobalPages.Insert(0, new ActionPage { Name = "默认", Items = cfg.LegacyItems });
                }
                else
                {
                    // 已经有页了（说明用户手改过），就把旧条目接到第一页后面，不丢数据
                    existing.Items ??= new List<ActionItem>();
                    existing.Items.InsertRange(0, cfg.LegacyItems);
                }

                migrated = true;
            }

            // 迁移完就清掉，避免这份数据被重复写入配置文件、每次启动都迁移一遍
            if (cfg.LegacyItems != null)
            {
                cfg.LegacyItems = null;
                migrated = true;
            }

            // ── 坑：System.Text.Json 反序列化 Dictionary 时会用 new Dictionary<,>()，
            //    构造时传的 OrdinalIgnoreCase 比较器不会被保留。
            //    不重建的话，"Chrome" 和 "chrome" 会被当成两个不同的场景。
            if (cfg.Scenes.Count > 0)
            {
                var rebuilt = new Dictionary<string, List<ActionPage>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in cfg.Scenes)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    rebuilt[pair.Key.Trim()] = pair.Value ?? new List<ActionPage>();
                }
                cfg.Scenes = rebuilt;
            }

            // 用户可能手动把页删光了，给一个空页兜底。
            // 注意这里给的是「空页」而不是「初始条目」—— 用户删掉的东西不该自己长回来。
            if (cfg.GlobalPages.Count == 0 && cfg.Scenes.Count == 0)
                cfg.GlobalPages.Add(new ActionPage { Name = "默认" });

            foreach (var page in AllPages(cfg))
            {
                page.Items ??= new List<ActionItem>();
                if (string.IsNullOrWhiteSpace(page.Name)) page.Name = "未命名";
            }

            return migrated;
        }

        /// <summary>遍历配置里的所有页（全局 + 各场景），用于统一做兜底修补。</summary>
        public static IEnumerable<ActionPage> AllPages(AppConfig cfg)
        {
            foreach (var page in cfg.GlobalPages)
                yield return page;

            foreach (var scene in cfg.Scenes.Values)
            {
                foreach (var page in scene)
                    yield return page;
            }
        }

        /// <summary>所有条目的总数，用于状态栏和日志。</summary>
        public static int CountItems(AppConfig cfg)
        {
            var total = 0;
            foreach (var page in AllPages(cfg))
                total += page.Items.Count;

            return total;
        }

        // ══════════════════════ 动作页管理 ══════════════════════

        /// <summary>配置里一共有多少页（全局 + 所有场景）。</summary>
        public static int CountPages(AppConfig cfg)
        {
            var total = cfg.GlobalPages.Count;
            foreach (var scene in cfg.Scenes.Values)
                total += scene.Count;

            return total;
        }

        /// <summary>
        /// 这一页属于哪个场景？全局面板页返回 null。
        /// 用引用比较而不是 Id 比较 —— 调用方手里拿的就是配置里的那个对象。
        /// </summary>
        public static string? FindSceneOf(AppConfig cfg, ActionPage page)
        {
            foreach (var pair in cfg.Scenes)
            {
                foreach (var candidate in pair.Value)
                {
                    if (ReferenceEquals(candidate, page)) return pair.Key;
                }
            }

            return null;
        }

        /// <summary>把一页从它现在的位置摘下来（不改归属）。摘掉了返回 true。</summary>
        public static bool Detach(AppConfig cfg, ActionPage page)
        {
            if (cfg.GlobalPages.Remove(page)) return true;

            string? emptiedScene = null;

            foreach (var pair in cfg.Scenes)
            {
                if (!pair.Value.Remove(page)) continue;

                if (pair.Value.Count == 0) emptiedScene = pair.Key;
                break;
            }

            if (emptiedScene == null) return false;

            // 场景被摘空了就顺手把这个场景删掉，免得配置文件里留一堆空壳。
            // 注意：不能在遍历字典的过程中删 key，必须跳出循环再删。
            cfg.Scenes.Remove(emptiedScene);
            return true;
        }

        /// <summary>
        /// 把一页移到指定位置。sceneKey 为 null 表示移到全局面板区。
        /// 会先把页面从原位置摘掉，所以同一个对象不会被重复挂两处。
        /// </summary>
        public static void MovePage(AppConfig cfg, ActionPage page, string? sceneKey)
        {
            Detach(cfg, page);

            if (string.IsNullOrWhiteSpace(sceneKey))
            {
                cfg.GlobalPages.Add(page);
                return;
            }

            var key = sceneKey.Trim();
            if (!cfg.Scenes.TryGetValue(key, out var pages) || pages == null)
            {
                pages = new List<ActionPage>();
                cfg.Scenes[key] = pages;
            }

            pages.Add(page);
        }

        /// <summary>把一个场景整体删掉（连带里面的页）。</summary>
        public static bool RemoveScene(AppConfig cfg, string sceneKey)
            => !string.IsNullOrWhiteSpace(sceneKey) && cfg.Scenes.Remove(sceneKey);

        /// <summary>
        /// 首次运行时给两页内容 + 一个场景，让用户打开就能看到效果：
        ///   · 两页全局面板 → 立刻能试出「滚轮翻页」
        ///   · 一个资源管理器场景 → 立刻能试出「上下文自动切换」
        /// 想恢复初始状态，删掉 config.json 重启即可。
        /// </summary>
        private static AppConfig CreateDefault()
        {
            var cfg = new AppConfig { Columns = 6 };

            cfg.GlobalPages.Add(BuildCommonPage());
            cfg.GlobalPages.Add(BuildToolsPage());

            var explorer = BuildExplorerScene();
            if (explorer.Items.Count > 0)
                cfg.Scenes["explorer"] = new List<ActionPage> { explorer };

            return cfg;
        }

        private static ActionPage BuildCommonPage()
        {
            var page = new ActionPage { Name = "常用" };

            void Add(string name, string target, ItemType type, params string[] keywords)
            {
                page.Items.Add(new ActionItem
                {
                    Name = name,
                    Target = target,
                    Type = type,
                    Keywords = new List<string>(keywords),
                });
            }

            Add("文件资源管理器", "explorer.exe", ItemType.App, "explorer", "wenjian", "资源", "此电脑");
            Add("记事本", "notepad.exe", ItemType.App, "notepad", "jishiben", "文本");
            Add("计算器", "calc.exe", ItemType.App, "calc", "jisuanqi");
            Add("画图", "mspaint.exe", ItemType.App, "mspaint", "huatu", "paint");
            Add("命令提示符", "cmd.exe", ItemType.App, "cmd", "mingling");
            Add("任务管理器", "taskmgr.exe", ItemType.App, "taskmgr", "renwu");
            Add("控制面板", "control.exe", ItemType.App, "control", "kongzhi");
            Add("系统设置", "ms-settings:", ItemType.Url, "settings", "shezhi", "设置");

            return page;
        }

        private static ActionPage BuildToolsPage()
        {
            var page = new ActionPage { Name = "系统工具" };

            void Add(string name, string target, params string[] keywords)
            {
                page.Items.Add(new ActionItem
                {
                    Name = name,
                    Target = target,
                    Type = ItemType.App,
                    Keywords = new List<string>(keywords),
                });
            }

            Add("注册表编辑器", "regedit.exe", "regedit", "zhucebiao");
            Add("服务", "services.msc", "services", "fuwu");
            Add("事件查看器", "eventvwr.msc", "eventvwr", "shijian");
            Add("磁盘管理", "diskmgmt.msc", "diskmgmt", "cipan");
            Add("任务计划程序", "taskschd.msc", "taskschd", "jihua");
            Add("设备管理器", "devmgmt.msc", "devmgmt", "shebei");
            Add("系统信息", "msinfo32.exe", "msinfo32", "xitong");
            Add("资源监视器", "resmon.exe", "resmon", "ziyuan");

            return page;
        }

        /// <summary>
        /// 资源管理器场景。这些都是「在文件管理器里才想用」的动作，
        /// 放到全局面板里会挤占常用位置，正好拿来演示上下文切换。
        /// </summary>
        private static ActionPage BuildExplorerScene()
        {
            var page = new ActionPage { Name = "资源管理器" };

            // .NET 没有提供 Downloads 的 SpecialFolder，只能自己拼。
            // 顺便把「目录不存在就跳过」也一起处理了 —— 不同机器上这些目录不一定都有。
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            void AddSub(string name, string sub, params string[] keywords)
            {
                if (string.IsNullOrEmpty(profile)) return;

                var path = Path.Combine(profile, sub);
                if (!Directory.Exists(path)) return;

                page.Items.Add(new ActionItem
                {
                    Name = name,
                    Target = path,
                    Type = ItemType.Folder,
                    Keywords = new List<string>(keywords),
                });
            }

            AddSub("下载", "Downloads", "xiazai", "downloads");
            AddSub("桌面", "Desktop", "zhuomian", "desktop");
            AddSub("文档", "Documents", "wendang", "documents");
            AddSub("图片", "Pictures", "tupian", "pictures");

            // 这两个走的是 shell 命名空间，不是真实路径，用 explorer.exe 的 shell: 参数打开
            page.Items.Add(new ActionItem
            {
                Name = "此电脑",
                Target = "explorer.exe",
                Type = ItemType.App,
                Arguments = "shell:MyComputerFolder",
                Keywords = new List<string> { "cidiannao", "computer" },
            });

            page.Items.Add(new ActionItem
            {
                Name = "回收站",
                Target = "explorer.exe",
                Type = ItemType.App,
                Arguments = "shell:RecycleBinFolder",
                Keywords = new List<string> { "huishouzhan", "recycle" },
            });

            return page;
        }
    }
}
