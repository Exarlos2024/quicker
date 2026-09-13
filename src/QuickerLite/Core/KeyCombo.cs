using System;
using System.Collections.Generic;

namespace QuickerLite.Core
{
    /// <summary>
    /// 一个按键组合的解析结果。纯数据，不碰 Win32。
    /// </summary>
    public readonly struct KeyCombo
    {
        public bool Ctrl { get; init; }
        public bool Alt { get; init; }
        public bool Shift { get; init; }
        public bool Win { get; init; }

        /// <summary>虚拟键码。修饰键之外的那一个键。</summary>
        public int Vk { get; init; }

        public bool HasModifier => Ctrl || Alt || Shift || Win;
    }

    /// <summary>
    /// 把 "Ctrl+Shift+T" 这样的字符串解析成 <see cref="KeyCombo"/>。
    ///
    /// 为什么单独抽成纯函数：解析规则里的边角情况（大小写、别名、重复修饰键、
    /// F13 这种越界值）在界面上试一遍很难覆盖全，而且错了也没有任何提示 ——
    /// 用户只会觉得「这个格子按了没反应」。照 TileReorder 的做法，抽出来配断言。
    ///
    /// 刻意不做的：按键序列（"Ctrl+C, Ctrl+V"）、按键重复（"A×3"）、
    /// 以及标点符号键。前两个会让这个东西滑向脚本引擎，标点用的场合太少，
    /// 给了反而要维护一张容易错的 OEM 键码表。
    /// </summary>
    public static class KeyComboParser
    {
        /// <summary>
        /// 解析。失败时 <paramref name="error"/> 是可直接显示给用户的中文说明。
        /// </summary>
        public static bool TryParse(string? text, out KeyCombo combo, out string? error)
        {
            combo = default;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "按键不能为空";
                return false;
            }

            var parts = text.Split('+');
            var tokens = new List<string>(parts.Length);

            foreach (var part in parts)
            {
                // "Ctrl+" 或 "Ctrl++S" 都会产生空段，这种输入是手滑，直接拒掉
                if (string.IsNullOrWhiteSpace(part))
                {
                    error = "按键格式不对，修饰键之间用 + 连接，例如 Ctrl+Shift+T";
                    return false;
                }

                tokens.Add(part.Trim());
            }

            // 最后一段是键，前面全是修饰键
            var keyToken = tokens[tokens.Count - 1];

            bool ctrl = false, alt = false, shift = false, win = false;

            for (var i = 0; i < tokens.Count - 1; i++)
            {
                switch (tokens[i].ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        if (ctrl) { error = "Ctrl 重复了"; return false; }
                        ctrl = true;
                        break;

                    case "alt":
                        if (alt) { error = "Alt 重复了"; return false; }
                        alt = true;
                        break;

                    case "shift":
                        if (shift) { error = "Shift 重复了"; return false; }
                        shift = true;
                        break;

                    case "win":
                    case "windows":
                    case "super":
                        if (win) { error = "Win 重复了"; return false; }
                        win = true;
                        break;

                    default:
                        error = $"不认识的修饰键「{tokens[i]}」，只支持 Ctrl / Alt / Shift / Win";
                        return false;
                }
            }

            if (!TryResolveKey(keyToken, out var vk, out error))
                return false;

            combo = new KeyCombo
            {
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                Win = win,
                Vk = vk,
            };

            return true;
        }

        private static bool TryResolveKey(string token, out int vk, out string? error)
        {
            vk = 0;
            error = null;

            var key = token.ToLowerInvariant();

            // ① 单字符：字母或数字
            if (token.Length == 1)
            {
                var c = token[0];

                if (c >= 'A' && c <= 'Z') { vk = 0x41 + (c - 'A'); return true; }
                if (c >= 'a' && c <= 'z') { vk = 0x41 + (c - 'a'); return true; }
                if (c >= '0' && c <= '9') { vk = 0x30 + (c - '0'); return true; }

                error = $"不支持的按键「{token}」，字母数字之外的键请用英文名（如 Enter、Tab、F5）";
                return false;
            }

            // ② F1 .. F24
            if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key[1..], out var fn))
            {
                if (fn < 1 || fn > 24)
                {
                    error = $"功能键只有 F1 到 F24，没有 F{fn}";
                    return false;
                }

                vk = 0x6F + fn;   // VK_F1 = 0x70
                return true;
            }

            // ③ 命名键
            if (NamedKeys.TryGetValue(key, out vk)) return true;

            error = $"不认识的按键「{token}」";
            return false;
        }

        private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.Ordinal)
        {
            ["enter"] = 0x0D,
            ["return"] = 0x0D,
            ["tab"] = 0x09,
            ["esc"] = 0x1B,
            ["escape"] = 0x1B,
            ["space"] = 0x20,
            ["backspace"] = 0x08,
            ["back"] = 0x08,
            ["delete"] = 0x2E,
            ["del"] = 0x2E,
            ["insert"] = 0x2D,
            ["ins"] = 0x2D,
            ["home"] = 0x24,
            ["end"] = 0x23,
            ["pageup"] = 0x21,
            ["pgup"] = 0x21,
            ["pagedown"] = 0x22,
            ["pgdn"] = 0x22,
            ["up"] = 0x26,
            ["down"] = 0x28,
            ["left"] = 0x25,
            ["right"] = 0x27,
            ["printscreen"] = 0x2C,
            ["prtsc"] = 0x2C,
            ["print"] = 0x2C,
            ["menu"] = 0x5D,
            ["apps"] = 0x5D,
            ["pause"] = 0x13,
            ["capslock"] = 0x14,
            ["numlock"] = 0x90,
            ["scrolllock"] = 0x91,

            // Win 单独出现时（"Win"）当作键；"Win+E" 里它是修饰键，由上面那段处理
            ["win"] = 0x5B,
            ["windows"] = 0x5B,
        };
    }
}
