using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public sealed class FakeCursorNative : ICursorNative
{
    public FakeCursorNative(int initialCount = 0) => Count = initialCount;

    public int Count { get; private set; }

    public int Calls { get; private set; }

    public NotificationState State { get; set; } = NotificationState.AcceptsNotifications;

    public int ShowCursor(bool show)
    {
        Calls++;
        Count += show ? 1 : -1;
        return Count;
    }

    /// <summary>被替换掉的系统光标 ID 记录。</summary>
    public List<uint> ReplacedCursorIds { get; } = new();

    public int ReloadCount { get; private set; }

    public bool SetSystemCursor(IntPtr cursor, uint id)
    {
        ReplacedCursorIds.Add(id);
        return cursor != IntPtr.Zero;
    }

    public bool ReloadSystemCursors()
    {
        ReloadCount++;
        return true;
    }

    public NotificationState QueryUserState() => State;
}

public class CursorManagerTests
{
    private static (SettingsModel settings, FakeCursorNative native, CursorManager manager) Create(
        NotificationState state = NotificationState.AcceptsNotifications,
        int initialCount = 0)
    {
        var settings = new SettingsModel();
        var native = new FakeCursorNative(initialCount) { State = state };
        return (settings, native, new CursorManager(settings, native));
    }

    [Fact]
    public void Update_HidesSystemCursor_WhenEnabled()
    {
        var (_, native, manager) = Create();

        manager.Update();

        Assert.True(manager.IsHidden);
        Assert.Equal(-1, native.Count);
    }

