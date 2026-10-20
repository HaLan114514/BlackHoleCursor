using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

public class PerformanceMonitorTests
{
    /// <summary>模拟 App 中每秒一次的轮询。</summary>
    private static bool Feed(PerformanceMonitor monitor, double fps, double seconds, bool lens = true)
    {
        bool result = false;
        int steps = (int)Math.Round(seconds);
        for (int i = 0; i < steps; i++)
        {
            result |= monitor.Update(fps, 1.0, lens);
        }

        return result;
    }

    [Fact]
    public void HealthyFrameRate_NeverSuggests()
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, 60, seconds: 10));
        Assert.False(monitor.HasSuggested);
    }

    [Fact]
    public void BriefDip_DoesNotSuggest()
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, 30, seconds: 2));
        Assert.False(monitor.HasSuggested);
    }

    [Fact]
    public void SustainedLowFrameRate_SuggestsExactlyOnce()
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, 30, seconds: 2));
        Assert.False(monitor.HasSuggested);

        // 第 3 次采样（累计满 3 秒）才提示
        Assert.True(Feed(monitor, 30, seconds: 1));
        Assert.True(monitor.HasSuggested);

        // 持续低帧率不再重复提示
        Assert.False(Feed(monitor, 30, seconds: 30));
    }

    [Fact]
    public void Recovery_RearmsTheSuggestion()
    {
        var monitor = new PerformanceMonitor();

        Assert.True(Feed(monitor, 30, seconds: 3));
        Assert.True(monitor.HasSuggested);

        Feed(monitor, 60, seconds: 2);
        Assert.False(monitor.HasSuggested);

        Assert.True(Feed(monitor, 30, seconds: 3));
    }

    [Fact]
    public void LensDisabled_IsNotMonitored()
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, 20, seconds: 20, lens: false));
        Assert.False(monitor.HasSuggested);
        Assert.Equal(0, monitor.LowDuration);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void MissingSamples_AreIgnored(double fps)
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, fps, seconds: 20));
        Assert.False(monitor.HasSuggested);
    }

    [Fact]
    public void ExactlyAtThreshold_CountsAsHealthy()
    {
        var monitor = new PerformanceMonitor();

        Assert.False(Feed(monitor, PerformanceMonitor.LowFramesPerSecond, seconds: 20));
    }

    [Fact]
    public void Reset_ClearsAccumulatedState()
    {
        var monitor = new PerformanceMonitor();
        Feed(monitor, 30, seconds: 2);
        Assert.True(monitor.LowDuration > 0);

        monitor.Reset();

        Assert.Equal(0, monitor.LowDuration);
        Assert.False(monitor.HasSuggested);
    }
}
