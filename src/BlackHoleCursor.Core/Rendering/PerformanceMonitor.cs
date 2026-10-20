namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 持续低帧率监测（plan.md 3.4）：连续低于阈值达到一定时长后提示用户降级。
/// 按 PRD 1.2，v1.0 不做自动降级，只提示，且同一轮低帧率只提示一次。
/// </summary>
public sealed class PerformanceMonitor
{
    /// <summary>低于该帧率视为不达标。</summary>
    public const double LowFramesPerSecond = 45.0;

    /// <summary>需要持续多久才提示。</summary>
    public const double SustainedSeconds = 3.0;

    /// <summary>至少需要这么多次采样，避免启动瞬间的抖动触发提示。</summary>
    public const int MinimumSamples = 3;

    private double _lowDuration;
    private int _sampleCount;
    private bool _raised;

    /// <summary>已经提示过、且帧率尚未恢复。</summary>
    public bool HasSuggested { get; private set; }

    /// <summary>已经持续低于阈值的时长（秒）。</summary>
    public double LowDuration => _lowDuration;

    public void Reset()
    {
        _lowDuration = 0;
        _sampleCount = 0;
        _raised = false;
        HasSuggested = false;
    }

    /// <summary>
    /// 喂入一次采样。返回 true 表示此刻应当提示用户降级（同一轮只返回一次）。
    /// </summary>
    /// <param name="framesPerSecond">最近一帧的帧率，&lt;= 0 表示无数据（如覆盖层未运行）。</param>
    /// <param name="deltaSeconds">距上次采样的时间间隔。</param>
    /// <param name="lensEnabled">扭曲层是否开启；关闭时没有可降级空间，不计入。</param>
    public bool Update(double framesPerSecond, double deltaSeconds, bool lensEnabled)
    {
        if (!lensEnabled || framesPerSecond <= 0 || double.IsNaN(framesPerSecond))
        {
            _lowDuration = 0;
            _sampleCount = 0;
            return false;
        }

        _sampleCount++;

        if (framesPerSecond >= LowFramesPerSecond)
        {
            // 帧率恢复，重新武装，允许下一次低帧率再次提示
            _lowDuration = 0;
            _sampleCount = 0;
            _raised = false;
            HasSuggested = false;
            return false;
        }

        _lowDuration += Math.Max(deltaSeconds, 0.0);

        if (_raised || _sampleCount < MinimumSamples || _lowDuration < SustainedSeconds)
        {
            return false;
        }

        _raised = true;
        HasSuggested = true;
        return true;
    }
}
