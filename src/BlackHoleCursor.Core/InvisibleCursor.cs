using System.Runtime.InteropServices;

namespace BlackHoleCursor.Core;

/// <summary>
/// 创建完全透明的光标。
///
/// 为什么需要它：<c>ShowCursor(FALSE)</c> 只改变「本进程」的显示计数，
/// 而桌面上的其它窗口在 <c>WM_SETCURSOR</c> 里仍会 <c>SetCursor(箭头)</c>，
/// 因此单靠它藏不住系统箭头（实测显示计数已到 -1，光标依然可见）。
/// 可靠做法是把标准系统光标整体换成透明光标，退出时再用 SPI_SETCURSORS 还原用户方案。
/// </summary>
public static class InvisibleCursor
{
    private const int Size = 32;

    /// <summary>
    /// 新建一个透明光标句柄。
    /// 注意：<c>SetSystemCursor</c> 会接管（销毁）传进去的句柄，
    /// 所以每次替换都必须传一个新句柄，绝不能复用同一个。
    /// </summary>
    public static IntPtr Create()
    {
        try
        {
            IntPtr handle = CreateCore();
            if (handle == IntPtr.Zero)
            {
                RuntimeLog.Error($"创建透明光标失败（GetLastError={Marshal.GetLastWin32Error()}）");
            }

            return handle;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("创建透明光标失败", ex);
            return IntPtr.Zero;
        }
    }

    private static IntPtr CreateCore()
    {
        // 单色光标的掩码位图高度为 2×Size：上半是 AND 掩码，下半是 XOR 掩码。
        // AND=1 且 XOR=0 的像素表示「完全透明」，所以上半填 1、下半保持 0。
        int stride = Size / 8; // 32 位宽 = 4 字节
        int rows = Size * 2;
        var bits = new byte[rows * stride];
        for (int i = 0; i < Size * stride; i++)
        {
            bits[i] = 0xFF;
        }

        IntPtr bitsPointer = Marshal.AllocHGlobal(bits.Length);
        try
        {
            Marshal.Copy(bits, 0, bitsPointer, bits.Length);

            var info = new NativeMethods.ICONINFO
            {
                fIcon = 0, // 0 = 光标（非图标）
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = NativeMethods.CreateBitmap(Size, rows, 1, 1, bitsPointer),
                hbmColor = IntPtr.Zero, // 不提供颜色图 → 纯单色光标
            };

            if (info.hbmMask == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(bitsPointer);
        }
    }
}
