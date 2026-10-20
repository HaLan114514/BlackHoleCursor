using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 问题 5（抖动那一半）：整屏抓屏**不能逐帧做**。
///
/// 背景：为了消灭抖动，抓屏改成「整块显示器固定抓取」（纹理锚在显示器上，
/// 黑洞怎么动都不影响它）。但本机 GDI 抓 1080p 全屏要 ~40ms，占整帧 95%，
/// 把帧率压到 ~20FPS —— 黑洞只能以 20Hz 更新，鼠标一动就一格一格地跳，
/// 观感上就是「左右摇摆」。节流后实测 20 → 44 FPS。
///
/// 关键前提（也是节流成立的原因）：**只有桌面内容变了才需要重抓**；
/// 黑洞/窗口移动不需要重抓，因为纹理锚在显示器坐标上。
/// </summary>
public class CaptureThrottleTests
{
    [Fact]
    public void FreeCapture_RefreshesAtMostEveryFrame()
    {
        // 抓屏几乎不要钱时，不该被无谓节流 —— 最多每帧抓一次
        double interval = OverlayController.CaptureRefreshInterval(0.0);

        Assert.Equal(OverlayController.CaptureRefreshMinSeconds, interval, 6);
    }

    [Fact]
    public void ExpensiveCapture_BacksOffProportionally()
    {
        // 间隔 = 上次抓屏耗时 × 系数：本机整屏 GDI 实测 ~40ms → 40ms 一抓（25Hz）
        double interval = OverlayController.CaptureRefreshInterval(40.0);

        Assert.Equal(0.040, interval, 6);
    }

    /// <summary>再贵也不会超过上限（10Hz），否则桌面内容会明显滞后。</summary>
    [Fact]
    public void AbsurdlyExpensiveCapture_IsCappedAtTheUpperLimit()
    {
        double interval = OverlayController.CaptureRefreshInterval(1000.0);

        Assert.Equal(OverlayController.CaptureRefreshMaxSeconds, interval, 6);
    }

    [Theory]
    [InlineData(1.0, 10.0)]
    [InlineData(10.0, 20.0)]
    [InlineData(20.0, 30.0)]
    public void Interval_GrowsWithCaptureCost(double cheaper, double costlier)
    {
        double cheap = OverlayController.CaptureRefreshInterval(cheaper);
        double costly = OverlayController.CaptureRefreshInterval(costlier);

        Assert.True(costly >= cheap, $"抓屏越贵间隔应越长：{cheap} → {costly}");
    }

    /// <summary>间隔必须落在 [1/60, 1/10] 内：太快会拖垮帧率，太慢桌面内容会明显滞后。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(40.0)]
    [InlineData(500.0)]
    public void Interval_IsAlwaysClamped(double costMs)
    {
        double interval = OverlayController.CaptureRefreshInterval(costMs);

        Assert.InRange(interval, OverlayController.CaptureRefreshMinSeconds, OverlayController.CaptureRefreshMaxSeconds);
    }
}
