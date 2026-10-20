namespace BlackHoleCursor.Core;

/// <summary>
/// 运行时宿主：集中持有光标接管、覆盖层等全局资源，
/// 保证「任何退出路径都能恢复系统光标」这一最高优先级回归点。
/// </summary>
public static class RuntimeHost
{
    private static readonly object Gate = new();
    private static CursorManager? _cursorManager;
    private static IOverlayController? _overlay;

    public static bool IsStarted { get; private set; }

    public static CursorManager? Cursor => _cursorManager;

    public static IOverlayController? Overlay => _overlay;

    /// <param name="takeoverCursor">
    /// false 时不隐藏系统光标（诊断用）。这不会修改用户的配置值，只是本次运行不接管。
    /// </param>
    public static void Start(
        SettingsModel settings,
        ICursorNative? cursorNative = null,
        IOverlayController? overlay = null,
        bool takeoverCursor = true)
    {
        lock (Gate)
        {
            if (IsStarted)
            {
                return;
            }

            _cursorManager = new CursorManager(settings, cursorNative)
            {
                ForceCursorVisible = !takeoverCursor,
            };
            _overlay = overlay;

            // 自愈：上一次异常退出可能残留透明光标，先强制重载用户方案
            _cursorManager.EnsureSystemCursorsRestored();
            _cursorManager.Update();
            _overlay?.Start();
            IsStarted = true;
        }
    }

    public static void Refresh()
    {
        lock (Gate)
        {
            if (!IsStarted)
            {
                return;
            }

            _cursorManager?.Update();
        }
    }

    /// <summary>
    /// 指针进入/离开程序自己的窗口。进入时立刻恢复系统光标，
    /// 否则用户连自己的设置界面都没法操作。
    /// </summary>
    public static void SetPointerOverOwnWindow(bool over)
    {
        lock (Gate)
        {
            if (_cursorManager is null || _cursorManager.PointerOverOwnWindow == over)
            {
                return;
            }

            _cursorManager.PointerOverOwnWindow = over;
            _cursorManager.Update();
        }
    }

    /// <summary>
    /// 告知运行时覆盖层是否还能渲染。不可用时立即恢复系统光标，
    /// 避免出现「箭头被隐藏、黑洞又没画出来」的失明状态（plan.md 2.1）。
    /// </summary>
    public static void SetOverlayUnavailable(bool unavailable)
    {
        lock (Gate)
        {
            if (_cursorManager is null || _cursorManager.OverlayUnavailable == unavailable)
            {
                return;
            }

            _cursorManager.OverlayUnavailable = unavailable;
            _cursorManager.Update();
        }
    }

    /// <summary>关闭全部运行时资源并恢复系统光标。可重复调用。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!IsStarted)
            {
                return;
            }

            try
            {
                _overlay?.Stop();
            }
            finally
            {
                _cursorManager?.Restore();
                _cursorManager = null;
                _overlay = null;
                IsStarted = false;
            }
        }
    }
}
