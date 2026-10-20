using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public class RuntimeHostTests
{
    private sealed class FakeOverlay : IOverlayController
    {
        public bool IsRunning { get; private set; }

        public double FramesPerSecond => 60;

        public (double X, double Y) VisualPosition => (0, 0);

        public double ElapsedSeconds => 0;

        public int NotifyCount { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void NotifySettingsChanged() => NotifyCount++;

        public void SetUiActive(bool active) => UiActive = active;

        public void ReplayTransition() => ReplayCount++;

        // M5.2：预设切换的 1 秒交叉淡入
        public void BeginLookFade() => FadeCount++;

        public bool UiActive { get; private set; }

        public int ReplayCount { get; private set; }

        public int FadeCount { get; private set; }
    }

    private static void EnsureStopped()
    {
        RuntimeHost.Shutdown();
    }

    [Fact]
    public void Start_HidesSystemCursorAndStartsOverlay()
    {
        EnsureStopped();
        var settings = new SettingsModel();
        var native = new FakeCursorNative();
        var overlay = new FakeOverlay();

        RuntimeHost.Start(settings, native, overlay);

        try
        {
            Assert.True(RuntimeHost.IsStarted);
            Assert.True(RuntimeHost.Cursor!.IsHidden);
            Assert.True(RuntimeHost.Overlay!.IsRunning);
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }

    [Fact]
    public void Shutdown_AlwaysRestoresSystemCursor()
    {
        EnsureStopped();
        var settings = new SettingsModel();
        var native = new FakeCursorNative();
        var overlay = new FakeOverlay();

        RuntimeHost.Start(settings, native, overlay);
        Assert.True(native.Count < 0);

        RuntimeHost.Shutdown();

        Assert.Equal(0, native.Count);
        Assert.False(RuntimeHost.IsStarted);
        Assert.False(overlay.IsRunning);
    }

    [Fact]
    public void SetPointerOverOwnWindow_RestoresCursorAndHidesAgain()
    {
        EnsureStopped();
        var native = new FakeCursorNative();

        RuntimeHost.Start(new SettingsModel(), native, new FakeOverlay());

        try
        {
            Assert.True(RuntimeHost.Cursor!.IsHidden);

            RuntimeHost.SetPointerOverOwnWindow(true);
            Assert.False(RuntimeHost.Cursor!.IsHidden, "指针在自己窗口上时必须能看到系统光标");
            Assert.Equal(0, native.Count);

            RuntimeHost.SetPointerOverOwnWindow(false);
            Assert.True(RuntimeHost.Cursor!.IsHidden);
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }

    [Fact]
    public void Shutdown_IsIdempotent()
    {
        EnsureStopped();
        RuntimeHost.Start(new SettingsModel(), new FakeCursorNative(), new FakeOverlay());

        RuntimeHost.Shutdown();
        RuntimeHost.Shutdown();

        Assert.False(RuntimeHost.IsStarted);
    }

    [Fact]
    public void Refresh_ReEvaluatesCursorVisibility()
    {
        EnsureStopped();
        var settings = new SettingsModel();
        RuntimeHost.Start(settings, new FakeCursorNative(), new FakeOverlay());

        try
        {
            settings.Enabled = false;
            RuntimeHost.Refresh();
            Assert.False(RuntimeHost.Cursor!.IsHidden);

            settings.Enabled = true;
            RuntimeHost.Refresh();
            Assert.True(RuntimeHost.Cursor!.IsHidden);
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }

    [Fact]
    public void Refresh_WithoutStart_DoesNothing()
    {
        EnsureStopped();

        RuntimeHost.Refresh();

        Assert.False(RuntimeHost.IsStarted);
    }

    [Fact]
    public void Start_IsIgnoredWhenAlreadyStarted()
    {
        EnsureStopped();
        var overlay = new FakeOverlay();
        RuntimeHost.Start(new SettingsModel(), new FakeCursorNative(), overlay);

        try
        {
            RuntimeHost.Start(new SettingsModel(), new FakeCursorNative(), new FakeOverlay());

            Assert.Same(overlay, RuntimeHost.Overlay);
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }

    [Fact]
    public void Start_WithoutCursorTakeover_LeavesSystemCursorVisible()
    {
        EnsureStopped();
        var settings = new SettingsModel();
        var native = new FakeCursorNative();

        RuntimeHost.Start(settings, native, new FakeOverlay(), takeoverCursor: false);

        try
        {
            Assert.False(RuntimeHost.Cursor!.IsHidden);
            Assert.Equal(0, native.Calls);
            Assert.True(settings.HideSystemCursor, "设置值不应被诊断参数改写");
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }

    [Fact]
    public void Start_WithoutOverlay_StillManagesCursor()
    {
        EnsureStopped();

        RuntimeHost.Start(new SettingsModel(), new FakeCursorNative(), null);

        try
        {
            Assert.True(RuntimeHost.Cursor!.IsHidden);
            Assert.Null(RuntimeHost.Overlay);
        }
        finally
        {
            RuntimeHost.Shutdown();
        }
    }
}
