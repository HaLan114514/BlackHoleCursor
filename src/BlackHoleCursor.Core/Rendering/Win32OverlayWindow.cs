using System.Runtime.InteropServices;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 无边框、置顶、鼠标穿透的覆盖层窗口（WS_EX_TOPMOST / TRANSPARENT / TOOLWINDOW / LAYERED）。
/// 由 D3D11 SwapChain 直接输出预乘 alpha，交给 DWM 合成。
/// </summary>
public sealed class Win32OverlayWindow : IOverlayWindow
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>不分配重定向位图：内容由 DirectComposition 直接合成。</summary>
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    /// <summary>
    /// 分层窗口。WS_EX_TRANSPARENT 只有在配合 WS_EX_LAYERED 时才会让命中测试彻底跳过窗口，
    /// 这是跨线程点击穿透唯一可靠的做法（仅靠 WM_NCHITTEST→HTTRANSPARENT 只在同线程内有效）。
    /// </summary>
    private const uint WS_EX_LAYERED = 0x00080000;

    private const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// 抓屏时**整个窗口被排除**（B3 / prd-v2 F5-3）。
    /// 不加这一句，覆盖层自己会被抓屏抓进去：黑洞把「上一帧的自己」折射一遍，
    /// 那一帧又含有再上一帧的自己 …… 逐帧放大，形成镜像回廊。
    /// 注意与 <c>WDA_MONITOR(0x1)</c> 区分：后者只是把内容涂黑，仍然会被抓进去。
    /// 需要 Windows 10 2004（build 19041）及以上；更老的系统调用会失败，此时降级为不加。
    /// </summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOREDRAW = 0x0008;

    /// <summary>置顶层级句柄（SetWindowPos 的 hWndInsertAfter）。</summary>
    private static readonly IntPtr HwndTopmost = new(-1);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint WM_NCHITTEST = 0x0084;

    /// <summary>命中测试「透明」：让窗口管理器继续向下寻找真正该接收点击的窗口。</summary>
    private const int HitTestTransparent = -1;

    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;

    private static readonly WindowProc Proc = WndProc;
    private static bool _classRegistered;

    private IntPtr _handle;
    private bool _disposed;

    public Win32OverlayWindow()
    {
        EnsureClassRegistered();
        _handle = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
            | WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_LAYERED,
            ClassName,
            "BlackHoleCursor Overlay",
            WS_POPUP,
            0, 0, 8, 8,
            IntPtr.Zero,
            IntPtr.Zero,
            InstanceHandle,
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"覆盖层窗口创建失败（GetLastError={Marshal.GetLastWin32Error()}）");
        }

        // 分层窗口默认完全透明，必须显式设为不透明；配合 WS_EX_TRANSPARENT 实现点击穿透
        SetLayeredWindowAttributes(_handle, 0, 0xFF, LWA_ALPHA);

        // B3：把自己从抓屏里排除掉，否则会形成镜像回廊。
        // 失败不抛异常 —— 老系统上没有这个能力，宁可降级也不要让覆盖层起不来。
        ExcludeFromCapture = SetWindowDisplayAffinity(_handle, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>是否成功把自己排除出抓屏（老版本 Windows 上会是 false）。</summary>
    public bool ExcludeFromCapture { get; }

    /// <summary>
    /// 当前的显示亲和性（回读值）。用于断言「确实设上了」，
    /// 而不是只看 SetWindowDisplayAffinity 的返回值。
    /// </summary>
    public uint DisplayAffinity
    {
        get
        {
            if (_handle == IntPtr.Zero)
            {
                return 0;
            }

            return GetWindowDisplayAffinity(_handle, out uint affinity) ? affinity : 0u;
        }
    }

    private const string ClassName = "BlackHoleCursorOverlayClass";

    public IntPtr Handle => _handle;

    public bool IsCreated => _handle != IntPtr.Zero;

    public void Move(int x, int y, int width, int height)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
    }

    /// <summary>
    /// 重申置顶层级（不改位置与尺寸）。创建时的 WS_EX_TOPMOST 只是初始状态：
    /// explorer 重启、任务栏重建或其它窗口抢占最顶层之后，覆盖层会被压到下面。
    /// </summary>
    public void EnsureTopmost()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOREDRAW);
    }

    public void SetVisible(bool visible)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(_handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = InstanceHandle,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"窗口类注册失败（GetLastError={Marshal.GetLastWin32Error()}）");
        }

        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 覆盖层窗口始终跟着鼠标，若不返回 HTTRANSPARENT，点击会被它吃掉，
        // 系统还会持续向它发送 WM_SETCURSOR，导致下面的窗口收不到任何输入。
        if (msg == WM_NCHITTEST)
        {
            return new IntPtr(HitTestTransparent);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 等待消息并抽取派发本线程队列中的全部消息。
    /// 覆盖层窗口在本线程创建，窗口所属的线程必须有消息循环；否则系统会判定窗口
    /// 「未响应」并生成幽灵窗口，严重时同步阻塞系统输入线程（用户表现为点不动、卡死）。
    /// </summary>
    public static void WaitAndPump(int timeoutMilliseconds)
    {
        MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)timeoutMilliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        PumpMessages();
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out MSG message, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount,
        IntPtr pHandles,
        uint dwMilliseconds,
        uint dwWakeMask,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativeMethods.POINT pt;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>进程模块句柄（单文件发布下不能用 Marshal.GetHINSTANCE(Module)）。</summary>
    private static IntPtr InstanceHandle => GetModuleHandle(null);
}
