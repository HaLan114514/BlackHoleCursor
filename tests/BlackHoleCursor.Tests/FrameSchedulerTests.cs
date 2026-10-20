using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

public class FrameSchedulerTests
{
    [Fact]
    public void Disabled_NeverRenders()
    {
        var scheduler = new FrameScheduler();

        Assert.False(scheduler.ShouldRender(enabled: false, moving: true, new SettingsModel()));
    }

    [Fact]
    public void FirstCall_AlwaysRenders()
    {
        var scheduler = new FrameScheduler();

        Assert.True(scheduler.ShouldRender(enabled: true, moving: true, new SettingsModel()));
    }

    [Fact]
    public void ImmediateSecondCall_IsThrottled()
    {
        var scheduler = new FrameScheduler();

        scheduler.ShouldRender(true, true, new SettingsModel());

        Assert.False(scheduler.ShouldRender(true, true, new SettingsModel()));
    }

    [Fact]
    public void Moving_TargetsActiveFrameRate()
    {
        var scheduler = new FrameScheduler();
        scheduler.ShouldRender(true, true, new SettingsModel());

        Assert.Equal(FrameScheduler.ActiveFramesPerSecond(new SettingsModel()), scheduler.TargetFramesPerSecond);
        Assert.False(scheduler.IsIdle);
    }

    [Fact]
    public void IdleWithNoAnimation_TargetsIdleFrameRate()
    {
        var settings = new SettingsModel { DiskSpeed = 0 };
        var scheduler = new FrameScheduler();

        scheduler.ShouldRender(true, false, settings);

        Assert.Equal(FrameScheduler.IdleFramesPerSecond, scheduler.TargetFramesPerSecond);
        Assert.True(scheduler.IsIdle);
    }

    [Fact]
    public void IdleButAnimating_StaysAtActiveFrameRate()
    {
        var settings = new SettingsModel { DiskSpeed = 5.0 };
        var scheduler = new FrameScheduler();

        scheduler.ShouldRender(true, false, settings);

        Assert.Equal(FrameScheduler.ActiveFramesPerSecond(settings), scheduler.TargetFramesPerSecond);
        Assert.False(scheduler.IsIdle);
    }

    [Fact]
    public void AfterReset_FramesAreAllowedAgain()
    {
        var scheduler = new FrameScheduler();
        scheduler.ShouldRender(true, true, new SettingsModel());

        scheduler.Reset();

        Assert.True(scheduler.ShouldRender(true, true, new SettingsModel()));
    }

    [Fact]
    /// <summary>v2 的条纹速度是 DiskSpeed，负值（反向公转）同样算「在动」。</summary>
    public void HasAnimation_ReflectsDiskSpeed()
    {
        Assert.True(FrameScheduler.HasAnimation(new SettingsModel { DiskSpeed = 1 }));
        Assert.True(FrameScheduler.HasAnimation(new SettingsModel { DiskSpeed = -1 }));
        Assert.False(FrameScheduler.HasAnimation(new SettingsModel { DiskSpeed = 0 }));
    }

    [Fact]
    public void ForceActive_KeepsActiveFrameRateWhileIdle()
    {
        var settings = new SettingsModel { DiskSpeed = 0 };
        var scheduler = new FrameScheduler();

        scheduler.ShouldRender(true, moving: false, settings, forceActive: true);

        Assert.Equal(FrameScheduler.ActiveFramesPerSecond(settings), scheduler.TargetFramesPerSecond);
        Assert.True(scheduler.IsIdle == false);
    }

    [Fact]
    public void ForceActiveFalse_StillDropsToIdle()
    {
        var settings = new SettingsModel { DiskSpeed = 0 };
        var scheduler = new FrameScheduler();

        scheduler.ShouldRender(true, moving: false, settings, forceActive: false);

        Assert.Equal(FrameScheduler.IdleFramesPerSecond, scheduler.TargetFramesPerSecond);
    }
}
