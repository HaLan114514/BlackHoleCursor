namespace BlackHoleCursor.Core.Rendering;

/// <summary>覆盖层窗口的几何计算（纯函数，可单元测试）。</summary>
public static class OverlayGeometry
{
    /// <summary>
    /// v2：远场淡出需要多少倍阴影半径才算走完。
    /// 着色器的淡出窗口是 <c>exp(-(plen/7)²)</c>，到 6 倍阴影半径处约 0.48，
    /// 剩下的交给窗口边缘羽化收尾（见 CompositePS 的 edge 项），避免被矩形裁出硬边。
    /// </summary>
    public const double FarFieldRadiusFactor = 6.0;

    /// <summary>盘外径再留一点余量，避免盘的最外圈贴着窗口边。</summary>
    public const double DiskMarginFactor = 1.15;

    /// <summary>
    /// 「黑洞影响范围」的半径：同时容纳**盘外径**与**远场过渡**。
    ///
    /// ⚠️ v3（P1.1）起它**不再决定窗口尺寸** —— 窗口已经固定为整块显示器。
    /// 现在只剩一个用途：截图补偿（M4）用它算补丁的边长
    /// （`ScreenshotCompositor.PatchExtent`），保证补回去的黑洞与桌面上一样大。
    ///
    /// 原先的两个约束随之消失：
    /// ① 不再按 N4 过冲峰值预留 —— 过冲缩放的是 `HoleSize` 本身
    ///    （`SceneConstants` 里 `holeFraction *= d.Scale`），窗口不缩放也就不用留余量；
    /// ② 不再受 1024 边长上限约束 —— 那是 PRD 给「跟随光标的小窗口」定的性能上限，
    ///    P0 实测整屏 1920×1080 仍有 56~57 FPS，该前提已不成立。
    /// </summary>
    /// <param name="shadowRadiusPx">阴影半径（像素，已按屏幕高度换算）。</param>
    /// <param name="diskOuterPx">盘外径（像素）。</param>
    /// <param name="scale">整体放大系数（不传按 1.0）。</param>
    public static double ComputeV2WindowRadius(double shadowRadiusPx, double diskOuterPx, double scale = 1.0)
    {
        double shadow = Math.Max(shadowRadiusPx, 1.0);
        double disk = Math.Max(diskOuterPx, 0.0) * DiskMarginFactor;
        double factor = Math.Clamp(scale, 0.1, 4.0);

        return Math.Max(Math.Max(disk, shadow * FarFieldRadiusFactor) * factor + 8.0, 8.0);
    }

    /// <summary>盘外径（r_s 单位）换算成像素：以临界撞击参数 B_CRIT 对应阴影半径为基准。</summary>
    public static double DiskOuterRadiusPixels(double outerRs, double shadowRadiusPx)
        => outerRs / LensMath.CriticalImpactParameter * Math.Max(shadowRadiusPx, 0.0);

    /// <summary>
    /// fixes-v2 问题 3（续）：预览框代表屏幕高度的这一比例。
    ///
    /// 0.5 = 预览框覆盖「半个屏高」—— 默认洞径（0.06）在框里恰好是合适的可见大小，
    /// 而拖「阴影大小」滑块时框里的洞会**真的变大变小**
    ///（这是上一版「缩略图」缺的大小反馈：它把整块窗口等比塞进框，尺度无关，
    /// 洞设多大看起来都一样，洞一大就基本没用）。
    /// </summary>
    public const double PreviewScreenSpan = 0.5;

    /// <summary>
    /// fixes-v2 问题 3（续）：预览的**混合**取景。
    ///
    /// 1）默认按**真实比例**：框代表屏幕上固定一块区域，于是洞越大、框里越大（有大小反馈）；
    /// 2）盘的外圈装不下时才整体缩小到刚好装下，并把缩小倍率返回给 UI 标注（例如「×0.62」）。
    ///
    /// 为什么用「盘外圈」而不是「整块覆盖层窗口」来判装不下：
    /// 窗口半径是阴影半径的 6 倍，按它判会让任何洞径都被缩到同一个大小 —— 那就又退化成缩略图了。
    /// 盘才是用户真正要看的造型主体，远场光晕允许被框裁掉
    /// （着色器的窗口边缘羽化本来就跟着 `Hole.x` 走，正好在框边收完，不会出现硬矩形边）。
    /// </summary>
    /// <returns>预览应使用的 <c>HoleSize</c>（占预览视口高度的比例）与缩小倍率（≤ 1）。</returns>
    public static (double HoleSize, double Scale) PreviewFit(
        BlackHoleLook look,
        int previewSize,
        double screenSpan = PreviewScreenSpan)
    {
        double size = Math.Max(previewSize, 8);
        double span = Math.Clamp(screenSpan, 0.05, 4.0);

        // 真实比例：阴影半径占**预览视口**高度的比例 = 屏高占比 ÷ 框覆盖的屏高比例
        double hole = look.HoleSize / span;

        double shadowPx = hole * size;
        double diskPx = DiskOuterRadiusPixels(look.OuterRadius, shadowPx) * DiskMarginFactor;
        double half = size / 2.0;

        double scale = diskPx > half ? half / diskPx : 1.0;

        return (hole * scale, scale);
    }

