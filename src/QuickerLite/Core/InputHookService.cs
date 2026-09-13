using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 低级输入钩子。做 RegisterHotKey 做不到的两件事：
    ///   · 单击 Ctrl 唤出（RegisterHotKey 强制要求组合键里有修饰键，注册不了「裸 Ctrl」）
    ///   · 鼠标中键唤出（RegisterHotKey 根本管不到鼠标）
    ///
    /// ─────────── 这个类里踩过的坑 ───────────
    ///
    /// 坑 A：委托被 GC 回收。SetWindowsHookEx 收的是函数指针，CLR 不知道原生侧还引用着它。
    ///        必须把委托存成字段（_keyboardProc / _mouseProc），否则运行几分钟后
    ///        GC 一跑，系统回调到已释放的地址 —— 轻则钩子失效，重则整机输入卡死。
    ///
    /// 坑 B：钩子回调有时间限制。Windows 默认 LowLevelHooksTimeout = 300ms，
    ///        回调超时系统会「静默」把钩子摘掉，不报错、不通知。
    ///        所以回调里绝对不能弹面板、不能读文件、不能弹对话框 ——
    ///        只做纯内存判断，然后把真正的活儿 Post 到消息队列后面去执行。
    ///
    /// 坑 C：自触发死循环。钩子收到的是「系统里所有」按键，包括我们自己用 SendInput
    ///        模拟出来的。必须靠 LLKHF_INJECTED / LLMHF_INJECTED 标志位把注入事件放行，
    ///        否则一旦将来加了「模拟按键」功能就会无限递归。
    ///
    /// 坑 D：杀软误报。全局低级钩子能看见用户的每一次击键，这是键盘记录器的标准形态，
    ///        Windows Defender / 各家杀软都会重点关照。加壳、签名能缓解，无法根治。
    ///
    /// 坑 E：钩子回调运行在「安装钩子的线程」上。我们在 UI 线程安装，所以回调也在 UI 线程，
    ///        不需要自己 Marshal。但反过来说 —— 回调里做的事会直接卡住 UI 线程的消息泵。
    ///
    /// 坑 F：误判。判定「单击 Ctrl」最麻烦的不是识别 Ctrl 本身，而是排除掉所有
    ///        「按着 Ctrl 干别的事」。实际要排除的至少有：
    ///          · Ctrl+C / Ctrl+V / Ctrl+Shift（中间碰了别的键）
    ///          · Ctrl+左键多选文件（中间点了鼠标）
    ///          · Ctrl+滚轮缩放（中间滚了滚轮）
    ///          · 先按住 Shift 再点 Ctrl（松开时别的修饰键还按着）
    ///          · 长时间按住 Ctrl 当修饰键用（超出时间窗）
    ///        漏掉任何一条，用户都会觉得这个功能「乱弹」。
    /// </summary>
    public sealed class InputHookService : IDisposable
    {
        // ⚠️ 坑 A：这两个字段是「保命引用」，永远不要改成局部变量。
        private readonly NativeMethods.LowLevelProc _keyboardProc;
        private readonly NativeMethods.LowLevelProc _mouseProc;

        private readonly Dispatcher _dispatcher;

        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private bool _disposed;

        // ── 单击 Ctrl 的判定状态 ──
        private bool _ctrlDown;
        private int _ctrlDownTick;

        /// <summary>
        /// Ctrl 按住期间是否发生过「别的输入」—— 别的键、鼠标点击、滚轮都算。
        /// 只要发生过，这次 Ctrl 松开就不算「单击 Ctrl」。
        /// </summary>
        private bool _otherKeyDuringCtrl;

        /// <summary>
        /// 中键的 down 是否被我们吞掉了。
        /// 吞了就必须连 up 一起吞 —— 只吞 down 会让目标程序收到一个
        /// 「没按下过就松开」的 up，某些程序会因此卡在拖拽状态。
        /// </summary>
        private bool _middleClickSwallowed;

        /// <summary>单击 Ctrl（按下并快速松开，期间没碰别的键）。</summary>
        public event Action? SingleCtrlPressed;

        /// <summary>单击鼠标中键。</summary>
        public event Action? MiddleClickPressed;

        public bool EnableSingleCtrl { get; set; }
        public bool EnableMiddleClick { get; set; }

        /// <summary>单击 Ctrl 的判定时间窗（毫秒）。</summary>
        public int SingleCtrlTimeoutMs { get; set; } = 400;

        /// <summary>
        /// 是否吞掉鼠标中键。吞掉之后浏览器不会触发「自动滚动」，
        /// 否则面板刚弹出来页面已经开始滚了。代价是中键拖拽也用不了。
        /// </summary>
        public bool SwallowMiddleClick { get; set; } = true;

        /// <summary>安装失败的原因，全部成功时为 null。</summary>
        public string? LastError { get; private set; }

        public bool KeyboardHookInstalled => _keyboardHook != IntPtr.Zero;
        public bool MouseHookInstalled => _mouseHook != IntPtr.Zero;

        public InputHookService()
        {
            // 在 UI 线程构造 → CurrentDispatcher 就是 UI 线程的 Dispatcher
            _dispatcher = Dispatcher.CurrentDispatcher;

            // 委托只创建一次并持有，见坑 A
            _keyboardProc = OnKeyboardMessage;
            _mouseProc = OnMouseMessage;
        }

        /// <summary>
        /// 按需安装/卸载钩子。幂等：重复调用不会重复安装。
        /// 两个开关都关掉时会主动卸载 —— 没必要的全局钩子只会白白招杀软。
        /// </summary>
        public void Apply(bool needKeyboard, bool needMouse)
        {
            if (_disposed) return;

            var errors = new List<string>();
            var module = NativeMethods.GetModuleHandle(null);

            if (needKeyboard && _keyboardHook == IntPtr.Zero)
            {
                _keyboardHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);

                if (_keyboardHook == IntPtr.Zero)
                    errors.Add($"键盘钩子安装失败（Windows 错误码 {Marshal.GetLastWin32Error()}）");
            }
            else if (!needKeyboard && _keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                ResetCtrlState();
            }

            if (needMouse && _mouseHook == IntPtr.Zero)
            {
                _mouseHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);

                if (_mouseHook == IntPtr.Zero)
                    errors.Add($"鼠标钩子安装失败（Windows 错误码 {Marshal.GetLastWin32Error()}）");
            }
            else if (!needMouse && _mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            LastError = errors.Count == 0 ? null : string.Join("；", errors);
        }

        // ══════════════════════ 键盘 ══════════════════════

        private IntPtr OnKeyboardMessage(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 时按约定必须直接透传，不能做任何处理
            if (nCode < 0) return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            try
            {
                var message = wParam.ToInt32();
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // 坑 C：注入事件一律放行，不参与判定
                if ((info.flags & NativeMethods.LLKHF_INJECTED) == 0)
                {
                    TrackCtrl(message, info.vkCode);
                }
            }
            catch
            {
                // 坑 B 的延伸：钩子里抛异常会打断整条消息链，
                // 结果是用户的所有键盘输入都受影响。吞掉，绝不放出去。
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private void TrackCtrl(int message, uint vkCode)
        {
            var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            if (!isDown && !isUp) return;

            var isCtrl = vkCode is NativeMethods.VK_CONTROL
                or NativeMethods.VK_LCONTROL
                or NativeMethods.VK_RCONTROL;

            if (isCtrl && isDown)
            {
                // 按住 Ctrl 会产生自动重复的 KEYDOWN，只有第一次算「按下」。
                // 不滤掉的话 TickCount 会被反复刷新，时间窗判定就废了。
                if (!_ctrlDown)
                {
                    _ctrlDown = true;
                    _ctrlDownTick = Environment.TickCount;
                    _otherKeyDuringCtrl = false;
                }
                return;
            }

            if (isCtrl && isUp)
            {
                var elapsed = unchecked(Environment.TickCount - _ctrlDownTick);

                // 三个条件缺一不可：
                //   wasClean  —— 中间没碰别的键 / 没点鼠标，说明不是 Ctrl+C、Ctrl+点击多选 这类组合
                //   !modifier —— 松开时 Shift / Alt / Win 都没按着，说明不是组合键的收尾
                //   elapsed   —— 在时间窗内松开，说明不是「按住 Ctrl 当修饰键用」
                // 第三条尤其重要：游戏里按住 Ctrl 蹲下、或者按住 Ctrl 准备多选，
                // 松手时如果弹面板会让人想砸键盘。
                var wasClean = _ctrlDown && !_otherKeyDuringCtrl && !AnyOtherModifierHeld();

                _ctrlDown = false;
                _otherKeyDuringCtrl = false;

                if (wasClean && elapsed <= SingleCtrlTimeoutMs && EnableSingleCtrl)
                {
                    Post(SingleCtrlPressed);
                }
                return;
            }

            // Ctrl 按住期间碰到了别的键 → 这次不可能是「单击 Ctrl」
            if (isDown && _ctrlDown)
            {
                _otherKeyDuringCtrl = true;
            }
        }

        /// <summary>
        /// 松开 Ctrl 的那一刻，Shift / Alt / Win 里还有按着的吗？
        /// 有的话说明这是一次组合键操作，不是单击 Ctrl。
        /// </summary>
        private static bool AnyOtherModifierHeld()
            => IsKeyDown(NativeMethods.VK_SHIFT)
               || IsKeyDown(NativeMethods.VK_MENU)
               || IsKeyDown(NativeMethods.VK_LWIN)
               || IsKeyDown(NativeMethods.VK_RWIN);

        private static bool IsKeyDown(int virtualKey)
            => (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

        // ══════════════════════ 鼠标 ══════════════════════

        private IntPtr OnMouseMessage(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            try
            {
                var message = wParam.ToInt32();

                // Ctrl 按住期间发生的任何鼠标动作都要标记：
                //   Ctrl+左键   —— 资源管理器里多选文件
                //   Ctrl+滚轮   —— 浏览器 / 编辑器里缩放
                // 不标记的话，上面这两种操作松手时会顺手弹出面板，非常烦人。
                if (_ctrlDown && message is NativeMethods.WM_LBUTTONDOWN
                    or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN
                    or NativeMethods.WM_XBUTTONDOWN
                    or NativeMethods.WM_MOUSEWHEEL
                    or NativeMethods.WM_MOUSEHWHEEL)
                {
                    _otherKeyDuringCtrl = true;
                }

                if (message is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var injected = (info.flags & NativeMethods.LLMHF_INJECTED) != 0;

                    if (!injected && EnableMiddleClick)
                    {
                        if (message == NativeMethods.WM_MBUTTONDOWN)
                        {
                            // Ctrl+中键 属于别的操作，不抢
                            var takeIt = !_ctrlDown;

                            _middleClickSwallowed = takeIt && SwallowMiddleClick;
                            if (takeIt) Post(MiddleClickPressed);

                            if (_middleClickSwallowed) return new IntPtr(1);
                        }
                        else
                        {
                            // up：只吞掉「之前确实吞过 down」的那一次
                            var swallowUp = _middleClickSwallowed;
                            _middleClickSwallowed = false;

                            if (swallowUp) return new IntPtr(1);
                        }
                    }
                }
            }
            catch
            {
                // 同上，钩子里不抛异常
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ══════════════════════ 派发 ══════════════════════

        /// <summary>
        /// 坑 B 的解药：把事件推到消息队列末尾，让钩子回调立刻返回。
        /// 直接在这里调 ShowPanel() 的话，创建窗口 + 定位 + 抢前台加起来轻松超过 300ms，
        /// 系统就会把这个钩子摘掉，而且不会告诉你。
        /// </summary>
        private void Post(Action? handler)
        {
            if (handler == null) return;

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, handler);
            }
            catch
            {
                // 程序正在退出时 Dispatcher 可能已经关了，忽略
            }
        }

        private void ResetCtrlState()
        {
            _ctrlDown = false;
            _otherKeyDuringCtrl = false;
            _middleClickSwallowed = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            ResetCtrlState();
        }
    }
}
