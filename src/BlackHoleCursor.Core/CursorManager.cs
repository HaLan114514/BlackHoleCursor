using System.Runtime.InteropServices;

namespace BlackHoleCursor.Core;

/// <summary>SHQueryUserNotificationState 的返回值。</summary>
public enum NotificationState
{
    Error = 0,
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

/// <summary>光标相关原生调用抽象，便于单元测试使用替身。</summary>
public interface ICursorNative
{
    /// <summary>ShowCursor 的包装，返回调用后的显示计数。</summary>
    int ShowCursor(bool show);

    /// <summary>把某个标准系统光标替换为指定光标（隐藏系统箭头用）。</summary>
    bool SetSystemCursor(IntPtr cursor, uint id);

    /// <summary>重新加载用户的光标方案（还原被替换掉的系统光标）。</summary>
    bool ReloadSystemCursors();

    NotificationState QueryUserState();
}

public sealed class WindowsCursorNative : ICursorNative
{
    public int ShowCursor(bool show) => NativeMethods.ShowCursor(show);

    public bool SetSystemCursor(IntPtr cursor, uint id) => NativeMethods.SetSystemCursor(cursor, id);

    public bool ReloadSystemCursors() => NativeMethods.SystemParametersInfo(
        NativeMethods.SPI_SETCURSORS,
        0,
        IntPtr.Zero,
        NativeMethods.SPIF_SENDCHANGE);

    public NotificationState QueryUserState()
    {
        int hr = NativeMethods.SHQueryUserNotificationState(out int state);
        if (hr != 0)
        {
            return NotificationState.Error;
        }

        return Enum.IsDefined(typeof(NotificationState), state)
            ? (NotificationState)state
            : NotificationState.Error;
    }
}

/// <summary>
/// 系统光标接管：隐藏/恢复、幂等计数、全屏场景智能兜底。
/// 任何退出路径都必须调用 <see cref="Restore"/>，否则用户会「失去鼠标」。
/// </summary>
public sealed class CursorManager
{
    private const int MaxShowCursorCalls = 64;

    private readonly SettingsModel _settings;
    private readonly ICursorNative _native;
    private bool _hidden;
    private bool _fallbackActive;

    public CursorManager(SettingsModel settings, ICursorNative? native = null)
    {
        _settings = settings;
        _native = native ?? new WindowsCursorNative();
    }

    /// <summary>当前系统光标是否处于被我们隐藏的状态。</summary>
    public bool IsHidden => _hidden;

    /// <summary>是否因全屏/演示等场景触发了兜底（已恢复系统箭头）。</summary>
    public bool IsFallbackActive => _fallbackActive;

    /// <summary>
    /// 诊断开关：为 true 时无论设置如何都不隐藏系统光标。
    /// 与设置分离，避免诊断参数被写进用户配置文件。
    /// </summary>
    public bool ForceCursorVisible { get; set; }

    /// <summary>
    /// 覆盖层已无法渲染（设备丢失无法恢复 / Present 连续失败）。
    /// 此时若继续隐藏系统光标，用户会既看不到箭头也看不到黑洞，必须立即恢复（plan.md 2.1）。
    /// </summary>
    public bool OverlayUnavailable { get; set; }

    /// <summary>
    /// 指针是否停在程序自己的窗口（主界面 / 托盘菜单）上。
    /// 系统光标是全局替换成透明光标的，若不在这里恢复，用户连自己的设置界面都操作不了。
    /// </summary>
    public bool PointerOverOwnWindow { get; set; }

    public NotificationState LastState { get; private set; } = NotificationState.Error;

    /// <summary>上一次的决策。只在翻转时写日志，避免每秒轮询刷屏。</summary>
    private bool _lastDecision = true;

