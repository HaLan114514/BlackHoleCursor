using System.Drawing;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M5.2（prd-v2 F2-5）：预设切换的 1 秒 smoothstep 交叉淡入。
///
/// 全部用**虚拟时钟**驱动（<see cref="LookFade"/> 的 clock 参数）：
/// 不真实等待，进度可精确到任意时刻，也不会因为机器快慢而偶发失败。
/// </summary>
public class LookFadeTests
{
    /// <summary>可手动推进的时钟，行为与 M3.1 里锁 B1 用的虚拟时钟一致。</summary>
    private sealed class VirtualClock
    {
        public double Now { get; set; }

        public Func<double> Func => () => Now;
    }

    private static BlackHoleLook Look(double temp, int steps = 48)
        => new() { TempKelvin = temp, StepCount = steps };

    [Fact]
    public void WithoutBegin_BlendReturnsTheTargetAsIs()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);
        var target = Look(9000);

        Assert.False(fade.IsActive);
        Assert.Same(target, fade.Blend(target));
    }

    [Fact]
    public void AtTheStart_TheRenderedLookIsStillTheOldOne()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);
        var start = Look(3000);

        fade.Begin(start);
        var blended = fade.Blend(Look(15000));

        Assert.True(fade.IsActive);
        Assert.Equal(3000.0, blended.TempKelvin, 6);
    }

    [Fact]
    public void HalfWayThrough_TheLookIsHalfWayBetween()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000));
        clock.Now += 0.5;

        // smoothstep(0.5) = 0.5，正好是中点的算术平均
        var blended = fade.Blend(Look(15000));

        Assert.Equal(0.5, fade.Progress, 6);
        Assert.Equal(9000.0, blended.TempKelvin, 6);
    }

    [Fact]
    public void AfterOneSecond_TheFadeEndsAndYieldsTheTarget()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000));
        clock.Now += 0.999;
        Assert.True(fade.IsActive, "差 1ms 到点，仍在淡入中");

        clock.Now += 0.002;
        fade.Step();

        Assert.False(fade.IsActive);
        Assert.Equal(15000.0, fade.Blend(Look(15000)).TempKelvin, 6);
    }

    [Fact]
    public void TheDefaultDurationIsOneSecond()
    {
        Assert.Equal(1.0, LookFade.DefaultDurationSeconds, 9);
        Assert.Equal(1.0, new LookFade().DurationSeconds, 9);
    }

    /// <summary>
    /// 覆盖层渲染循环与主界面预览共用同一个实例，各自每帧调一次 <see cref="LookFade.Step"/>。
    /// 若进度靠「累计 delta」，淡入就会被推到约 2 倍速 —— 这条把绝对时钟的做法锁住。
    /// </summary>
    [Fact]
    public void ManyCallersStepping_DoesNotSpeedUpTheFade()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000));

        for (int i = 0; i < 20; i++)
        {
            fade.Step();
        }

        Assert.Equal(0.0, fade.Progress, 6);
        Assert.True(fade.IsActive, "时钟没走，就不该结束");

        clock.Now += 0.25;

        for (int i = 0; i < 20; i++)
        {
            fade.Step();
        }

        Assert.Equal(0.25, fade.Progress, 6);
    }

    /// <summary>
    /// 目标外观**每帧实时取**：淡入途中用户再拖滑块，收敛到的是新目标而不是 Begin 时的快照。
    /// </summary>
    [Fact]
    public void TheTargetIsFetchedLive_NotSnapshottedAtBegin()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000));
        clock.Now += 0.5;

        var moved = fade.Blend(Look(11000));

        Assert.Equal(7000.0, moved.TempKelvin, 6);

        clock.Now += 0.0;
        var redirected = fade.Blend(Look(20000));

        Assert.Equal(11500.0, redirected.TempKelvin, 6);
    }

    [Fact]
    public void BeginSnapshotsTheStartLook()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);
        var start = Look(3000);

        fade.Begin(start);
        start.TempKelvin = 99999; // 之后改起点实例不该影响淡入

        clock.Now += 0.5;
        Assert.Equal(9000.0, fade.Blend(Look(15000)).TempKelvin, 6);
    }

    /// <summary>质量档位是离散的，中间值没有意义 —— 直接取目标档位（最多跳一次）。</summary>
    [Fact]
    public void StepCountIsNotInterpolated()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000, steps: 24));
        clock.Now += 0.5;

        var blended = fade.Blend(Look(15000, steps: 88));

        Assert.Equal(88, blended.StepCount);
    }

    [Fact]
    public void TintIsInterpolatedPerChannel()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        var from = new BlackHoleLook { Tint = Color.FromArgb(255, 0, 0, 0) };
        var to = new BlackHoleLook { Tint = Color.FromArgb(255, 200, 100, 50) };

        fade.Begin(from);
        clock.Now += 0.5;

        var blended = fade.Blend(to);

        Assert.Equal(100, blended.Tint.R);
        Assert.Equal(50, blended.Tint.G);
        Assert.Equal(25, blended.Tint.B);
    }

    [Fact]
    public void Cancel_FallsBackToTheTargetImmediately()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(clock: clock.Func);

        fade.Begin(Look(3000));
        clock.Now += 0.3;
        fade.Cancel();

        Assert.False(fade.IsActive);
        Assert.Equal(15000.0, fade.Blend(Look(15000)).TempKelvin, 6);
    }

    [Fact]
    public void ZeroDuration_GoesStraightToTheTarget()
    {
        var clock = new VirtualClock();
        var fade = new LookFade(0.0, clock.Func);

        fade.Begin(Look(3000));
        fade.Step();

        Assert.False(fade.IsActive);
        Assert.Equal(15000.0, fade.Blend(Look(15000)).TempKelvin, 6);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.15625)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 0.84375)]
    [InlineData(1.0, 1.0)]
    public void SmoothStep_MatchesTheTextbookCurve(double t, double expected)
    {
        Assert.Equal(expected, LookFade.SmoothStep(t), 6);
    }

    [Fact]
    public void SmoothStep_IsMonotonicAndStaysInRange()
    {
        double previous = -1.0;

        for (double t = 0.0; t <= 1.0001; t += 0.05)
        {
            double value = LookFade.SmoothStep(t);
            Assert.InRange(value, 0.0, 1.0);
            Assert.True(value > previous, $"t={t} 处不单调");
            previous = value;
        }

        // 越界输入被钳住，不会吐出奇怪的插值系数
        Assert.Equal(0.0, LookFade.SmoothStep(-5.0), 9);
        Assert.Equal(1.0, LookFade.SmoothStep(5.0), 9);
    }
}
