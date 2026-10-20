using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>问题 1 的埋点统计本身也要能自证：间隔到了才写日志、数值被平滑、重置后清零。</summary>
public class FrameTimingTests
{
    [Fact]
    public void DoesNotLogBeforeTheIntervalElapses()
    {
        var timing = new FrameTiming();

        timing.AddFrame(1.0);

        Assert.False(timing.TryTakeLog(out _), "刚开始不该写日志，否则会刷屏");
    }

    [Fact]
    public void LogsOnceTheIntervalHasPassed()
    {
        var timing = new FrameTiming();
        timing.AddFrame(1.0);

        // 内部用真实 Stopwatch 计时，这里等够一个周期
        Thread.Sleep((int)(FrameTiming.LogIntervalSeconds * 1000) + 200);

        Assert.True(timing.TryTakeLog(out double fps));
        Assert.True(fps >= 0);
    }

    [Fact]
    public void AverageFramesPerSecond_ReflectsHowManyFramesWereCounted()
    {
        var timing = new FrameTiming();
        for (int i = 0; i < 30; i++)
        {
            timing.AddFrame(1.0);
        }

        Thread.Sleep((int)(FrameTiming.LogIntervalSeconds * 1000) + 200);

        timing.TryTakeLog(out double fps);

        // 30 帧摊在略多于 5 秒里 → 大约 5~6 FPS（这里只验证量级与「确实数过帧」）
        Assert.True(fps > 4.0 && fps < 7.0, $"平均帧率应在 5~6 之间，实际 {fps:F2}");

        // 取走一次日志后计数清零，下一次统计不会重复计入
        for (int i = 0; i < 5; i++)
        {
            timing.AddFrame(1.0);
        }

        Thread.Sleep((int)(FrameTiming.LogIntervalSeconds * 1000) + 200);
        timing.TryTakeLog(out double second);

        Assert.True(second < 2.0, $"第二次统计不应再算上之前的 30 帧，实际 {second:F2}");
    }

    [Fact]
    public void Samples_AreSmoothedNotReplaced()
    {
        var timing = new FrameTiming();

        timing.AddCapture(10.0);
        double afterFirst = timing.CaptureMs;
        Assert.Equal(10.0, afterFirst, 6);

        // 突然来一个 0ms 的采样：应该平滑下降，而不是直接归零
        timing.AddCapture(0.0);

        Assert.True(timing.CaptureMs > 0.0 && timing.CaptureMs < 10.0,
            $"应当平滑过渡，实际 {timing.CaptureMs:F3}");
    }

    [Fact]
    public void EachSectionIsTrackedSeparately()
    {
        var timing = new FrameTiming();

        timing.AddCapture(10.0);
        timing.AddRender(20.0);
        timing.AddPresent(30.0);
        timing.AddFrame(40.0);

        Assert.Equal(10.0, timing.CaptureMs, 6);
        Assert.Equal(20.0, timing.RenderMs, 6);
        Assert.Equal(30.0, timing.PresentMs, 6);
        Assert.Equal(40.0, timing.FrameMs, 6);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var timing = new FrameTiming();
        timing.AddCapture(10.0);
        timing.AddRender(20.0);
        timing.AddPresent(30.0);
        timing.AddFrame(40.0);

        timing.Reset();

        Assert.Equal(0.0, timing.CaptureMs);
        Assert.Equal(0.0, timing.FrameMs);
        Assert.Equal(0.0, timing.LastAverageFramesPerSecond);
    }
}
