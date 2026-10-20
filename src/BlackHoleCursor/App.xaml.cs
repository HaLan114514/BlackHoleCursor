using System.Windows;
using System.Windows.Threading;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using BlackHoleCursor.UI;
using Hardcodet.Wpf.TaskbarNotification;

namespace BlackHoleCursor;

public partial class App : Application
{
    // 静态构造函数里抛异常会让整个 App 不可用，这里做兜底（单实例唤醒是次要功能）
    private static readonly uint WM_SHOW_ME = RegisterShowMeMessage();

    private static uint RegisterShowMeMessage()
    {
        try
        {
            return NativeMethods.RegisterWindowMessage("BlackHoleCursor_ShowMe");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("注册单实例唤醒消息失败", ex);
            return 0;
        }
    }

    private SingleInstanceLock? _instanceLock;
    private TaskbarIcon? _trayIcon;
    private OverlayRunner? _overlay;
    private ClipboardCompensationHost? _clipboard;
    private DispatcherTimer? _fallbackTimer;
    private readonly PerformanceMonitor _performance = new();
    private bool _fallbackNotified;
    private bool _renderUnavailableNotified;
    private bool _softwareRenderingNotified;

    public static new App Current => (App)Application.Current;

    public SettingsService SettingsService { get; private set; } = null!;

    public PresetService Presets { get; private set; } = null!;

    public SettingsModel Settings => SettingsService.Settings;

    /// <summary>
    /// M5.2：预设切换的 1 秒交叉淡入状态（prd-v2 F2-5）。
    /// 覆盖层与主界面预览**共用同一个实例**，否则桌面在淡入、预览却瞬间跳变。
    /// </summary>
    public LookFade PresetFade { get; } = new();

    /// <summary>
    /// M6.1（prd-v2 F6-4）：自动档的步数调节器。与 `PresetFade` 同理，
    /// 覆盖层与预览区共用同一个实例，否则两边渲染出的画面不一样。
    /// </summary>
    public AutoQualityController AutoQuality { get; } = new();

    /// <summary>
    /// 应用预设**之前**调用：抓住当前外观作为淡入起点。
    /// 顺序反了（先改设置再 Begin）起点就是新外观，等于没有淡入。
    /// </summary>
    public void BeginPresetFade() => PresetFade.Begin(LookPresets.FromSettings(Settings));

    public MainWindow? MainWindowRef { get; private set; }

    public TaskbarIcon? TrayIcon => _trayIcon;