    [Fact]
    public void Update_DoesNotHide_WhenHideSystemCursorDisabled()
    {
        var (settings, native, manager) = Create();
        settings.HideSystemCursor = false;

        manager.Update();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void Update_DoesNotHide_WhenGloballyDisabled()
    {
        var (settings, native, manager) = Create();
        settings.Enabled = false;

        manager.Update();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void Hide_IsIdempotent()
    {
        var (_, native, manager) = Create();

        manager.Hide();
        manager.Hide();
        manager.Hide();

        Assert.Equal(1, native.Calls);
    }

    [Fact]
    public void Restore_WithoutHide_DoesNothing()
    {
        var (_, native, manager) = Create();

        manager.Restore();

        Assert.Equal(0, native.Calls);
        Assert.False(manager.IsHidden);
    }

    [Fact]
    public void Restore_AfterHide_BalancesCountExactly()
    {
        var (_, native, manager) = Create();

        manager.Hide();
        manager.Restore();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Count);
    }

    [Fact]
    public void Restore_IsIdempotent()
    {
        var (_, native, manager) = Create();

        manager.Hide();
        manager.Restore();
        int afterFirstRestore = native.Calls;
        manager.Restore();

        Assert.Equal(afterFirstRestore, native.Calls);
    }

    [Fact]
    public void Hide_CompensatesHighInitialCount()
    {
        var (_, native, manager) = Create(initialCount: 6);

        manager.Hide();

        Assert.True(manager.IsHidden);
        Assert.True(native.Count < 0);
    }

    [Theory]
    [InlineData(NotificationState.RunningD3DFullScreen)]
    [InlineData(NotificationState.PresentationMode)]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.NotPresent)]
    [InlineData(NotificationState.Error)]
    public void Fallback_RestoresCursorInUnsupportedScenarios(NotificationState state)
    {
        var (_, _, manager) = Create();
        manager.Update();
        Assert.True(manager.IsHidden);

        var (_, _, fallbackManager) = Create(state);
        fallbackManager.Update();

        Assert.True(fallbackManager.IsFallbackActive);
        Assert.False(fallbackManager.IsHidden, "全屏等场景必须把系统箭头还给用户");
    }

    [Fact]
    public void Fallback_RecordsLastState()
    {
        var (_, _, manager) = Create(NotificationState.RunningD3DFullScreen);

        manager.Update();

        Assert.Equal(NotificationState.RunningD3DFullScreen, manager.LastState);
    }

    [Fact]
    public void Fallback_DisabledBySetting_KeepsCursorHidden()
    {
        var (settings, _, manager) = Create(NotificationState.RunningD3DFullScreen);
        settings.AutoFallback = false;

        manager.Update();

        Assert.False(manager.IsFallbackActive);
        Assert.True(manager.IsHidden, "用户显式关闭兜底时应尊重用户选择");
    }

    [Fact]
    public void Fallback_LeavingFullScreen_ReHidesCursor()
    {
        var (_, native, manager) = Create(NotificationState.RunningD3DFullScreen);
        manager.Update();
        Assert.False(manager.IsHidden);

        native.State = NotificationState.AcceptsNotifications;
        bool changed = manager.EvaluateFallback();
        manager.Update();

        Assert.True(changed);
        Assert.True(manager.IsHidden);
    }

    [Fact]
    public void Update_WhenEnablingAfterDisable_HidesAgain()
    {
        var (settings, _, manager) = Create();
        settings.Enabled = false;
        manager.Update();

        settings.Enabled = true;
        manager.Update();

        Assert.True(manager.IsHidden);
    }

    [Fact]
    public void ForceCursorVisible_OverridesSettings()
    {
        var (settings, native, manager) = Create();
        manager.ForceCursorVisible = true;

        manager.Update();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Calls);
        Assert.True(settings.HideSystemCursor, "设置值本身不应被诊断开关改写");
    }

    [Fact]
    public void ForceCursorVisible_CanBeTurnedOffAtRuntime()
    {
        var (_, _, manager) = Create();
        manager.ForceCursorVisible = true;
        manager.Update();

        manager.ForceCursorVisible = false;
        manager.Update();

        Assert.True(manager.IsHidden);
    }

    [Fact]
    public void Update_WhenDisablingAfterEnable_RestoresCursor()
    {
        var (settings, native, manager) = Create();
        manager.Update();
        Assert.True(manager.IsHidden);

        settings.Enabled = false;
        manager.Update();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Count);
    }

    [Fact]
    public void OverlayUnavailable_RestoresCursor()
    {
        var (_, native, manager) = Create();
        manager.Update();
        Assert.True(manager.IsHidden);

        // 设备丢失 / Present 连续失败：不能再让用户「既没箭头也没黑洞」
        manager.OverlayUnavailable = true;
        manager.Update();

        Assert.False(manager.IsHidden);
        Assert.Equal(0, native.Count);
    }

    [Fact]
    public void OverlayUnavailable_Recovers_HidesAgain()
    {
        var (_, _, manager) = Create();
        manager.OverlayUnavailable = true;
        manager.Update();
        Assert.False(manager.IsHidden);

        manager.OverlayUnavailable = false;
        manager.Update();

        Assert.True(manager.IsHidden);
    }

    /// <summary>
    /// ShowCursor 只影响本进程，实测无法隐藏系统箭头，
    /// 因此隐藏时必须把标准系统光标整体替换为透明光标。
    /// </summary>
    [Fact]
    public void Hide_ReplacesAllStandardSystemCursors()
    {
        var (_, native, manager) = Create();

        manager.Update();

        Assert.Contains(NativeMethods.OCR_NORMAL, native.ReplacedCursorIds);
        Assert.Equal(NativeMethods.SystemCursorIds.Length, native.ReplacedCursorIds.Count);
    }

    [Fact]
    public void Restore_ReloadsUserCursorScheme()
    {
        var (_, native, manager) = Create();
        manager.Update();
        Assert.True(manager.IsHidden);

        manager.Restore();

        Assert.Equal(1, native.ReloadCount);
        Assert.False(manager.IsHidden);
    }

    [Fact]
    public void Restore_IsNotCalledRepeatedlyWhileVisible()
    {
        var (_, native, manager) = Create();
        manager.Update();
        manager.Restore();

        manager.Restore();

        // 每秒轮询不应反复重载光标方案
        Assert.Equal(1, native.ReloadCount);
    }

    [Fact]
    public void EnsureSystemCursorsRestored_ReloadsScheme()
    {
        var (_, native, manager) = Create();

        manager.EnsureSystemCursorsRestored();

        Assert.Equal(1, native.ReloadCount);
    }

    /// <summary>
    /// 系统光标是全局替换成透明光标的，指针停在自己窗口上时必须还回来，
    /// 否则用户连设置界面都操作不了。
    /// </summary>
    [Fact]
    public void PointerOverOwnWindow_RestoresCursor()
    {
        var (_, native, manager) = Create();
        manager.Update();
        Assert.True(manager.IsHidden);

        manager.PointerOverOwnWindow = true;
        manager.Update();

        Assert.False(manager.IsHidden);
        // 进入自己的窗口要重新加载用户的光标方案
        Assert.Equal(1, native.ReloadCount);
    }

    [Fact]
    public void PointerOverOwnWindow_LeavingHidesAgain()
    {
        var (_, native, manager) = Create();
        manager.Update();
        manager.PointerOverOwnWindow = true;
        manager.Update();
        Assert.False(manager.IsHidden);

        manager.PointerOverOwnWindow = false;
        manager.Update();

        Assert.True(manager.IsHidden);
        // 只应在进入时重载一次
        Assert.Equal(1, native.ReloadCount);
    }

    [Fact]
    public void CursorReplacement_DoesNotHappenWhilePointerOverOwnWindow()
    {
        var (_, native, manager) = Create();
        manager.PointerOverOwnWindow = true;

        manager.Update();

        Assert.Empty(native.ReplacedCursorIds);
        Assert.False(manager.IsHidden);
    }

    [Fact]
    public void CursorReplacement_DoesNotHappenWhileFallbackActive()
    {
        var (_, native, manager) = Create(NotificationState.RunningD3DFullScreen);

        manager.Update();

        Assert.Empty(native.ReplacedCursorIds);
        Assert.False(manager.IsHidden);
    }
}
