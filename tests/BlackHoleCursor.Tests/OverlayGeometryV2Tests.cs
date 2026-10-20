using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M2.2：v2 覆盖层窗口半径的纯函数测试。
/// 核心风险（R5）是窗口太小把盘外缘 / 远场过渡裁掉，所以这里逐项断言「能装得下」。
/// </summary>
public class OverlayGeometryV2Tests
{
    private const double Epsilon = 1e-6;

    [Fact]
    public void DiskOuterRadiusPixels_UsesCriticalImpactParameterAsScale()
    {
        // b_crit = 3√3/2 ≈ 2.598 r_s 对应阴影半径，所以 2.598 r_s 处应恰好等于阴影半径
        double atCritical = OverlayGeometry.DiskOuterRadiusPixels(LensMath.CriticalImpactParameter, 100.0);

        Assert.Equal(100.0, atCritical, 6);
    }

    [Fact]
    public void DiskOuterRadiusPixels_ScalesLinearly()
    {
        double inner = OverlayGeometry.DiskOuterRadiusPixels(3.0, 60.0);
        double outer = OverlayGeometry.DiskOuterRadiusPixels(6.0, 60.0);

        Assert.Equal(inner * 2.0, outer, 6);
    }

    [Fact]
    public void Radius_GrowsWithShadowRadius()
    {
        double small = OverlayGeometry.ComputeV2WindowRadius(30.0, 80.0);
        double large = OverlayGeometry.ComputeV2WindowRadius(90.0, 80.0);

        Assert.True(large > small, $"阴影越大窗口越大：{small} → {large}");
    }

    /// <summary>窗口必须能装下整个盘（含余量），否则盘的外缘会被矩形裁掉。</summary>
    [Fact]
    public void Radius_AlwaysContainsTheDisk()
    {
        double diskOuter = 174.0;
        double radius = OverlayGeometry.ComputeV2WindowRadius(64.8, diskOuter);

        Assert.True(radius >= diskOuter * OverlayGeometry.DiskMarginFactor - Epsilon,
            $"窗口半径 {radius} 必须容纳盘外径 {diskOuter}（含余量）");
    }

    /// <summary>窗口必须让远场过渡展开，否则会出现硬边。</summary>
    [Fact]
    public void Radius_CoversFarFieldTransition()
    {
        double shadow = 64.8;
        double radius = OverlayGeometry.ComputeV2WindowRadius(shadow, 40.0);

        Assert.True(radius >= shadow * OverlayGeometry.FarFieldRadiusFactor,
            $"窗口半径 {radius} 应覆盖远场过渡 {shadow * OverlayGeometry.FarFieldRadiusFactor}");
    }

    /// <summary>
    /// v3（P1.5）：放大系数必须真的放大影响半径。
    /// 旧名 `PeakScale_EnlargesTheWindow` —— 那时这是给 N4 过冲**预留窗口尺寸**用的；
    /// 现在窗口固定为整块显示器，这个系数只剩截图补偿一个用途（补丁按同比例放大）。
    /// </summary>
    [Fact]
    public void Scale_EnlargesTheInfluenceRadius()
    {
        double normal = OverlayGeometry.ComputeV2WindowRadius(64.8, 174.0, 1.0);
        double scaled = OverlayGeometry.ComputeV2WindowRadius(64.8, 174.0, 1.2);

        Assert.True(scaled > normal, $"放大系数越大半径越大：{normal} → {scaled}");
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.06)]
    [InlineData(0.15)]
    public void Radius_HasLowerBound_ForAllHoleSizes(double holeSize)
    {
        double shadow = holeSize * 1080.0;
        double disk = OverlayGeometry.DiskOuterRadiusPixels(7.0, shadow);
        double radius = OverlayGeometry.ComputeV2WindowRadius(shadow, disk, 1.2);

        // v3（P1.5）：1024 上界已取消，只保留下界（避免退化成 0）
        Assert.True(radius >= 8.0, $"影响半径应有下界，实际 {radius:F1}");
    }

    [Fact]
    public void Radius_SurvivesDegenerateInputs()
    {
        double zero = OverlayGeometry.ComputeV2WindowRadius(0.0, 0.0, 1.0);
        double negative = OverlayGeometry.ComputeV2WindowRadius(-50.0, -10.0, 0.0);

        Assert.True(zero >= 8.0);
        Assert.True(negative >= 8.0);
    }

    // ⚠️ v3（P1.5）已删除 `WindowRect_IsCenteredOnTheHole`：
    // `ComputeWindowRect` 随「跟随光标的小窗口」一起删除了，窗口现在是整块显示器。
}
