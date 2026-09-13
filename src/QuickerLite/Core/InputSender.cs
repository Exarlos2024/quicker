using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 把文本或按键打到当前光标处。
    ///
    /// 调用方必须先保证焦点已经在目标窗口上 —— 这个函数只负责「发」，
    /// 不管「发给谁」。焦点时序由 PanelWindow 处理（见那里关于轮询等待的注释）。
    /// </summary>
    public static class InputSender
    {
        /// <summary>
        /// 发送一段文本。走 KEYEVENTF_UNICODE，不碰剪贴板。
        /// </summary>
        public static bool SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var inputs = new List<NativeMethods.INPUT>(text.Length * 2 + 4);

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                // \r\n 是 Windows 的换行，只发一次回车，否则会空出一行
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                    AddKey(inputs, 0x0D);   // VK_RETURN
                    continue;
                }

                if (c == '\n')
                {
                    AddKey(inputs, 0x0D);
                    continue;
                }

                if (c == '\t')
                {
                    AddKey(inputs, 0x09);   // VK_TAB
                    continue;
                }

                AddUnicode(inputs, c);
            }

            return Flush(inputs);
        }

        /// <summary>发送一个按键组合。修饰键先按下、最后抬起，中间夹着主键。</summary>
        public static bool SendKeys(KeyCombo combo)
        {
            var inputs = new List<NativeMethods.INPUT>(10);

            // VK_CONTROL 在 NativeMethods 里是 uint（钩子那段就是这么定的），这里统一成 int
            if (combo.Ctrl) AddDown(inputs, (int)NativeMethods.VK_CONTROL);
            if (combo.Alt) AddDown(inputs, NativeMethods.VK_MENU);
            if (combo.Shift) AddDown(inputs, NativeMethods.VK_SHIFT);
            if (combo.Win) AddDown(inputs, NativeMethods.VK_LWIN);

            AddKey(inputs, combo.Vk);

            // 反向抬起。顺序反过来只是习惯，真正重要的是不能在主键之前抬。
            if (combo.Win) AddUp(inputs, NativeMethods.VK_LWIN);
            if (combo.Shift) AddUp(inputs, NativeMethods.VK_SHIFT);
            if (combo.Alt) AddUp(inputs, NativeMethods.VK_MENU);
            if (combo.Ctrl) AddUp(inputs, (int)NativeMethods.VK_CONTROL);

            return Flush(inputs);
        }

        private static void AddKey(List<NativeMethods.INPUT> inputs, int vk)
        {
            AddDown(inputs, vk);
            AddUp(inputs, vk);
        }

        private static void AddDown(List<NativeMethods.INPUT> inputs, int vk)
        {
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        dwFlags = IsExtended(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0,
                    },
                },
            });
        }

        private static void AddUp(List<NativeMethods.INPUT> inputs, int vk)
        {
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        dwFlags = NativeMethods.KEYEVENTF_KEYUP |
                                  (IsExtended(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
                    },
                },
            });
        }

        private static void AddUnicode(List<NativeMethods.INPUT> inputs, char c)
        {
            // 代理对（emoji 之类）要按 UTF-16 码元逐个发，wScan 就是码元本身
            inputs.Add(MakeUnicode(c, false));
            inputs.Add(MakeUnicode(c, true));
        }

        private static NativeMethods.INPUT MakeUnicode(char c, bool up)
        {
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE |
                                  (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
                    },
                },
            };
        }

        /// <summary>
        /// 方向键、Home/End、PageUp/PageDown、Insert/Delete 这些「扩展键」
        /// 必须带 EXTENDEDKEY 标志，否则小键盘上同码的键会被触发 ——
        /// 典型症状是按 Home 却出来一个数字 7。
        /// </summary>
        private static bool IsExtended(int vk)
        {
            return vk is 0x21 or 0x22 or 0x23 or 0x24   // PgUp PgDn End Home
                      or 0x25 or 0x26 or 0x27 or 0x28   // ← ↑ → ↓
                      or 0x2D or 0x2E                   // Insert Delete
                      or 0x5B or 0x5C                   // 左右 Win
                      or 0x5D;                          // 菜单键
        }

        private static bool Flush(List<NativeMethods.INPUT> inputs)
        {
            if (inputs.Count == 0) return false;

            var size = Marshal.SizeOf<NativeMethods.INPUT>();
            var sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), size);

            // 返回值是「真正被插入的事件数」。不等于输入数就是失败了，
            // 而且系统不会给任何提示 —— 必须自己判，否则就是「按了没反应」。
            return sent == (uint)inputs.Count;
        }
    }
}
