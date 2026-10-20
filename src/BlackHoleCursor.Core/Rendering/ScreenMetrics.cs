using System.Runtime.InteropServices;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 屏幕尺寸查询。prd-v2 §6.1 规定 <c>HoleSize</c> 是「阴影半径占**屏高**的比例」，
/// 因此必须按**光标所在的那个显示器**取高度（多显示器下各屏高度可能不同）。
/// </summary>
public static class ScreenMetrics
{
    /// <summary>
    /// 主显示器的高度（物理像素）；失败返回 0。
    /// 预览区没有「光标所在的那个显示器」可用，只能退到主显示器。
    /// 刻意走与 <see cref="MonitorHeightAt"/> 相同的 Win32 口径，而不是 WPF 的
    /// <c>SystemParameters.PrimaryScreenHeight</c> —— 后者是 96dpi 的逻辑单位，
    /// 在 125% / 150% 缩放下会与覆盖层用的物理像素不一致，导致预览比实机偏小。
    /// </summary>
    public static int PrimaryMonitorHeight()
    {
        IntPtr monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
        {
            return 0;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info)
            ? info.rcMonitor.Bottom - info.rcMonitor.Top
            : 0;
    }

    /// <summary>
    /// 整个虚拟桌面的尺寸（像素）。多显示器横向排列时宽为各屏之和。
    /// 用于判定剪贴板里的位图是否是一张全屏截图（M4.1）。
    /// </summary>
    public static (int Width, int Height) VirtualScreenSize()
    {
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return (width, height);
    }

    /// <summary>
    /// 返回包含指定点的显示器在**虚拟桌面坐标系**中的矩形；失败返回全零。
    ///
    /// fixes-v2 问题 5：抓屏改为「整块显示器固定抓取」后需要它 ——
    /// 抓屏矩形必须**只由光标所在的显示器决定**，与黑洞/窗口位置无关，
    /// 否则每帧挪一点就会把亚像素偏移注进「天空平面」，表现就是剧烈抖动
    /// （参考实现 singularity 正是靠「整屏 + 窗口不动」消灭抖动的）。
    /// </summary>
    public static NativeMethods.RECT MonitorRectAt(int x, int y)
    {
        var point = new NativeMethods.POINT { X = x, Y = y };
        IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return default;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }

    /// <summary>返回包含指定点的显示器高度（像素）；失败返回 0。</summary>
    public static int MonitorHeightAt(int x, int y)
    {
        var point = new NativeMethods.POINT { X = x, Y = y };
        IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return 0;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info)
            ? info.rcMonitor.Bottom - info.rcMonitor.Top
            : 0;
    }
}
