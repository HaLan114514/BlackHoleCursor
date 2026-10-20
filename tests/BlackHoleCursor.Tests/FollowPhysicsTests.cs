using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public class FollowPhysicsTests
{
    private static FollowParameters Create(
        bool enabled = true,
        double stiffness = 60,
        double damping = 16,
        double maxSpeed = 2500,
        double maxAccel = 60000,
        double snap = 1.5,
        bool adaptive = false)
        => new(enabled, stiffness, damping, maxSpeed, maxAccel, snap, adaptive);

    private static FollowState Simulate(FollowState state, double targetX, double targetY, double seconds, in FollowParameters p, double fps = 60)
    {
        double dt = 1.0 / fps;
        int steps = (int)(seconds * fps);
        for (int i = 0; i < steps; i++)
        {
            state.Step(targetX, targetY, dt, p);
        }

        return state;
    }

    [Fact]
    public void Disabled_MatchesMouseExactly()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create(enabled: false);

        state.Step(500, 300, 1.0 / 60, p);

        Assert.Equal(500, state.X);
        Assert.Equal(300, state.Y);
        Assert.Equal(0, state.Speed);
        Assert.True(state.IsSettled);
    }

    [Fact]
    public void ConvergesToTheTarget()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);

        Simulate(state, 300, 0, 2.0, Create());

        Assert.Equal(300, state.X, 1);
        Assert.Equal(0, state.Y, 1);
        Assert.True(state.IsSettled, "到达后应吸附并标记静止，避免持续运算");
    }

    [Fact]
    public void AcceleratesGradually_NotInstantly()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create();

        state.Step(300, 0, 1.0 / 60, p);
        double firstStep = state.X;

        Assert.True(firstStep > 0, "应该开始移动");
        Assert.True(firstStep < 300, "一帧内不应直接跳到目标，否则就没有加速过程");
        Assert.True(state.Speed > 0);
    }

    [Fact]
    public void MaxSpeed_IsEnforced()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create(maxSpeed: 200);

        state.Step(100000, 0, 1.0 / 60, p);
        double travelled = state.X;

        Assert.True(travelled <= (200 / 60.0) + 1e-6, $"单帧位移 {travelled} 超过限速允许的 {200 / 60.0}");
        Assert.True(state.Speed <= 200 + 1e-6, $"速度 {state.Speed} 超过上限");
    }

    [Fact]
    public void MaxAcceleration_IsEnforced()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create(maxAccel: 1000);

        state.Step(100000, 0, 1.0 / 60, p);

        Assert.True(state.Speed <= (1000 / 60.0) + 1e-6, $"速度 {state.Speed} 超过加速度限制");
    }

    [Fact]
    public void AdaptiveSpeed_AllowsFasterChaseForLongDistances()
    {
        var fixedState = new FollowState();
        fixedState.SnapTo(0, 0);
        Simulate(fixedState, 2000, 0, 0.2, Create(maxSpeed: 500, adaptive: false));

        var adaptiveState = new FollowState();
        adaptiveState.SnapTo(0, 0);
        Simulate(adaptiveState, 2000, 0, 0.2, Create(maxSpeed: 500, adaptive: true));

        Assert.True(adaptiveState.X > fixedState.X,
            $"自适应限速应在远距离时追得更快（自适应={adaptiveState.X}, 固定={fixedState.X}）");
    }

    [Fact]
    public void AdaptiveSpeed_NeverExceedsConfiguredFactor()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create(maxSpeed: 500, adaptive: true);

        state.Step(1_000_000, 0, 1.0 / 60, p);

        Assert.True(state.Speed <= 500 * FollowState.AdaptiveMaxFactor + 1e-6);
    }

    [Fact]
    public void SnapThreshold_EliminatesSubPixelJitter()
    {
        var state = new FollowState();
        state.SnapTo(100, 100);
        var p = Create(snap: 2.0);

        state.Step(100.5, 100.5, 1.0 / 60, p);

        Assert.Equal(100.5, state.X, 9);
        Assert.Equal(100.5, state.Y, 9);
        Assert.True(state.IsSettled);
    }

    [Fact]
    public void CriticalDamping_DoesNotOvershoot()
    {
        double stiffness = 80;
        var p = new FollowParameters(
            true,
            stiffness,
            FollowState.CriticalDamping(stiffness),
            100000,
            1000000,
            0.01,
            false);

        var state = new FollowState();
        state.SnapTo(0, 0);

        bool overshot = false;
        for (int i = 0; i < 240; i++)
        {
            state.Step(200, 0, 1.0 / 60, p);
            if (state.X > 200 + 0.5)
            {
                overshot = true;
            }
        }

        Assert.False(overshot, "临界阻尼不应过冲，否则手感会「弹」");
    }

    [Fact]
    public void UnderdampedPreset_OvershootsEventually()
    {
        double stiffness = 80;
        var p = new FollowParameters(
            true,
            stiffness,
            FollowState.CriticalDamping(stiffness) * 0.4,
            100000,
            1000000,
            0.01,
            false);

        var state = new FollowState();
        state.SnapTo(0, 0);

        bool overshot = false;
        for (int i = 0; i < 240; i++)
        {
            state.Step(200, 0, 1.0 / 60, p);
            if (state.X > 200 + 0.5)
            {
                overshot = true;
            }
        }

        Assert.True(overshot, "欠阻尼应当可观察到过冲（Q 弹手感）");
    }

    [Fact]
    public void LargeTimeStep_IsClamped()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);
        var p = Create(maxSpeed: 100);

        state.Step(100000, 0, 10.0, p);

        double maxTravel = 100 * FollowState.MaxTimeStep;
        Assert.True(state.X <= maxTravel + 1e-6, "卡顿后不允许瞬移");
    }

    [Fact]
    public void NegativeTimeStep_IsIgnored()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);

        state.Step(100, 100, -1.0, Create());

        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
    }

    [Fact]
    public void SnapTo_ResetsVelocity()
    {
        var state = new FollowState();
        state.Step(500, 500, 1.0 / 60, Create());

        state.SnapTo(10, 20);

        Assert.Equal(10, state.X);
        Assert.Equal(20, state.Y);
        Assert.Equal(0, state.Speed);
    }

    [Fact]
    public void DampingRatio_MatchesDefinition()
    {
        Assert.Equal(1.0, FollowState.DampingRatio(100, FollowState.CriticalDamping(100)), 6);
        Assert.True(FollowState.DampingRatio(100, 5) < 1);
        Assert.True(FollowState.DampingRatio(100, 40) > 1);
    }

    [Fact]
    public void Presets_ProduceExpectedDamping()
    {
        var settings = new SettingsModel { FollowStiffness = 100 };
        double critical = FollowState.CriticalDamping(100);

        Assert.Equal(critical, FollowState.Preset(settings, DampingPresetKind.Critical).Damping, 6);
        Assert.True(FollowState.Preset(settings, DampingPresetKind.Underdamped).Damping < critical);
        Assert.True(FollowState.Preset(settings, DampingPresetKind.Overdamped).Damping > critical);
    }

    [Fact]
    public void Presets_AreClampedToLegalRange()
    {
        var settings = new SettingsModel { FollowStiffness = 200 };

        var overdamped = FollowState.Preset(settings, DampingPresetKind.Overdamped);

        Assert.InRange(overdamped.Damping, SettingsRanges.FollowDampingMin, SettingsRanges.FollowDampingMax);
    }

    [Fact]
    public void FromSettings_MapsAllFields()
    {
        var settings = new SettingsModel
        {
            FollowEnabled = true,
            FollowStiffness = 111,
            FollowDamping = 22,
            FollowMaxSpeed = 3333,
            FollowMaxAccel = 44444,
            FollowSnapDistance = 2.5,
            FollowAdaptiveSpeed = true,
        };

        var p = FollowParameters.FromSettings(settings);

        Assert.True(p.Enabled);
        Assert.Equal(111, p.Stiffness);
        Assert.Equal(22, p.Damping);
        Assert.Equal(3333, p.MaxSpeed);
        Assert.Equal(44444, p.MaxAccel);
        Assert.Equal(2.5, p.SnapDistance);
        Assert.True(p.AdaptiveSpeed);
    }

    [Fact]
    public void DiagonalMovement_FollowsBothAxes()
    {
        var state = new FollowState();
        state.SnapTo(0, 0);

        Simulate(state, 100, 100, 1.5, Create());

        Assert.Equal(100, state.X, 1);
        Assert.Equal(100, state.Y, 1);
    }
}
