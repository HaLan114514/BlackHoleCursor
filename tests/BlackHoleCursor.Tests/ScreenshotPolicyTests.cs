using BlackHoleCursor.Core.Screenshots;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M4.1（prd-v2 F8-2）：只补偿**全屏截图**，区域截图与磁盘文件不处理。
/// </summary>
public class ScreenshotPolicyTests
{
    private const int ScreenW = 2560;
    private const int ScreenH = 1440;

    [Fact]
    public void FullScreenImage_MatchesTheVirtualDesktop()
    {
        Assert.True(ScreenshotPolicy.IsFullScreen(ScreenW, ScreenH, ScreenW, ScreenH));
    }

    /// <summary>取整 / 缩放导致的 1~2px 偏差仍应算全屏，否则会漏掉第三方截图工具。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void SmallDimensionRounding_StillCountsAsFullScreen(int delta)
    {
        Assert.True(ScreenshotPolicy.IsFullScreen(ScreenW + delta, ScreenH, ScreenW, ScreenH));
    }

    [Fact]
    public void RegionCapture_IsIgnored()
    {
        Assert.False(ScreenshotPolicy.IsFullScreen(800, 600, ScreenW, ScreenH));
    }

    /// <summary>只截了一个窗口（尺寸明显小于桌面）。</summary>
    [Fact]
    public void WindowCapture_IsIgnored()
    {
        Assert.False(ScreenshotPolicy.IsFullScreen(1600, 900, ScreenW, ScreenH));
    }

    /// <summary>
    /// 宽高互换不算全屏：多显示器下横向排列与纵向排列的虚拟桌面尺寸会被混淆，
    /// 放过去就会把黑洞补到错误的位置。
    /// </summary>
    [Fact]
    public void SwappedDimensions_AreNotFullScreen()
    {
        Assert.False(ScreenshotPolicy.IsFullScreen(ScreenH, ScreenW, ScreenW, ScreenH));
    }

    /// <summary>拿不到虚拟桌面尺寸（查询失败返回 0）时一律不补，避免盲目合成。</summary>
    [Theory]
    [InlineData(0, 1440)]
    [InlineData(2560, 0)]
    [InlineData(0, 0)]
    public void UnknownScreenSize_IsNeverFullScreen(int virtualWidth, int virtualHeight)
    {
        Assert.False(ScreenshotPolicy.IsFullScreen(2560, 1440, virtualWidth, virtualHeight));
    }

    /// <summary>M4.3 的行为基础：开关关掉时，即便确实是全屏图也不补偿。</summary>
    [Fact]
    public void Disabled_NeverCompensatesEvenForFullScreen()
    {
        Assert.True(ScreenshotPolicy.ShouldCompensate(true, ScreenW, ScreenH, ScreenW, ScreenH));
        Assert.False(ScreenshotPolicy.ShouldCompensate(false, ScreenW, ScreenH, ScreenW, ScreenH));
    }

    [Fact]
    public void Enabled_ButRegionCapture_IsNotCompensated()
    {
        Assert.False(ScreenshotPolicy.ShouldCompensate(true, 800, 600, ScreenW, ScreenH));
    }
}
