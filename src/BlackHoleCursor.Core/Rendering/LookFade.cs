using System.Diagnostics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// M5.2（prd-v2 F2-5）：切换预设时的 **1 秒 smoothstep 交叉淡入**。
///
/// 用法：切换前先 <see cref="Begin"/> 抓住「当前外观」作为起点，
/// 之后每帧用 <see cref="Blend"/> 把「起点」与「目标外观」按进度混合。
/// 目标外观**每帧实时取**（不是快照），所以淡入途中用户再拖动滑块也能正确收敛。
///
/// 进度用 smoothstep 而不是线性：两端导数为 0，起落都没有硬拐点。
///
/// 进度按**绝对时间**算（<see cref="Progress"/> 由注入的时钟求出），不是「累计 delta」：
/// 覆盖层渲染循环与主界面预览共用同一个实例、各自每帧调一次 <see cref="Step"/>，
/// 调用次数不影响快慢，否则淡入会被两边一起推到约 2 倍速。
/// </summary>
public sealed class LookFade
{
    /// <summary>默认时长（秒）：prd-v2 F2-5 要求 1 秒。</summary>
    public const double DefaultDurationSeconds = 1.0;

    private readonly Func<double> _clock;
    private BlackHoleLook? _from;
    private double _startSeconds;
    private volatile bool _isActive;

    /// <param name="durationSeconds">淡入时长（秒）。</param>
    /// <param name="clock">
    /// 单调时间源（秒）。默认用进程级 <see cref="Stopwatch"/> 时间戳；
    /// 测试传入虚拟时钟即可精确控制进度。
    /// </param>
    public LookFade(double durationSeconds = DefaultDurationSeconds, Func<double>? clock = null)
    {
        DurationSeconds = Math.Max(durationSeconds, 0.0);
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _startSeconds = _clock();
    }

    public double DurationSeconds { get; }

    /// <summary>是否正在淡入。</summary>
    public bool IsActive => _isActive;

    /// <summary>
    /// 已走过的进度（0–1，未经 smoothstep）。不在淡入中时为 1（即「已在目标外观」）。
    /// </summary>
    public double Progress
    {
        get
        {
            if (!_isActive)
            {
                return 1.0;
            }

            if (DurationSeconds <= 0.0)
            {
                return 1.0;
            }

            return Math.Clamp((_clock() - _startSeconds) / DurationSeconds, 0.0, 1.0);
        }
    }

    /// <summary>
    /// 从 <paramref name="from"/> 开始淡入到之后传入的目标。
    /// 必须在**改设置之前**调用，否则起点就是新外观，等于没有淡入。
    /// </summary>
    public void Begin(BlackHoleLook from)
    {
        _from = from.Clone();
        _startSeconds = _clock();
        _isActive = true;
    }

    /// <summary>
    /// 到点了就结束淡入并释放起点。多个调用方可以各调一次：
    /// 进度只由时钟决定，不会因为调用次数而加速。
    /// </summary>
    public void Step()
    {
        if (_isActive && Progress >= 1.0)
        {
            _isActive = false;
            _from = null;
        }
    }

    /// <summary>
    /// 取当前应渲染的外观：淡入中返回插值结果，否则原样返回 <paramref name="target"/>。
    /// </summary>
    public BlackHoleLook Blend(BlackHoleLook target)
    {
        if (!_isActive || _from is null)
        {
            return target;
        }

        double t = SmoothStep(Progress);
        if (t >= 1.0)
        {
            _isActive = false;
            _from = null;
            return target;
        }

        return BlackHoleLook.Lerp(_from, target, t);
    }

    /// <summary>放弃淡入，立刻落到目标外观（例如恢复默认 / 关闭黑洞）。</summary>
    public void Cancel()
    {
        _isActive = false;
        _from = null;
    }

    /// <summary>smoothstep：两端导数为 0，起落无硬拐点。</summary>
    public static double SmoothStep(double t)
    {
        double x = Math.Clamp(t, 0.0, 1.0);
        return x * x * (3.0 - (2.0 * x));
    }
}
