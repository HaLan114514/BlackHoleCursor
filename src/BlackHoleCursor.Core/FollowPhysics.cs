namespace BlackHoleCursor.Core;

/// <summary>惯性跟随参数（由 SettingsModel 派生一次，渲染循环内只读）。</summary>
public readonly record struct FollowParameters(
    bool Enabled,
    double Stiffness,
    double Damping,
    double MaxSpeed,
    double MaxAccel,
    double SnapDistance,
    bool AdaptiveSpeed)
{
    public static FollowParameters FromSettings(SettingsModel s)
        => new(
            s.FollowEnabled,
            s.FollowStiffness,
            s.FollowDamping,
            s.FollowMaxSpeed,
            s.FollowMaxAccel,
            s.FollowSnapDistance,
            s.FollowAdaptiveSpeed);
}

/// <summary>
/// F5 惯性跟随：黑洞视觉位置以临界阻尼弹簧模型追向真实鼠标。
/// 命中点始终是真实鼠标位置，本类只影响「画在哪里」。
/// </summary>
public sealed class FollowState
{
    /// <summary>单帧最大时间步（秒）。卡顿后不允许瞬移。</summary>
    public const double MaxTimeStep = 0.05;

    /// <summary>自适应限速的距离归一化基准（px）。</summary>
    public const double AdaptiveReferenceDistance = 400.0;

    /// <summary>自适应限速的最大倍率。</summary>
    public const double AdaptiveMaxFactor = 3.0;

    public double X { get; private set; }

    public double Y { get; private set; }

    public double VelocityX { get; private set; }

    public double VelocityY { get; private set; }

    public double Speed => Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);

    public bool IsSettled { get; private set; } = true;

    public void SnapTo(double x, double y)
    {
        X = x;
        Y = y;
        VelocityX = 0;
        VelocityY = 0;
        IsSettled = true;
    }

    public void Step(double targetX, double targetY, double deltaTime, in FollowParameters p)
    {
        double dt = Math.Clamp(deltaTime, 0.0, MaxTimeStep);

        if (!p.Enabled)
        {
            X = targetX;
            Y = targetY;
            VelocityX = 0;
            VelocityY = 0;
            IsSettled = true;
            return;
        }

        double dx = targetX - X;
        double dy = targetY - Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // a = k * 位移 - c * 速度
        double ax = dx * p.Stiffness - VelocityX * p.Damping;
        double ay = dy * p.Stiffness - VelocityY * p.Damping;
        ClampMagnitude(ref ax, ref ay, p.MaxAccel);

        double vx = VelocityX + ax * dt;
        double vy = VelocityY + ay * dt;

        double speedLimit = p.AdaptiveSpeed
            ? p.MaxSpeed * Math.Clamp(1.0 + distance / AdaptiveReferenceDistance, 1.0, AdaptiveMaxFactor)
            : p.MaxSpeed;
        ClampMagnitude(ref vx, ref vy, speedLimit);

        VelocityX = vx;
        VelocityY = vy;

        X += VelocityX * dt;
        Y += VelocityY * dt;

        double remainingX = targetX - X;
        double remainingY = targetY - Y;
        double remaining = Math.Sqrt(remainingX * remainingX + remainingY * remainingY);

        // 到达吸附阈值内且速度足够低 → 直接贴合，消除亚像素抖动
        if (remaining < p.SnapDistance && Speed < p.SnapDistance * 60.0)
        {
            X = targetX;
            Y = targetY;
            VelocityX = 0;
            VelocityY = 0;
            IsSettled = true;
        }
        else
        {
            IsSettled = false;
        }
    }

    private static void ClampMagnitude(ref double x, ref double y, double max)
    {
        if (max <= 0)
        {
            x = 0;
            y = 0;
            return;
        }

        double length = Math.Sqrt(x * x + y * y);
        if (length <= max || length == 0)
        {
            return;
        }

        double scale = max / length;
        x *= scale;
        y *= scale;
    }

    /// <summary>临界阻尼系数 c = 2√k。</summary>
    public static double CriticalDamping(double stiffness) => 2.0 * Math.Sqrt(Math.Max(stiffness, 0.0));

    /// <summary>阻尼比 ζ = c / (2√k)：&lt;1 欠阻尼，=1 临界，&gt;1 过阻尼。</summary>
    public static double DampingRatio(double stiffness, double damping)
    {
        double critical = CriticalDamping(stiffness);
        return critical <= 0 ? double.PositiveInfinity : damping / critical;
    }

    public static FollowParameters Preset(SettingsModel settings, DampingPresetKind kind)
    {
        double stiffness = settings.FollowStiffness;
        double critical = CriticalDamping(stiffness);
        double damping = kind switch
        {
            DampingPresetKind.Underdamped => critical * 0.55,
            DampingPresetKind.Overdamped => critical * 1.70,
            _ => critical,
        };

        return new FollowParameters(
            true,
            stiffness,
            Math.Clamp(damping, SettingsRanges.FollowDampingMin, SettingsRanges.FollowDampingMax),
            settings.FollowMaxSpeed,
            settings.FollowMaxAccel,
            settings.FollowSnapDistance,
            settings.FollowAdaptiveSpeed);
    }
}

public enum DampingPresetKind
{
    Underdamped,
    Critical,
    Overdamped,
}
