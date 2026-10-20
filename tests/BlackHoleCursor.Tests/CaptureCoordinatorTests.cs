using BlackHoleCursor.Core.Capture;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

public sealed class ScriptedCapture : IDesktopCapture
{
    private readonly Queue<CaptureStatus> _statuses;

    public ScriptedCapture(string name, params CaptureStatus[] statuses)
    {
        Name = name;
        _statuses = new Queue<CaptureStatus>(statuses);
    }

    public string Name { get; }

    public bool IsAvailable { get; private set; } = true;

    public int RecreateCount { get; private set; }

    public int CaptureCount { get; private set; }

    public int ReleaseCount { get; private set; }

    public CaptureStatus ExhaustedStatus { get; set; } = CaptureStatus.Unavailable;

    public CaptureStatus TryCapture(ID3D11DeviceContext context, ID3D11Texture2D destination, int left, int top, int width, int height, int destX = 0, int destY = 0)
    {
        CaptureCount++;
        return _statuses.Count > 0 ? _statuses.Dequeue() : ExhaustedStatus;
    }

    public void ReleaseFrame() => ReleaseCount++;

    public void Recreate()
    {
        RecreateCount++;
        IsAvailable = true;
    }

    public void SelectOutput(int centerX, int centerY)
    {
    }

    public int DisposeCount { get; private set; }

    public void Dispose() => DisposeCount++;
}

public class CaptureCoordinatorTests
{
    private static (CaptureCoordinator coordinator, ScriptedCapture primary, ScriptedCapture fallback) Create(
        params CaptureStatus[] primaryStatuses)
    {
        var primary = new ScriptedCapture("primary", primaryStatuses);
        var fallback = new ScriptedCapture("fallback");
        return (new CaptureCoordinator(primary, fallback), primary, fallback);
    }

    private static CaptureStatus Capture(CaptureCoordinator coordinator)
        => coordinator.TryCapture(null!, null!, 0, 0, 100, 100);

    [Fact]
    public void Success_PassesThrough()
    {
        var (coordinator, primary, fallback) = Create(CaptureStatus.Success);

        Assert.Equal(CaptureStatus.Success, Capture(coordinator));
        Assert.False(coordinator.IsDegraded);
        Assert.Equal(1, primary.CaptureCount);
        Assert.Equal(0, fallback.CaptureCount);
    }

    [Fact]
    public void Timeout_IsNotTreatedAsFailure()
    {
        var (coordinator, primary, _) = Create(CaptureStatus.Timeout);

        Assert.Equal(CaptureStatus.Timeout, Capture(coordinator));
        Assert.Equal(0, coordinator.FailureCount);
        Assert.False(coordinator.IsDegraded);
    }

    [Fact]
    public void AccessLost_TriggersRecreate()
    {
        var (coordinator, primary, _) = Create(CaptureStatus.AccessLost);

        Capture(coordinator);

        Assert.Equal(1, primary.RecreateCount);
        Assert.Equal(1, coordinator.FailureCount);
        Assert.False(coordinator.IsDegraded, "首次失败应先尝试重建，而不是立刻降级");
    }

    [Fact]
    public void RepeatedAccessLost_DegradesToFallback()
    {
        var (coordinator, primary, fallback) = Create(
            CaptureStatus.AccessLost,
            CaptureStatus.AccessLost,
            CaptureStatus.AccessLost,
            CaptureStatus.AccessLost,
            CaptureStatus.AccessLost);
        primary.ExhaustedStatus = CaptureStatus.AccessLost;

        for (int i = 0; i < 5; i++)
        {
            Capture(coordinator);
        }

        Assert.True(coordinator.IsDegraded, "重建多次仍失败必须降级到备用方案");
        Assert.Equal("fallback", coordinator.ActiveName);
        Assert.True(fallback.CaptureCount > 0);
    }

    [Fact]
    public void Unavailable_ImmediatelyDegrades()
    {
        var (coordinator, _, fallback) = Create(CaptureStatus.Unavailable);
        coordinator.TryCapture(null!, null!, 0, 0, 10, 10);

        Assert.True(coordinator.IsDegraded);
        Assert.True(fallback.RecreateCount >= 1);
    }

    [Fact]
    public void Recovery_ResetsFailureCounter()
    {
        var (coordinator, primary, _) = Create(CaptureStatus.AccessLost, CaptureStatus.Success);
        primary.ExhaustedStatus = CaptureStatus.Success;

        Capture(coordinator);
        Assert.Equal(1, coordinator.FailureCount);

        Capture(coordinator);
        Assert.Equal(0, coordinator.FailureCount);
    }

    [Fact]
    public void ReleaseFrame_DelegatesToActiveCapture()
    {
        var (coordinator, primary, _) = Create(CaptureStatus.Success);

        coordinator.ReleaseFrame();

        Assert.Equal(1, primary.ReleaseCount);
    }

    [Fact]
    public void ActiveName_StartsWithPrimary()
    {
        var (coordinator, _, _) = Create();

        Assert.Equal("primary", coordinator.ActiveName);
    }

    [Fact]
    public void Dispose_ReleasesFrameAndBothCaptures()
    {
        var (coordinator, primary, fallback) = Create(CaptureStatus.Success);
        Capture(coordinator);

        coordinator.Dispose();

        // 设备重建时必须真正释放 Desktop Duplication，否则旧输出会一直被占用
        Assert.Equal(1, primary.ReleaseCount);
        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(1, fallback.DisposeCount);
    }
}
