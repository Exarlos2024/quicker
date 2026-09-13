using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickerLite.Interop;

namespace QuickerLite.Core
{
    /// <summary>
    /// 全局热键。
    ///
    /// 坑 5：RegisterHotKey 失败是「静默」的——别的程序（微信、输入法、截图工具）
    /// 已经占用了同一个组合键时，它只返回 false，不报错。用户按了没反应，
    /// 开发者很容易误判成自己的 bug。所以这里把失败原因一路带出来，
    /// 让上层能弹提示、让用户去设置里换键。
    ///
    /// 说明：RegisterHotKey 无法注册「单独的 Ctrl 键」这种无修饰键操作
    /// （Quicker 的招牌交互），那需要低级键盘钩子 WH_KEYBOARD_LL，
    /// 代价是会被杀软误报。这一版先不做，接口留好了。
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 0x5151;
        private const int ErrorHotkeyAlreadyRegistered = 1409;

        private HwndSource? _sink;
        private bool _registered;

        public event Action? Pressed;

        /// <summary>上一次注册失败的原因，成功时为 null。</summary>
        public string? LastError { get; private set; }

        public bool IsRegistered => _registered;

        /// <summary>
        /// 用一个独立的隐藏窗口接收 WM_HOTKEY，而不是挂在面板窗口上。
        /// 因为 WPF 在某些属性变化时会重建窗口的 HWND，热键就丢了。
        /// </summary>
        private void EnsureSink()
        {
            if (_sink != null) return;

            var parameters = new HwndSourceParameters("QuickerLite.HotkeySink")
            {
                WindowStyle = 0x00000000,          // WS_OVERLAPPED，尺寸 0 且不可见
                ExtendedWindowStyle = 0x00000080,  // WS_EX_TOOLWINDOW，不进 Alt+Tab
                Width = 0,
                Height = 0,
            };

            _sink = new HwndSource(parameters);
            _sink.AddHook(WndProc);
        }

        public bool Register(uint modifiers, uint virtualKey)
        {
            EnsureSink();
            Unregister();

            // MOD_NOREPEAT：按住不放时只触发一次，避免面板疯狂开关
            _registered = NativeMethods.RegisterHotKey(
                _sink!.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);

            if (_registered)
            {
                LastError = null;
            }
            else
            {
                var code = Marshal.GetLastWin32Error();
                LastError = code == ErrorHotkeyAlreadyRegistered
                    ? "这个组合键已被其它程序占用，请换一个"
                    : $"注册失败（Windows 错误码 {code}）";
            }

            return _registered;
        }

        public void Unregister()
        {
            if (!_registered || _sink == null) return;

            NativeMethods.UnregisterHotKey(_sink.Handle, HotkeyId);
            _registered = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Pressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();

            if (_sink != null)
            {
                _sink.RemoveHook(WndProc);
                _sink.Dispose();
                _sink = null;
            }
        }
    }
}
