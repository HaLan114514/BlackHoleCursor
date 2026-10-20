using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M2.3：<c>HoleSize</c> 是「占屏幕高度的比例」，而着色器工作在视口 uv 上。
/// 这组测试锁住「屏高 → 视口高度」的换算，避免黑洞大小随覆盖层窗口尺寸漂移。
/// </summary>
public class ScreenHeightMappingTests
{
    private static SceneConstants Build(double width, double height, double screenHeight, double holeSize = 0.06)
    {
        var look = new BlackHoleLook { HoleSize = holeSize };
        var description = new SceneDescription(
            width, height, width / 2.0, height / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: screenHeight);

        return SceneParamsBuilder.Build(description);
    }

    [Fact]
    public void WithoutScreenHeight_HoleSizeIsUsedAsViewportFraction()
    {
        var constants = Build(400, 400, 0.0);

        Assert.Equal(0.06f, constants.Hole.X, 5);
    }

    /// <summary>
    /// 同一个 HoleSize、同一个视口，屏高越大 → 黑洞在该视口里占的比例越大。
    /// 这正是「按屏高而非视口高度计算」的含义。
    /// </summary>
    [Fact]
    public void LargerScreenHeight_ProducesLargerHoleInTheSameViewport()
    {
        var small = Build(400, 400, 720.0);
        var large = Build(400, 400, 1440.0);

        Assert.True(large.Hole.X > small.Hole.X,
            $"屏高越大黑洞越大：720 → {small.Hole.X}，1440 → {large.Hole.X}");
    }

    [Fact]
    public void ScreenHeightConversion_MatchesExpectedFraction()
    {
        // HoleSize 0.06、屏高 1080 → 阴影半径 64.8px；窗口高 400 → 占 0.162
        var constants = Build(400, 400, 1080.0);

        Assert.Equal(0.162f, constants.Hole.X, 4);
    }

    /// <summary>W 必须与换算后的半径一致（W = B_CRIT / 半径），否则测地线尺度会错。</summary>
    [Fact]
    public void WorldScale_MatchesTheConvertedRadius()
    {
        var constants = Build(400, 400, 1080.0);
        float expected = (float)(LensMath.CriticalImpactParameter / constants.Hole.X);

        Assert.Equal(expected, constants.Hole.Y, 3);
    }

    [Fact]
    public void HoleFraction_IsClampedToSaneRange()
    {
        var constants = Build(40, 40, 4000.0, holeSize: 0.15);

        Assert.InRange(constants.Hole.X, 1e-4f, 4.0f);
    }
}
