namespace BlackHoleCursor.Core.Screenshots;

/// <summary>
/// M4.1：判断剪贴板里的位图是否属于「需要补偿的全屏截图」（prd-v2 F8-2）。
///
/// 只处理**尺寸与虚拟桌面一致**的全屏位图：
/// 区域截图、窗口截图、以及保存到磁盘的文件都不处理 ——
/// 那些图里的黑洞位置无法可靠还原，硬补只会补错地方。
/// </summary>
public static class ScreenshotPolicy
{
    /// <summary>
    /// 尺寸容差（像素）。PrtScn 走 DIB，某些第三方工具走 PNG 再解码，
    /// 缩放取整会让全屏图差一两个像素，卡死在「完全相等」会漏掉这些情况。
    /// </summary>
    public const int DimensionTolerance = 2;

    /// <summary>是否是一张覆盖整个虚拟桌面的截图。</summary>
    public static bool IsFullScreen(int width, int height, int virtualWidth, int virtualHeight)
    {
        if (width <= 0 || height <= 0 || virtualWidth <= 0 || virtualHeight <= 0)
        {
            return false;
        }

        return Math.Abs(width - virtualWidth) <= DimensionTolerance
            && Math.Abs(height - virtualHeight) <= DimensionTolerance;
    }

    /// <summary>
    /// 是否应当补偿：开关打开 **且** 是全屏图。
    /// 拆成独立方法是为了让「开关关掉后什么都不做」这条能被单独断言（M4.3）。
    /// </summary>
    public static bool ShouldCompensate(
        bool enabled,
        int width,
        int height,
        int virtualWidth,
        int virtualHeight)
        => enabled && IsFullScreen(width, height, virtualWidth, virtualHeight);
}