    /// <summary>托盘右键菜单是否展开（此时也要恢复系统光标）。</summary>
    public bool IsTrayMenuOpen { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // 系统光标已被替换成透明光标，任何非正常退出都必须把它换回来
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RuntimeHost.Shutdown();

        _instanceLock = new SingleInstanceLock();
        RuntimeLog.Info("进程启动");
        if (!_instanceLock.IsPrimary)
        {
            NativeMethods.PostMessage(NativeMethods.HwndBroadcast, WM_SHOW_ME, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        SettingsService = new SettingsService();
        SettingsService.Load();

        // 诊断开关：--no-takeover 时本次运行不接管系统光标（不会改写用户配置）
        bool takeover = !e.Args.Any(a => string.Equals(a, "--no-takeover", StringComparison.OrdinalIgnoreCase));

        Presets = new PresetService();

        // 诊断开关：--jitter-log 时逐帧输出窗口/黑洞/抓屏数据（fixes-v2 问题 5 取证用）
        bool jitterLog = e.Args.Any(a => string.Equals(a, "--jitter-log", StringComparison.OrdinalIgnoreCase));

        _overlay = new OverlayRunner(
            SettingsService.Settings,
            lookFade: PresetFade,
            autoQuality: AutoQuality,
            jitterLog: jitterLog);
        RuntimeHost.Start(SettingsService.Settings, overlay: _overlay, takeoverCursor: takeover);
        _overlay.Start();
        RuntimeLog.Info($"程序启动（takeover={takeover}）");

        _trayIcon = CreateTrayIcon();

        // F8 截图补偿：覆盖层被排除出系统捕获，全屏截图里没有黑洞，
        // 检测到剪贴板出现全屏位图就把它合成回去。
        _clipboard = new ClipboardCompensationHost(
            SettingsService.Settings,
            () => RuntimeHost.Overlay?.VisualPosition ?? (0, 0),
            () => RuntimeHost.Overlay?.ElapsedSeconds ?? 0);
        _clipboard.Start();

        // 每秒轮询一次系统状态：全屏/演示场景自动兜底恢复系统光标
        _fallbackTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnFallbackPoll,
            Dispatcher);
        _fallbackTimer.Start();

        MainWindowRef = new MainWindow(SettingsService);
        MainWindowRef.Show();
        MainWindowRef.RegisterShowMeMessage(WM_SHOW_ME);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeLog.Info($"程序退出（code={e.ApplicationExitCode}）");
        RuntimeHost.Shutdown();

        _fallbackTimer?.Stop();
        _fallbackTimer = null;

        _clipboard?.Dispose();
        _clipboard = null;

        SettingsService?.Flush();
        SettingsService?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visibility = Visibility.Collapsed;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    private void OnFallbackPoll(object? sender, EventArgs e)
    {
        RuntimeHost.Refresh();

        var cursor = RuntimeHost.Cursor;
        if (cursor is null)
        {
            return;
        }

        if (cursor.IsFallbackActive && !_fallbackNotified)
        {
            _fallbackNotified = true;
            _trayIcon?.ShowBalloonTip(
                "BlackHole Cursor",
                "检测到全屏/演示场景，已临时恢复系统光标。",
                BalloonIcon.Info);
        }
        else if (!cursor.IsFallbackActive)
        {
            _fallbackNotified = false;
        }

        PollPerformance();
        PollOverlayAvailability();
        PollSoftwareRendering();
    }

    /// <summary>
    /// M6.1：退到 WARP 软件渲染时提示一次（prd-v2 非功能需求）。
    /// 状态行里一直有「软件渲染」字样，这里只额外弹一次气泡，让用户知道为什么卡。
    /// </summary>
    private void PollSoftwareRendering()
    {
        if (_overlay is null || !_overlay.IsUsingSoftwareRendering)
        {
            _softwareRenderingNotified = false;
            return;
        }

        if (_softwareRenderingNotified)
        {
            return;
        }

        _softwareRenderingNotified = true;
        _trayIcon?.ShowBalloonTip(
            "BlackHole Cursor",
            "正在使用软件渲染（WARP），帧率会明显偏低。可在「渲染」里把质量档位调低。",
            BalloonIcon.Warning);
    }

    /// <summary>
    /// 覆盖层彻底不可用时（设备丢失无法恢复 / 启动失败）恢复系统光标，
    /// 否则用户会同时失去箭头和黑洞（plan.md 2.1）。
    /// </summary>
    private void PollOverlayAvailability()
    {
        if (_overlay is null)
        {
            return;
        }

        bool unavailable = _overlay.StartupError is not null || _overlay.IsDeviceLost;
        RuntimeHost.SetOverlayUnavailable(unavailable);

        if (unavailable && !_renderUnavailableNotified)
        {
            _renderUnavailableNotified = true;
            _trayIcon?.ShowBalloonTip(
                "BlackHole Cursor",
                "渲染不可用，已恢复系统光标。可在托盘菜单退出后重新启动程序。",
                BalloonIcon.Warning);
        }
        else if (!unavailable)
        {
            _renderUnavailableNotified = false;
        }
    }

    /// <summary>
    /// 持续低帧率时提示一次降级建议（PRD 1.2：v1.0 不自动降级，只提示）。
    /// 本方法由每秒一次的定时器驱动，因此固定传入 1 秒的采样间隔。
    /// </summary>
    private void PollPerformance()
    {
        var overlay = RuntimeHost.Overlay;
        if (overlay is null)
        {
            return;
        }

        if (!_performance.Update(overlay.FramesPerSecond, 1.0, Settings.LensEnabled))
        {
            return;
        }

        _trayIcon?.ShowBalloonTip(
            "BlackHole Cursor",
            "帧率持续低于 45 FPS。建议降低「影响半径」，或关闭引力透镜以获得更流畅的体验。",
            BalloonIcon.Warning);
    }

    public void ShowMainWindow()
    {
        if (MainWindowRef is null)
        {
            return;
        }

        MainWindowRef.Show();
        MainWindowRef.WindowState = WindowState.Normal;
        MainWindowRef.Activate();
    }

    public void UpdateTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Icon?.Dispose();
        // v4（V4-7）：改用资源里的那两张（不再每次现画）
        _trayIcon.Icon = IconFactory.LoadAppIcon(Settings.Enabled);
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            Icon = IconFactory.LoadAppIcon(Settings.Enabled),
            ToolTipText = "BlackHole Cursor",
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
        };

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(CreateMenuItem("开关黑洞效果", (_, _) => ToggleEnabled()));
        menu.Items.Add(CreateMenuItem("打开主界面", (_, _) => ShowMainWindow()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(CreateMenuItem("退出", (_, _) => RequestExit()));
        // 托盘菜单也是自己的界面：打开期间必须把系统光标还回来，否则菜单没法点。
        // 这里只置状态位，由主界面的指针轮询统一决定是否恢复光标。
        menu.Opened += (_, _) => IsTrayMenuOpen = true;
        menu.Closed += (_, _) => IsTrayMenuOpen = false;

        tray.ContextMenu = menu;

        return tray;
    }

    private static System.Windows.Controls.MenuItem CreateMenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    /// <summary>切换「启用黑洞」。主界面的大开关、托盘菜单与全局快捷键共用这一条路径。</summary>
    public void ToggleEnabled()
    {
        Settings.Enabled = !Settings.Enabled;
        SettingsService.Flush();
        UpdateTrayIcon();
    }

    public void RequestExit()
    {
        SettingsService.Flush();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 任何 UI 线程异常都必须先恢复系统光标，避免用户「失去鼠标」
        RuntimeLog.Error("UI 线程异常", e.Exception);
        RuntimeHost.Shutdown();
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        RuntimeLog.Error("后台线程异常", e.ExceptionObject as Exception);
        RuntimeHost.Shutdown();
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
