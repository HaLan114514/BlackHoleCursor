using System.Diagnostics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 帧率调度：鼠标静止且无动画时降帧，避免空闲时空转占用 CPU。
/// 目标帧率由设置决定（默认 60，可拉到 240），不再写死。
/// </summary>
public sealed class FrameScheduler
{
    public const double DefaultFramesPerSecond = 60.0;

    /// <summary>降帧时的帧率上限：高刷屏用户设了 165 也不会在空闲时空转到 165。</summary>
    public const double IdleFramesPerSecond = 30.0;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Func<double> _clock;
    private double _nextFrameTime;

    public double TargetFramesPerSecond { get; private set; } = DefaultFramesPerSecond;

    /// <param name="clock">
    /// 时间源（秒）。默认用 <see cref="Stopwatch"/>；测试传入虚拟时钟即可脱离真实时间，
    /// 让「不同帧率下画面是否一致」这类断言变得确定（plan-v2 M3.1）。
    /// 宿主与帧调度共用同一个时钟，避免两边对「过了多久」的理解不一致。
    /// </param>
    public FrameScheduler(Func<double>? clock = null)
        => _clock = clock ?? (() => _stopwatch.Elapsed.TotalSeconds);

    /// <summary>是否处于降帧状态。</summary>
    public bool IsIdle { get; private set; }

    /// <summary>活跃时的目标帧率（来自设置，已钳制到合法范围）。</summary>
    public static double ActiveFramesPerSecond(SettingsModel settings)
        => Math.Clamp(
            settings.TargetFrameRate,
            SettingsRanges.TargetFrameRateMin,
            SettingsRanges.TargetFrameRateMax);

    /// <summary>空闲时的目标帧率：永远不超过 30，省电优先。</summary>
    public static double IdleTargetFramesPerSecond(SettingsModel settings)
        => Math.Min(ActiveFramesPerSecond(settings), IdleFramesPerSecond);

    /// <summary>距下一帧还有多少毫秒（渲染线程据此决定等待时长，不再固定 1ms 忙等）。</summary>
    public double MillisecondsUntilNextFrame
    {
        get
        {
            double remaining = _nextFrameTime - _clock();
            return remaining <= 0 ? 0.0 : remaining * 1000.0;
        }
    }

    /// <summary>画面是否本身在动（吸积盘自转）。</summary>
    public static bool HasAnimation(SettingsModel settings) => HasAnimation(settings, false);

    /// <param name="wandering">N1 静止游荡是否正在产生位移（游荡时必须保持满帧，否则会一顿一顿）。</param>
    public static bool HasAnimation(SettingsModel settings, bool wandering)
        // v2（M1.8.4）：条纹速度改为 DiskSpeed，负值表示反向公转，同样算「在动」。
        => Math.Abs(settings.DiskSpeed) > 0 || wandering;

    /// <summary>距离上一次渲染是否已经过去一帧的时间。</summary>
    /// <param name="forceActive">
    /// 主界面打开时为 true：暂停降帧策略，保证预览与桌面效果同步流畅（plan.md 5.3）。
    /// </param>
    public bool ShouldRender(bool enabled, bool moving, SettingsModel settings, bool forceActive = false, bool wandering = false)
    {
        if (!enabled)
        {
            return false;
        }

        bool animating = HasAnimation(settings, wandering);
        IsIdle = !forceActive && !moving && !animating;

        double fps = forceActive || moving || animating
            ? ActiveFramesPerSecond(settings)
            : IdleTargetFramesPerSecond(settings);

        fps = Math.Clamp(fps, SettingsRanges.TargetFrameRateMin, SettingsRanges.TargetFrameRateMax);
        TargetFramesPerSecond = fps;

        double now = _clock();
        if (now < _nextFrameTime)
        {
            return false;
        }

        // 卡顿时不要把下一帧推得太远，否则会越拖越久
        _nextFrameTime = Math.Max(now + (1.0 / fps), _nextFrameTime + (1.0 / fps) * 0.5);
        return true;
    }

    public void Reset()
    {
        _stopwatch.Restart();
        _nextFrameTime = 0;
        IsIdle = false;
    }
}
