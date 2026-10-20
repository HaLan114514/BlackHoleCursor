namespace BlackHoleCursor.Core;

/// <summary>静止游荡参数（N1）。由 SettingsModel 派生一次，渲染循环内只读。</summary>
public readonly record struct WanderParameters(
    bool Enabled,
    double DelaySeconds,
    double Radius,
    double Speed)
{
    public static WanderParameters FromSettings(SettingsModel s)
        => new(s.IdleWanderEnabled, s.IdleWanderDelay, s.IdleWanderRadius, s.IdleWanderSpeed);
}

/// <summary>
/// N1：鼠标静止一段时间后，黑洞在鼠标附近缓慢游荡。
///
/// 只输出一个「叠加到真实鼠标位置上的偏移量」，与 F5 惯性跟随同一条原则：
/// **命中点永远在真实鼠标**，游荡纯粹是视觉效果。
///
/// 状态机：静止计时 → 超过延迟进入游荡（渐入）→ 鼠标一动立即渐出回位。
/// 轨迹用两个不同频率的正弦（Lissajous）叠一点低频扰动，避免看出周期性。
/// </summary>
public sealed class WanderState
{
    /// <summary>单帧最大时间步（与 FollowState 一致，防止卡顿后瞬移）。</summary>
    public const double MaxTimeStep = 0.05;

    /// <summary>进入/退出游荡的缓动时长（秒）。回位必须平滑，不许瞬移。</summary>
    public const double BlendSeconds = 0.25;

    /// <summary>Lissajous 的两个角频率倍率（比值非整数 → 轨迹不容易闭合）。</summary>
    private const double FrequencyX = 0.90;

    private const double FrequencyY = 0.61;

    /// <summary>低频扰动倍率，进一步打散周期性。</summary>
    private const double DriftFrequency = 0.23;

    private double _idleSeconds;
    private double _wanderSeconds;
    private double _blend;

    /// <summary>当前偏移量（叠加到真实鼠标坐标上）。</summary>
    public (double X, double Y) Offset { get; private set; }

    /// <summary>是否正在产生位移（用于帧率调度：游荡时必须保持满帧）。</summary>
    public bool IsActive => _blend > 0.0;

    /// <summary>是否已达延迟、处于游荡状态（未计渐入渐出）。</summary>
    public bool IsWandering { get; private set; }

    /// <summary>静止了多久（秒）。</summary>
    public double IdleSeconds => _idleSeconds;

    public void Reset()
    {
        _idleSeconds = 0;
        _wanderSeconds = 0;
        _blend = 0;
        IsWandering = false;
        Offset = (0, 0);
    }

    /// <param name="mouseMoved">本帧鼠标坐标是否发生变化。</param>
    public void Step(bool mouseMoved, double deltaTime, in WanderParameters p)
    {
        double dt = Math.Clamp(deltaTime, 0.0, MaxTimeStep);

        if (!p.Enabled)
        {
            Reset();
            return;
        }

        _idleSeconds = mouseMoved ? 0.0 : _idleSeconds + dt;
        IsWandering = _idleSeconds >= p.DelaySeconds;

        if (IsWandering)
        {
            _wanderSeconds += dt;
        }

        // 渐入 / 渐出：进入游荡时 0→1，鼠标一动 1→0（0.25 秒内平滑回位）
        _blend = Approach(_blend, IsWandering ? 1.0 : 0.0, dt / BlendSeconds);

        Offset = _blend <= 0.0 ? (0, 0) : ComputeOffset(p);
    }

    private (double X, double Y) ComputeOffset(in WanderParameters p)
    {
        double radius = Math.Max(p.Radius, 0.0);
        double speed = Math.Max(p.Speed, 0.0);
        double t = _wanderSeconds * speed;

        // Lissajous：两个不可通约的频率，轨迹长时间不闭合
        double amplitude = radius * 0.7;
        double x = amplitude * Math.Sin(FrequencyX * t) + radius * 0.15 * Math.Sin(DriftFrequency * t);
        double y = amplitude * Math.Sin(FrequencyY * t + 1.3) + radius * 0.15 * Math.Cos(DriftFrequency * t * 0.8);

        // 兜底钳制：任何情况下都不得超出游荡半径
        double length = Math.Sqrt(x * x + y * y);
        if (length > radius && length > 0)
        {
            double scale = radius / length;
            x *= scale;
            y *= scale;
        }

        return (x * _blend, y * _blend);
    }

    private static double Approach(double current, double target, double maxDelta)
    {
        if (maxDelta <= 0)
        {
            return current;
        }

        double delta = target - current;
        if (Math.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(delta) * maxDelta;
    }
}
