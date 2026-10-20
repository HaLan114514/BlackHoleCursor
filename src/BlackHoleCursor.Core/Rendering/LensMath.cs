namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 引力透镜的几何/强度计算。C# 与 HLSL 共用同一套定义：
/// C# 只负责算好「传给着色器的常量」，HLSL 里的公式与这里一一对应。
///
/// M1.8.3：v1 的位移近似 / 光子环 / 渐隐窗口一套已随旧着色器删除，
/// 这里只留下 v2 测地线渲染真正用到的换算。
/// </summary>
public static class LensMath
{
    // ======================= v2：测地线渲染的半径换算 =======================
    // 屏幕坐标以「屏高」为单位（uv），阴影的角尺寸为 B_CRIT·r_s，
    // 于是 屏幕单位 ↔ 世界(r_s) 的换算系数 W = B_CRIT / rh。
    // 参考实现 singularity.wgsl：
    //   let W = B_CRIT / max(rh, 1e-4);  let pr = rot(...) * W;  let b = length(pr);

    /// <summary>临界撞击参数 b_crit = (3√3/2)·r_s ≈ 2.598 r_s。</summary>
    public const double CriticalImpactParameter = 2.5980762;

    /// <summary>天空平面（桌面）到黑洞的距离，单位 r_s。</summary>
    public const double LensDepth = 13.0;

    /// <summary>阴影半径（屏高单位）→ 世界换算系数 W。</summary>
    public static double WorldScale(double shadowRadiusScreenUnits)
        => CriticalImpactParameter / Math.Max(shadowRadiusScreenUnits, 1e-4);

    /// <summary>阴影半径的物理像素数（HoleSize × 屏幕高度）。</summary>
    public static double ShadowRadiusPixels(double holeSize, double screenHeight)
        => Math.Max(holeSize, 0.0) * Math.Max(screenHeight, 1.0);

    /// <summary>相机到黑洞的距离（r_s）：需容纳盘外径。</summary>
    public static double CameraDistance(double outerRadius)
        => Math.Max(14.0, outerRadius + 5.0);

    /// <summary>超过该撞击参数的光线不可能碰到盘，可走解析远场分支。</summary>
    public static double MaxImpactParameter(double outerRadius) => outerRadius + 3.0;
}
