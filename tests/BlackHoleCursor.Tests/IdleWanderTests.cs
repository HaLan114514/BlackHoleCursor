using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// N1 静止游荡：鼠标不动一段时间后黑洞在附近飘动，鼠标一动立刻回位。
/// 纯逻辑测试，不依赖 D3D 设备。
/// </summary>
public class IdleWanderTests
{
    private const double FrameStep = 1.0 / 60.0;

    private static readonly WanderParameters DefaultWander = new(true, 1.5, 40.0, 0.3);

    /// <summary>以固定步长推进若干秒，返回最终偏移量。</summary>
    private static (double X, double Y) Advance(
        WanderState state,
        double seconds,
        bool mouseMoved,
        in WanderParameters parameters,
        double step = FrameStep)
    {
        int frames = (int)Math.Ceiling(seconds / step);
        for (int i = 0; i < frames; i++)
        {
            state.Step(mouseMoved, step, parameters);
        }

        return state.Offset;
    }

    private static double Length((double X, double Y) offset)
        => Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);

    [Fact]
    public void Disabled_StaysExactlyOnTheMouse()
    {
        var state = new WanderState();
        var parameters = new WanderParameters(false, 0.2, 120.0, 1.5);

        var offset = Advance(state, 10.0, mouseMoved: false, parameters);

        Assert.Equal(0.0, Length(offset));
        Assert.False(state.IsActive);
        Assert.False(state.IsWandering);
    }

    [Fact]
    public void BeforeTheDelay_DoesNotMove()
    {
        var state = new WanderState();

        var offset = Advance(state, 1.4, mouseMoved: false, DefaultWander);

        Assert.Equal(0.0, Length(offset));
        Assert.False(state.IsWandering);
        Assert.True(state.IdleSeconds < 1.5);
    }

    [Fact]
    public void AfterTheDelay_MovesAwayFromTheMouse()
    {
        var state = new WanderState();

        // 1.5s 延迟 + 0.5s 渐入（BlendSeconds = 0.25），此时应已产生可见位移
        var offset = Advance(state, 2.0, mouseMoved: false, DefaultWander);

        Assert.True(state.IsWandering);
        Assert.True(Length(offset) > 1.0, $"应已飘离鼠标，实际偏移 {Length(offset):F3} px");
    }

    [Fact]
    public void NeverLeavesTheWanderRadius()
    {
        var state = new WanderState();

        // 跑两分钟，逐帧检查：任何情况下都不得超出半径（多快的游荡速度都不行）
        int frames = (int)(120.0 / FrameStep);
        for (int i = 0; i < frames; i++)
        {
            state.Step(false, FrameStep, DefaultWander);
            Assert.True(
                Length(state.Offset) <= DefaultWander.Radius + 1e-9,
                $"第 {i} 帧偏移 {Length(state.Offset):F3} 超出半径 {DefaultWander.Radius}");
        }

        // 同时确认它真的在动，而不是被钳成了静止
        Assert.True(Length(state.Offset) > 0.0);
    }

    [Fact]
    public void FastWander_StillStaysInsideTheRadius()
    {
        var state = new WanderState();
        var parameters = new WanderParameters(true, 0.2, 5.0, 1.5);

        int frames = (int)(60.0 / FrameStep);
        for (int i = 0; i < frames; i++)
        {
            state.Step(false, FrameStep, parameters);
            Assert.True(
                Length(state.Offset) <= parameters.Radius + 1e-9,
                $"第 {i} 帧偏移 {Length(state.Offset):F3} 超出半径 {parameters.Radius}");
        }
    }

    [Fact]
    public void MouseMove_BringsItBackHome()
    {
        var state = new WanderState();
        Advance(state, 3.0, mouseMoved: false, DefaultWander);
        Assert.True(Length(state.Offset) > 0.0);

        // 鼠标一动：立刻退出游荡，并在 0.25s 内平滑回位（不许瞬移）
        var offset = Advance(state, 0.5, mouseMoved: true, DefaultWander);

        Assert.False(state.IsWandering);
        Assert.False(state.IsActive);
        Assert.Equal(0.0, Length(offset));
    }

    [Fact]
    public void ReturnHome_IsGradualNotInstant()
    {
        var state = new WanderState();
        Advance(state, 3.0, mouseMoved: false, DefaultWander);
        double drifted = Length(state.Offset);

        // 回位只推进一帧：位移必须变小，但绝不能一步归零
        state.Step(true, FrameStep, DefaultWander);

        Assert.True(Length(state.Offset) < drifted, "应在向鼠标收敛");
        Assert.True(Length(state.Offset) > 0.0, "单帧内不许瞬移回位");
    }

    [Fact]
    public void HugeTimeStep_IsClamped()
    {
        var state = new WanderState();

        // 卡顿 10 秒后只应推进一个 MaxTimeStep（0.05s），不能一帧就判定为「静止已久」
        state.Step(false, 10.0, DefaultWander);

        Assert.False(state.IsWandering);
        Assert.Equal(0.0, Length(state.Offset));
        Assert.Equal(WanderState.MaxTimeStep, state.IdleSeconds);
    }

    [Fact]
    public void WanderParameters_AreReadFromSettings()
    {
        var settings = new SettingsModel();

        Assert.False(settings.IdleWanderEnabled);
        var parameters = WanderParameters.FromSettings(settings);

        Assert.False(parameters.Enabled);
        Assert.Equal(1.5, parameters.DelaySeconds);
        Assert.Equal(40.0, parameters.Radius);
        Assert.Equal(0.3, parameters.Speed);

        settings.IdleWanderEnabled = true;
        Assert.True(WanderParameters.FromSettings(settings).Enabled);
    }

    [Fact]
    public void OutOfRangeValues_AreClampedBySettings()
    {
        var settings = new SettingsModel
        {
            IdleWanderDelay = 999,
            IdleWanderRadius = -50,
            IdleWanderSpeed = 0,
        };

        Assert.True(settings.Clamp());

        Assert.Equal(SettingsRanges.IdleWanderDelayMax, settings.IdleWanderDelay);
        Assert.Equal(SettingsRanges.IdleWanderRadiusMin, settings.IdleWanderRadius);
        Assert.Equal(SettingsRanges.IdleWanderSpeedMin, settings.IdleWanderSpeed);
    }

    [Fact]
    public void Wandering_KeepsTheFrameRateAtActive()
    {
        // 转速为 0 且鼠标静止时本应降到 30 FPS；正在游荡则必须保持 60 FPS，否则会一顿一顿
        var settings = new SettingsModel { DiskSpeed = 0 };
        var scheduler = new FrameScheduler();

        Assert.True(scheduler.ShouldRender(true, moving: false, settings, forceActive: false, wandering: true));

        Assert.Equal(FrameScheduler.ActiveFramesPerSecond(settings), scheduler.TargetFramesPerSecond);
        Assert.False(scheduler.IsIdle);
    }
}
