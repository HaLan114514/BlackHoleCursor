using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

/// <summary>
/// N4 出现 / 消失动画：弹性过冲的状态机。
/// 观感约定（已定案）：出现 0 → 1.1 → 1；消失是同一条曲线反向播放，即 1 → 1.1 → 0。
/// </summary>
public class TransitionStateTests
{
    private static readonly TransitionParameters Default = new(true, 0.35, 1.1);

    private static void Advance(TransitionState state, double seconds, in TransitionParameters p, double step = 1.0 / 60.0)
    {
        int frames = (int)Math.Ceiling(seconds / step);
        for (int i = 0; i < frames; i++)
        {
            state.Step(step, p);
        }
    }

    [Fact]
    public void AtRest_ShowsTheHoleCompletely()
    {
        var state = new TransitionState();

        Assert.Equal(1.0, state.Progress);
        Assert.True(state.IsFinished);
        Assert.False(state.IsActive);
        Assert.Equal(1.0, state.Fade);
    }

    [Fact]
    public void Appearing_ReachesFullWithinTheDuration()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);

        Assert.False(state.IsFinished);

        Advance(state, Default.DurationSeconds + 0.1, Default);

        Assert.Equal(1.0, state.Progress);
        Assert.True(state.IsFinished);
    }

    [Fact]
    public void Disappearing_ReachesZeroWithinTheDuration()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Disappearing, Default);

        Advance(state, Default.DurationSeconds + 0.1, Default);

        Assert.Equal(0.0, state.Progress);
        Assert.True(state.IsFinished, "消失动画播完后必须标记为结束，覆盖层才能收起窗口");
    }

    [Fact]
    public void Appearing_ScalesFromZeroUpToThePeak()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);

        // 起点：完全不可见
        Advance(state, 0.0, Default);
        Assert.Equal(0.0, state.Scale(Default), 6);

        // 峰值点：正好是过冲倍率
        Advance(state, Default.DurationSeconds * TransitionState.PeakAt, Default);
        Assert.Equal(Default.Overshoot, state.Scale(Default), 2);
    }

    [Fact]
    public void Scale_OvershootsBeyondOneThenSettles()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);

        double peak = 0.0;
        for (int i = 0; i < 200; i++)
        {
            state.Step(1.0 / 60.0, Default);
            peak = Math.Max(peak, state.Scale(Default));
        }

        Assert.True(peak > 1.05, $"必须真的冲过 1，实测峰值 {peak:F3}");
        Assert.Equal(Default.Overshoot, peak, 2);
        Assert.Equal(1.0, state.Scale(Default), 6);
    }

    [Fact]
    public void Disappearing_IsTheSameCurvePlayedBackwards()
    {
        // 消失 = 反向播放：中途同样会经过过冲峰值（先胀一下再收没）
        var state = new TransitionState();
        state.Begin(TransitionDirection.Disappearing, Default);

        double peak = 0.0;
        int frames = 0;
        while (!state.IsFinished && frames < 500)
        {
            state.Step(1.0 / 60.0, Default);
            peak = Math.Max(peak, state.Scale(Default));
            frames++;
        }

        Assert.True(peak > 1.05, $"消失时应先胀一下，实测峰值 {peak:F3}");
        Assert.Equal(Default.Overshoot, peak, 2);
        Assert.Equal(0.0, state.Scale(Default), 6);
        Assert.Equal(0.0, state.Fade, 6);
    }

    [Fact]
    public void ReversingMidFlight_ContinuesFromTheCurrentProgress()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);
        Advance(state, 0.2, Default);

        double midway = state.Progress;
        Assert.True(midway > 0.0 && midway < 1.0);

        state.Begin(TransitionDirection.Disappearing, Default);

        Assert.Equal(midway, state.Progress, 6);
        Assert.Equal(TransitionDirection.Disappearing, state.Direction);
        Assert.False(state.IsFinished);
    }

    [Fact]
    public void HugeTimeStep_IsClamped()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);

        // 卡顿 10 秒不应该一帧就把动画播完
        state.Step(10.0, Default);

        Assert.False(state.IsFinished);
        Assert.True(state.Progress < 0.2, $"单帧最多推进 MaxTimeStep，实际进度 {state.Progress:F3}");
    }

    [Fact]
    public void Disabled_JumpsStraightToTheEnd()
    {
        var off = new TransitionParameters(false, 0.35, 1.1);

        var appearing = new TransitionState();
        appearing.Begin(TransitionDirection.Appearing, off);
        Assert.True(appearing.IsFinished);
        Assert.Equal(1.0, appearing.Progress);

        var disappearing = new TransitionState();
        disappearing.Begin(TransitionDirection.Disappearing, off);
        Assert.True(disappearing.IsFinished, "关闭动画时必须立刻结束，否则收起窗口会被拖延");
        Assert.Equal(0.0, disappearing.Progress);
    }

    [Fact]
    public void Disabled_KeepsScaleAtOne()
    {
        var off = new TransitionParameters(false, 0.35, 1.1);
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, off);

        Assert.Equal(1.0, state.Scale(off), 6);
    }

    [Fact]
    public void LongerDuration_TakesMoreFrames()
    {
        int Count(double duration)
        {
            var state = new TransitionState();
            var p = new TransitionParameters(true, duration, 1.1);
            state.Begin(TransitionDirection.Appearing, p);

            int frames = 0;
            while (!state.IsFinished && frames < 1000)
            {
                state.Step(1.0 / 60.0, p);
                frames++;
            }

            return frames;
        }

        Assert.True(Count(1.0) > Count(0.2) * 2, "时长 5 倍，帧数应显著更多");
    }

    [Fact]
    public void Fade_IsSmoothedAtBothEnds()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);

        Assert.Equal(0.0, state.Fade, 6);

        Advance(state, Default.DurationSeconds + 0.1, Default);
        Assert.Equal(1.0, state.Fade, 6);
    }

    [Fact]
    public void Replay_RestartsFromZero()
    {
        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, Default);
        Advance(state, Default.DurationSeconds + 0.1, Default);
        Assert.True(state.IsFinished);

        state.Replay(Default);

        Assert.False(state.IsFinished);
        Assert.True(state.Progress < 0.05);
        Assert.Equal(TransitionDirection.Appearing, state.Direction);
    }

    // ⚠️ v3（P1.5）已删除 `PeakScale_IsUsedToReserveWindowSize`：
    // `TransitionState.PeakScale` 随「按过冲峰值预留窗口尺寸」一起删除了 ——
    // 窗口是整块显示器，不存在裁切，无需预留。
    //
    // 过冲本身**照旧生效**，改由下面这条断言守：它缩放的是 HoleSize，
    // 经 `SceneConstants` 的 `holeFraction *= d.Scale` 落到画面上。

    /// <summary>
    /// v3（P1.5）：出现动画的过冲体现在**视觉缩放**上（0 → 峰值 → 1），
    /// 而不是窗口尺寸。这条锁住「过冲参数确实进入了缩放曲线」。
    /// </summary>
    [Fact]
    public void Overshoot_ShowsUpInTheVisualScale()
    {
        var over = new TransitionParameters(true, 0.4, 1.3);
        var flat = new TransitionParameters(true, 0.4, 1.0);

        var state = new TransitionState();
        state.Begin(TransitionDirection.Appearing, over);

        // ⚠️ 必须**多步**推进：`Step` 会把 deltaTime 钳到 `MaxTimeStep`（0.05s），
        // 单次最多前进 0.05 / 0.4 = 0.125 的进度，而峰值在 `PeakAt` = 0.6。
        // 第一版写成 `Step(0.4 * PeakAt)` 想一步到位，结果只到进度 0.125、
        // Scale 算出 0.27 —— 看着像过冲失效，其实是测试没把动画推到位。
        for (int i = 0; i < 20 && state.Progress < TransitionState.PeakAt; i++)
        {
            state.Step(0.05, over);
        }

        double peak = state.Scale(over);
        Assert.True(peak > 1.0, $"过冲峰值处缩放应 > 1，实际 {peak:F2}");

        // 同样进度下，不带过冲的曲线不应超过 1
        Assert.True(state.Scale(flat) <= 1.0 + 1e-6);
    }

    [Fact]
    public void Defaults_AreEnabledWithSensibleValues()
    {
        var settings = new SettingsModel();
        var p = TransitionParameters.FromSettings(settings);

        Assert.True(p.Enabled);
        Assert.Equal(0.35, p.DurationSeconds);
        Assert.Equal(1.1, p.Overshoot);
    }

    [Fact]
    public void OutOfRangeValues_AreClampedBySettings()
    {
        var settings = new SettingsModel { TransitionDuration = 99, TransitionOvershoot = 0.2 };
        Assert.True(settings.Clamp());

        Assert.Equal(SettingsRanges.TransitionDurationMax, settings.TransitionDuration);
        Assert.Equal(SettingsRanges.TransitionOvershootMin, settings.TransitionOvershoot);
    }
}