    /// <summary>
    /// fixes-v2 问题 5：窗口局部 uv → 抓屏纹理 uv 的仿射映射。
    ///
    /// v3（P1.1）：窗口 == 整块显示器，所以这个映射**恒等**（scale=1、offset=0）。
    /// 保留它是因为预览与单元测试仍可能传入不同的窗口矩形。
    ///
    /// 抓屏改为「整块显示器固定抓取」之后，纹理不再是窗口那一块，
    /// 所以着色器取样前必须先把窗口 uv 换算成显示器 uv：
    ///   uvTexture = uvWindow · (窗口尺寸 / 抓取尺寸) + (窗口原点 − 抓取原点) / 抓取尺寸
    ///
    /// 纯函数，便于单测；抓取矩形由调用方（<see cref="OverlayController"/>）按光标所在显示器给出。
    /// </summary>
    public static (double ScaleX, double ScaleY, double OffsetX, double OffsetY) CaptureMap(
        double windowLeft, double windowTop, double windowWidth, double windowHeight,
        double captureLeft, double captureTop, double captureWidth, double captureHeight)
    {
        double w = Math.Max(captureWidth, 1.0);
        double h = Math.Max(captureHeight, 1.0);

        return (windowWidth / w,
                windowHeight / h,
                (windowLeft - captureLeft) / w,
                (windowTop - captureTop) / h);
    }

    /// <summary>
    /// 黑洞在覆盖层窗口内的局部坐标（像素）。
    ///
    /// v3（P1.2）：窗口是**整块显示器且永不移动**，所以这里直接返回
    /// 「跟随位置 − 窗口原点」，**连续浮点、不做任何量化**。
    ///
    /// 这正是 fixes-v2 问题 7 的最终解法：窗口不动 ⇒ 没有 Win32 整数像素约束，
    /// 于是「黑洞位置亚像素精确」与「折射图案稳定」**可以同时成立** ——
    /// 小窗口架构下这两者互斥（见 prd-v3 §1.2 的推导），只能二选一。
    ///
    /// 历史：问题 7 期间这里曾返回**窗口中心的恒定值**来换取折射图案稳定，
    /// 代价是黑洞整体位置只能按 1px 步进。窗口固定之后，这个代价没必要再付。
    /// </summary>
    public static (double X, double Y) LocalCenter(double centerX, double centerY, in Rect window)
        => (centerX - window.Left, centerY - window.Top);

    /// <summary>
    /// v3（P1.3）：全黑帧检测的采样原点，单位是**抓屏纹理**坐标系。
    ///
    /// 抓屏纹理是整块显示器，所以要减去显示器原点；采样框以**黑洞**为中心 ——
    /// 判「桌面是不是全黑」要对着黑洞看才有意义。
    /// 旧实现按「窗口中心」算，而窗口 v3 起就是整块显示器 ⇒ 会永远落在屏幕正中，
    /// 黑洞移到边上时检测的就是别处了。
    ///
    /// 越界不需要在这里处理：<c>UpdateBlackFrameState</c> 会把采样矩形钳进纹理范围
    /// （黑洞贴屏幕边时采样框会被推回纹理内）。
    /// </summary>
    public static (int X, int Y) BlackFrameSampleOrigin(
        double holeX, double holeY, int sampleSize, int monitorLeft, int monitorTop)
    {
        int size = Math.Max(sampleSize, 1);

        return ((int)holeX - (size / 2) - monitorLeft,
                (int)holeY - (size / 2) - monitorTop);
    }

    /// <summary>整数矩形。</summary>
    public readonly record struct Rect(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;

        public int Bottom => Top + Height;
    }
}
