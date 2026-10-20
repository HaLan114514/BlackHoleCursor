namespace BlackHoleCursor.Core;

/// <summary>出现 / 消失动画的方向。</summary>
public enum TransitionDirection
{
    /// <summary>没有在播动画（静止在完全显示状态）。</summary>
    None = 0,

    /// <summary>出现：进度 0 → 1。</summary>
    Appearing = 1,

    /// <summary>消失：进度 1 → 0（与出现共用同一条曲线，观感上就是「反向播放」）。</summary>
    Disappearing = 2,
}

/// <summary>N4 出现/消失动画参数。由 SettingsModel 派生一次，渲染循环内只读。</summary>
public readonly record struct TransitionParameters(
    bool Enabled,
    double DurationSeconds,
    double Overshoot)
{
    public static TransitionParameters FromSettings(SettingsModel s)
        => new(s.TransitionEnabled, s.TransitionDuration, s.TransitionOvershoot);
}

/// <summary>
/// N4 出现 / 消失动画的状态机（纯逻辑，可单测）。
///
/// 观感（已定案）：弹性过冲，不是线性淡入。
///   出现：缩放 0 → 1.1 → 1，透明度同步 0 → 1；
///   消失：**反向播放**同一条曲线，即 1 → 1.1 → 0（先胀一下再收没）。
/// 所以消失不需要单独写一条曲线 —— 进度倒着走就是了。
///
/// 只改渲染强度（缩放与透明度），不碰位置与命中点，
/// 因此与 N1 游荡、F5 惯性跟随天然无冲突。
/// </summary>
public sealed class TransitionState
{
    /// <summary>单帧最大时间步（与 FollowState 一致，防止卡顿后动画一帧跳完）。</summary>
    public const double MaxTimeStep = 0.05;

    /// <summary>过冲峰值出现的进度点。0.6 之前冲到峰值，之后回落到 1。</summary>
    public const double PeakAt = 0.6;

    /// <summary>动画进度：0 = 完全消失，1 = 完全显示。</summary>
    public double Progress { get; private set; } = 1.0;

    public TransitionDirection Direction { get; private set; } = TransitionDirection.None;

    /// <summary>是否已经到达终点（消失动画播完 = 可以收起覆盖层窗口了）。</summary>
    public bool IsFinished { get; private set; } = true;

    /// <summary>是否正在播动画。播动画时必须保持满帧，否则会一顿一顿。</summary>
    public bool IsActive => !IsFinished;

    /// <summary>
    /// 开始播一次动画。
    /// 静止状态下从头开始；正在播的时候切换方向则从当前进度接着走（不会跳变）。
    /// </summary>
    public void Begin(TransitionDirection direction, in TransitionParameters parameters)
    {
        Direction = direction;

        if (!parameters.Enabled)
        {
            // 关闭动画：直接落到终态，调用方据此立刻收起窗口（托盘退出要求零延迟）
            Progress = direction == TransitionDirection.Appearing ? 1.0 : 0.0;
            IsFinished = true;
            return;
        }

        if (IsFinished)
        {
            // 关键：静止时 Progress 是 1，若不清零，「出现」会一帧就播完（等于没播）
            Progress = direction == TransitionDirection.Appearing ? 0.0 : 1.0;
        }

        IsFinished = false;
    }

    /// <summary>从头重播一次「出现」（主界面调参时用）。</summary>
    public void Replay(in TransitionParameters parameters)
    {
        if (!parameters.Enabled)
        {
            Progress = 1.0;
            Direction = TransitionDirection.None;
            IsFinished = true;
            return;
        }

        Progress = 0.0;
        Direction = TransitionDirection.Appearing;
        IsFinished = false;
    }

    public void Step(double deltaTime, in TransitionParameters parameters)
    {
        if (IsFinished)
        {
            return;
        }

        if (!parameters.Enabled)
        {
            Progress = Direction == TransitionDirection.Appearing ? 1.0 : 0.0;
            IsFinished = true;
            return;
        }

        double dt = Math.Clamp(deltaTime, 0.0, MaxTimeStep);
        double step = dt / Math.Max(parameters.DurationSeconds, 0.01);

        Progress = Direction == TransitionDirection.Appearing
            ? Math.Min(Progress + step, 1.0)
            : Math.Max(Progress - step, 0.0);

        if (Progress <= 0.0 || Progress >= 1.0)
        {
            IsFinished = true;
        }
    }

    /// <summary>
    /// 当前视觉缩放：0 → 峰值(<paramref name="parameters"/>.Overshoot) → 1。
    /// 消失时进度倒着走，得到的是「先胀一下再收没」。
    /// </summary>
    public double Scale(in TransitionParameters parameters)
    {
        double t = Progress;
        double peak = Math.Max(parameters.Overshoot, 1.0);

        if (!parameters.Enabled)
        {
            return 1.0;
        }

        return t <= PeakAt
            ? peak * (t / PeakAt)
            : peak + (1.0 - peak) * ((t - PeakAt) / (1.0 - PeakAt));
    }

    /// <summary>透明度：用 smoothstep 柔化端点，避免开头/结尾的硬切。</summary>
    public double Fade
    {
        get
        {
            double t = Math.Clamp(Progress, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }

    // ⚠️ 原先这里有一个 `PeakScale(SettingsModel)`：窗口按「过冲峰值」预留尺寸，
    // 免得放大到 1.1 倍那一瞬光晕被窗口矩形裁掉。
    // v3（P1.5）已删除 —— 窗口是整块显示器，不存在裁切，也就不需要预留。
    // 过冲本身照旧生效：它缩放的是 HoleSize（见 SceneConstants 里 holeFraction *= d.Scale）。

    public void Reset()
    {
        Progress = 1.0;
        Direction = TransitionDirection.None;
        IsFinished = true;
    }
}
