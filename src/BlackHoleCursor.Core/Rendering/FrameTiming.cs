using System.Diagnostics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 每帧分段耗时统计（问题 1 的埋点）：抓屏 / 渲染 / Present / 整帧，单位毫秒。
///
/// 为什么必须分段量：帧率低的时候，「抓屏慢」「渲染慢」「Present 慢」对应的优化完全不同
/// —— 抓屏慢要降频，渲染慢要缩窗口，Present 慢多半只是在等垂直同步（正常现象）。
/// 盲改容易改错地方，所以先埋点。
///
/// 读数的注意事项：D3D11 的命令是延迟提交到 GPU 的，CPU 侧的「渲染」耗时通常很短，
/// 真正的 GPU 开销会体现在 Present（等垂直同步 / 等 GPU 完成）上。
/// 因此判断瓶颈时看的是：整帧 - Present 是否过长，以及抓屏段是否异常。
/// </summary>
public sealed class FrameTiming
{
    /// <summary>写一条汇总日志的间隔（秒）。太频繁会刷屏，太长则采样不足。</summary>
    public const double LogIntervalSeconds = 5.0;

    private const double Smoothing = 0.1;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private double _captureMs;
    private double _renderMs;
    private double _presentMs;
    private double _frameMs;
    private double _lastLogSeconds;
    private int _framesSinceLog;

    /// <summary>抓屏（含全黑帧检测）的平滑耗时，毫秒。</summary>
    public double CaptureMs => _captureMs;

    /// <summary>常量打包 + 绘制调用的平滑耗时，毫秒。</summary>
    public double RenderMs => _renderMs;

    /// <summary>Present 的平滑耗时，毫秒。通常包含等待垂直同步。</summary>
    public double PresentMs => _presentMs;

    /// <summary>整个 Tick 的平滑耗时，毫秒。</summary>
    public double FrameMs => _frameMs;

    /// <summary>自上次写日志以来统计到的平均帧率。</summary>
    public double LastAverageFramesPerSecond { get; private set; }

    public void AddCapture(double milliseconds) => _captureMs = Blend(_captureMs, milliseconds);

    public void AddRender(double milliseconds) => _renderMs = Blend(_renderMs, milliseconds);

    public void AddPresent(double milliseconds) => _presentMs = Blend(_presentMs, milliseconds);

    public void AddFrame(double milliseconds)
    {
        _frameMs = Blend(_frameMs, milliseconds);
        _framesSinceLog++;
    }

    /// <summary>是否到了该写汇总日志的时刻。</summary>
    public bool TryTakeLog(out double averageFramesPerSecond)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        double span = now - _lastLogSeconds;

        if (span < LogIntervalSeconds)
        {
            averageFramesPerSecond = 0;
            return false;
        }

        averageFramesPerSecond = span > 0 ? _framesSinceLog / span : 0;
        LastAverageFramesPerSecond = averageFramesPerSecond;
        _lastLogSeconds = now;
        _framesSinceLog = 0;
        return true;
    }

    public void Reset()
    {
        _captureMs = 0;
        _renderMs = 0;
        _presentMs = 0;
        _frameMs = 0;
        _lastLogSeconds = _stopwatch.Elapsed.TotalSeconds;
        _framesSinceLog = 0;
        LastAverageFramesPerSecond = 0;
    }

    private static double Blend(double current, double sample)
        => current <= 0 ? sample : current + ((sample - current) * Smoothing);
}
