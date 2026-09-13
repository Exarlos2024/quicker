using System;
using System.Runtime.InteropServices;

namespace QuickerLite.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    /// <summary>
    /// 低级键盘钩子回调收到的按键信息。
    /// 注意 dwExtraInfo 在 32/64 位下都是指针宽度，必须用 IntPtr，用 int 会让整个结构体错位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>低级鼠标钩子回调收到的鼠标信息。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 所有 P/Invoke 签名集中放这里。集中管理的好处是签名写错时只有一处需要改，
    /// 而且能一眼看出这个项目到底依赖了哪些 Win32 能力。
    /// </summary>
    internal static class NativeMethods
    {
        // ───────────────────────── 全局热键 ─────────────────────────
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ─────────────────── 前台焦点（破解前台锁） ───────────────────
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        /// <summary>同上，但把进程 ID 取出来（用于识别前台程序，做上下文动作页）。</summary>
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        public static extern uint GetWindowProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
            [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ───────────────────────── 定位与尺寸 ─────────────────────────
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("Shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;

        // ───────────────────────── 图标提取 ─────────────────────────
        public const uint SHGFI_ICON = 0x00000100;
        public const uint SHGFI_LARGEICON = 0x00000000;
        public const uint SHGFI_SMALLICON = 0x00000001;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
        public const uint SHGFI_SYSICONINDEX = 0x00004000;

        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public const int SHIL_LARGE = 0;
        public const int SHIL_SMALL = 1;
        public const int SHIL_EXTRALARGE = 2;
        public const int SHIL_JUMBO = 4;
        public const int ILD_TRANSPARENT = 0x00000001;

        [DllImport("shell32.dll", PreserveSig = false)]
        public static extern void SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        /// <summary>
        /// IImageList 的 COM 接口。只声明到 GetIcon 为止——vtable 顺序必须和原生定义一致，
        /// 后面用不到的方法可以省略，但不能插错顺序。
        /// </summary>
        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        public static readonly Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

        // ───────────────────── COM 初始化（图标工作线程） ─────────────────────
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        public const uint COINIT_APARTMENTTHREADED = 0x2;

        // ───────────────────── 窗口合成：圆角与投影 ─────────────────────
        // 全部交给 DWM，不用 WPF 的 AllowsTransparency + 自绘阴影：
        // 后者会禁用硬件加速，面板滚动掉帧、拖动闪烁，而这是个热键高频唤出的窗口。
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
            ref int pvAttribute, int cbAttribute);

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_DEFAULT = 0;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        /// <summary>
        /// DWM 扩展边框。对无边框窗口来说这是拿到「系统标准投影」的正规途径：
        /// 把四条边各扩展 1px，DWM 就认为这个窗口有非客户区，从而绘制投影。
        /// 边距必须是 1 而不是 0 —— 0 的语义是「不扩展」，等于什么都没有。
        /// </summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        // 备选方案：给窗口类加 CS_DROPSHADOW。DWM 扩展边框在部分主题下不生效
        // （尤其是关掉「透明效果」的机器），这条能兜住。
        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        public static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        public static extern uint SetClassLong(IntPtr hWnd, int nIndex, uint dwNewLong);

        public const int GCL_STYLE = -26;
        public const uint CS_DROPSHADOW = 0x00020000;

        // ───────────────────── 低级输入钩子 ─────────────────────
        // 用于实现 RegisterHotKey 做不到的两件事：
        //   1. 「单击 Ctrl 唤出」—— RegisterHotKey 要求必须带修饰键
        //   2. 「鼠标中键唤出」—— RegisterHotKey 管不到鼠标

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_MOUSEHWHEEL = 0x020E;

        /// <summary>标志位：这个按键/点击是程序模拟出来的（SendInput、按键精灵…）。</summary>
        public const uint LLKHF_INJECTED = 0x00000010;
        public const uint LLMHF_INJECTED = 0x00000001;

        public const uint VK_CONTROL = 0x11;
        public const uint VK_LCONTROL = 0xA2;
        public const uint VK_RCONTROL = 0xA3;

        public const int VK_SHIFT = 0x10;
        public const int VK_MENU = 0x12;    // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        /// <summary>
        /// 取按键当前状态。返回值最高位为 1 表示正在按下。
        /// 注意它和 GetAsyncKeyState 的区别：GetKeyState 反映的是「本线程消息队列
        /// 已处理到的那一点」的状态，在钩子回调里用它是恰当的。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        /// <summary>
        /// 低级钩子的回调签名。
        /// ⚠️ 这个委托实例必须被长期持有（存成字段）—— 只把委托传给 SetWindowsHookEx
        /// 而自己不保存引用，GC 会把它回收掉，然后系统回调到已释放的地址，
        /// 表现为整个系统的键盘鼠标卡死或进程直接崩。这是最经典的钩子事故。
        /// </summary>
        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        // ───────────────────── 模拟输入（SendInput）─────────────────────
        // 用于「文本片段」和「按键」两类格子：把内容打到当前光标处。
        //
        // 为什么用 SendInput 而不是 keybd_event：后者是 16 位时代遗留的 API，
        // 已经被 SendInput 取代，且不支持 KEYEVENTF_UNICODE。
        //
        // 为什么文本走 KEYEVENTF_UNICODE 而不是「剪贴板 + Ctrl+V」：
        // 后者会覆盖用户剪贴板，而还原剪贴板是个竞态 —— 你不知道目标程序
        // 什么时候才真正完成粘贴。为了不打搅用户现有的剪贴板内容，宁可自己发字符。
        // 代价是极少数不处理 WM_UNICODE_CHAR 的老程序可能收不到，已在 README 记下。

        public const int INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParam;
            public ushort lParam;
        }

        /// <summary>
        /// 三个输入结构体的联合体。必须用 Explicit + FieldOffset(0) 让它们重叠，
        /// 否则结构体尺寸对不上，SendInput 会返回 0 且 GetLastError 报参数错误 ——
        /// 而且这个失败是静默的，按键就是「按了没反应」。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        // 修饰键的虚拟键码（VK_SHIFT / VK_CONTROL / VK_MENU / VK_LWIN）
        // 复用上面「低级输入钩子」那一段已经定义好的，别再抄一份。

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
    }
}