    /// <summary>根据设置与当前系统状态决定隐藏或恢复。每次设置变化/定时轮询时调用。</summary>
    public void Update()
    {
        EvaluateFallback();

        bool desiredHidden = _settings.Enabled
                             && _settings.HideSystemCursor
                             && !ForceCursorVisible
                             && !OverlayUnavailable
                             && !PointerOverOwnWindow
                             && !_fallbackActive;

        if (desiredHidden != _lastDecision)
        {
            _lastDecision = desiredHidden;
            RuntimeLog.Info(
                $"光标可见性 → {(desiredHidden ? "隐藏" : "显示")}：" +
                $"Enabled={_settings.Enabled}, HideSystemCursor={_settings.HideSystemCursor}, " +
                $"ForceVisible={ForceCursorVisible}, OverlayUnavailable={OverlayUnavailable}, " +
                $"PointerOverOwnWindow={PointerOverOwnWindow}, Fallback={_fallbackActive}, State={LastState}");
        }

        if (desiredHidden)
        {
            Hide();
        }
        else
        {
            Restore();
        }
    }

    /// <summary>隐藏系统光标（幂等）。</summary>
    public void Hide()
    {
        if (_hidden)
        {
            return;
        }

        int depth = 0;
        int count = 0;
        while (depth < MaxShowCursorCalls)
        {
            count = _native.ShowCursor(false);
            depth++;
            if (count < 0)
            {
                break;
            }
        }

        _hidden = true;

        // ShowCursor 只影响本进程，必须再把系统光标换成透明光标才能真正看不到箭头
        bool replaced = ApplyTransparentCursor();
        RuntimeLog.Info(
            $"隐藏系统光标：ShowCursor 调用 {depth} 次（计数 {count}），替换系统光标{(replaced ? "成功" : "失败")}");
    }

    /// <summary>恢复系统光标（幂等）。</summary>
    public void Restore()
    {
        if (!_hidden)
        {
            return;
        }

        // 先还原用户的光标方案，再恢复显示计数
        bool reloaded = RestoreSystemCursors();

        int depth = 0;
        int count = 0;
        while (depth < MaxShowCursorCalls)
        {
            count = _native.ShowCursor(true);
            depth++;
            if (count >= 0)
            {
                break;
            }
        }

        _hidden = false;
        RuntimeLog.Info(
            $"恢复系统光标：ShowCursor 调用 {depth} 次（计数 {count}），重载光标方案{(reloaded ? "成功" : "失败")}");
    }

    /// <summary>
    /// 启动时自愈：上一次异常退出可能残留被替换掉的透明光标，这里强制重载用户方案。
    /// </summary>
    public void EnsureSystemCursorsRestored()
    {
        bool reloaded = RestoreSystemCursors();
        RuntimeLog.Info($"启动时重载系统光标方案{(reloaded ? "成功" : "失败")}");
    }

    private bool ApplyTransparentCursor()
    {
        int succeeded = 0;

        // 每个 ID 都要传一个新句柄：SetSystemCursor 会接管并销毁传入的句柄
        foreach (uint id in NativeMethods.SystemCursorIds)
        {
            IntPtr cursor = InvisibleCursor.Create();
            if (cursor == IntPtr.Zero)
            {
                continue;
            }

            if (_native.SetSystemCursor(cursor, id))
            {
                succeeded++;
            }
        }

        if (succeeded != NativeMethods.SystemCursorIds.Length)
        {
            RuntimeLog.Info(
                $"替换系统光标：{succeeded}/{NativeMethods.SystemCursorIds.Length} 成功（GetLastError={Marshal.GetLastWin32Error()}）");
        }

        return succeeded == NativeMethods.SystemCursorIds.Length;
    }

    private bool RestoreSystemCursors() => _native.ReloadSystemCursors();

    /// <summary>检测是否需要兜底（全屏游戏 / 演示模式 / 会话锁定）。</summary>
    public bool EvaluateFallback()
    {
        NotificationState state = _native.QueryUserState();
        LastState = state;

        bool shouldFallback = _settings.AutoFallback
                              && (state is NotificationState.RunningD3DFullScreen
                                  or NotificationState.PresentationMode
                                  or NotificationState.Busy
                                  or NotificationState.NotPresent
                                  or NotificationState.Error);

        bool changed = shouldFallback != _fallbackActive;
        _fallbackActive = shouldFallback;
        return changed;
    }
}
