using System.Runtime.InteropServices;

namespace BlackHoleCursor.Core;

/// <summary>Win32 API 声明集中管理。</summary>
public static partial class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint SPI_SETCURSORS = 0x0057;
    public const uint SPIF_SENDCHANGE = 0x0002;
    public const uint WM_SESSION_CHANGE = 0x02B1;
    public const uint WTS_SESSION_LOCK = 0x7;
    public const uint WTS_SESSION_UNLOCK = 0x8;

    // ---- 标准系统光标 ID（OCR_*）----
    public const uint OCR_NORMAL = 32512;
    public const uint OCR_IBEAM = 32513;
    public const uint OCR_WAIT = 32514;
    public const uint OCR_CROSS = 32515;
    public const uint OCR_UP = 32516;
    public const uint OCR_SIZENWSE = 32642;
    public const uint OCR_SIZENESW = 32643;
    public const uint OCR_SIZEWE = 32644;
    public const uint OCR_SIZENS = 32645;
    public const uint OCR_SIZEALL = 32646;
    public const uint OCR_NO = 32648;
    public const uint OCR_HAND = 32649;
    public const uint OCR_APPSTARTING = 32650;

    /// <summary>需要替换掉的系统光标：覆盖桌面/文本/等待/缩放/手型等常见形态。</summary>
    public static uint[] SystemCursorIds { get; } =
    {
        OCR_NORMAL, OCR_IBEAM, OCR_WAIT, OCR_CROSS, OCR_UP,
        OCR_SIZENWSE, OCR_SIZENESW, OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL,
        OCR_NO, OCR_HAND, OCR_APPSTARTING,
    };

    public static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    // ---- 系统定时器分辨率（winmm）----
    // Windows 默认的系统时钟粒度是 15.6ms：MsgWaitForMultipleObjectsEx(1ms) 实际会睡满一两个滴答，
    // 渲染线程因此被压到 ~32 FPS（问题 1 实测）。把分辨率提到 1ms 才能让「等待 1ms」真的是 1ms。
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint period);

    /// <summary>注册应用私有消息。注意 user32 只导出 W 后缀版本。</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    /// <summary>user32 只导出 PostMessageA/W，没有无后缀的入口点。</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // ---- 虚拟桌面尺寸（M4.1 判定「全屏截图」用）----
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    /// <summary>剪贴板序列号（M4：用它识别「自己刚写回去的那次通知」）。</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    /// <summary>注册 / 注销剪贴板变化通知（M4.1）。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>剪贴板内容变化通知。</summary>
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>传 <see cref="IntPtr.Zero"/> + MONITOR_DEFAULTTOPRIMARY 可稳定取到主显示器。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>注意 user32 只导出 A/W 后缀版本，没有无后缀的入口点。</summary>
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetSystemCursor(IntPtr hcur, uint id);

    // ---- 全局快捷键 ----
    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_B = 0x42;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>user32 只导出 GetMonitorInfoA/W。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    /// <summary>单色图标描述。fIcon 用 int 而非 bool，保证结构体可直接 blittable 传递。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
}
