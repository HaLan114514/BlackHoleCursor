using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using BlackHoleCursor.UI;
using BlackHoleCursor.UI.Views;

namespace BlackHoleCursor;

public partial class MainWindow : Window
{
    /// <summary>全局快捷键「启用/停用黑洞」的 ID（Ctrl+Alt+B）。</summary>
    private const int ToggleHotKeyId = 0x4248;

    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly PreviewSurface _preview;
    private uint _showMeMessage;
    private bool _previewStarted;
    private IntPtr _hotKeyWindow;
    private bool _hotKeyRegistered;
    private readonly DispatcherTimer _pointerProbe;

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        DataContext = settingsService.Settings;

        // M5.2 / M6.1：预览与桌面覆盖层共用同一个淡入实例与自动档调节器
        _preview = new PreviewSurface(
            settingsService.Settings,
            lookFade: App.Current.PresetFade,
            autoQuality: App.Current.AutoQuality);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => TogglePreview();

        // 系统光标被整体换成透明光标，指针进入自己的窗口时必须把它还回来，
        // 否则用户连设置界面都点不了（调坏参数后也就没法恢复了）。
        // 这里用轮询而不是 WPF 的 MouseEnter/MouseLeave：指针一次性跳到窗口外时
        // MouseLeave 不一定会触发，会导致光标一直卡在「显示」状态。
        _pointerProbe = new DispatcherTimer(
            TimeSpan.FromMilliseconds(60),
            DispatcherPriority.Background,
            OnPointerProbe,
            Dispatcher);

        Closed += OnClosed;

        settingsService.Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Navigate("Appearance");
        UpdateStatus();
        ShowMigrationNoticeOnce();
        TogglePreview();
        _pointerProbe.Start();
    }

    /// <summary>
    /// M5.3：旧版（v1）配置被识别并回落后，提示**一次**。
    /// 文档要求「提示一次」而不是每次启动都弹 —— 旧文件已被挪走，下次加载不会再认出来，
    /// 这里再消费掉提示对象，避免同一次运行里重复弹。
    /// </summary>
    private void ShowMigrationNoticeOnce()
    {
        var notice = _settingsService.TakeMigrationNotice();
        if (notice is null)
        {
            return;
        }

        SavedHint.Text = notice.Message;
        App.Current.TrayIcon?.ShowBalloonTip("BlackHole Cursor", notice.Message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    /// <summary>判断指针是否停在自己的窗口上，用于在该范围内恢复系统光标。</summary>
    private bool _lastPointerOver;

    private void OnPointerProbe(object? sender, EventArgs e)
    {
        // 必须先完整调用一次，避免 && 短路导致 out 变量未赋值
        bool inside = IsCursorOverWindow(out var cursor, out var rect);
        bool over = (IsVisible && inside) || App.Current.IsTrayMenuOpen;

        if (over != _lastPointerOver)
        {
            _lastPointerOver = over;
            RuntimeLog.Info(
                $"指针 → {(over ? "窗口内" : "窗口外")}：光标 ({cursor.X},{cursor.Y})，" +
                $"窗口 {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}，IsVisible={IsVisible}");
        }

        RuntimeHost.SetPointerOverOwnWindow(over);

        // fixes-v2 问题 3：预览的缩小倍率是**每帧渲染时**算出来的，
        // 而设置变化时预览还没渲染过 —— 所以角标要跟着这个轮询一起刷新，
        // 否则「已经缩小了」这件事永远不会显示出来。
        UpdatePreviewScaleHint();
    }

    private bool IsCursorOverWindow(out NativeMethods.POINT cursor, out NativeMethods.RECT rect)
    {
        cursor = default;
        rect = default;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out rect))
        {
            return false;
        }

        NativeMethods.GetCursorPos(out cursor);
        return cursor.X >= rect.Left && cursor.X < rect.Right
               && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    /// <summary>窗口隐藏到托盘时停止预览渲染，避免白耗 CPU。</summary>
    private void TogglePreview()
    {
        if (!IsLoaded)
        {
            return;
        }

        // 主界面可见时暂停降帧策略，保证预览与桌面效果同步流畅（plan.md 5.3）
        RuntimeHost.Overlay?.SetUiActive(IsVisible);

        if (IsVisible && !_previewStarted)
        {
            _preview.Start();
            PreviewImage.Source = _preview.Source;
            _previewStarted = true;
        }
        else if (!IsVisible && _previewStarted)
        {
            _preview.Stop();
        }
    }

    /// <summary>为 true 时允许真正关闭窗口（程序退出流程）。</summary>
    public bool AllowClose { get; set; }

    public void RegisterShowMeMessage(uint message)
    {
        _showMeMessage = message;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 无论单实例唤醒消息有没有注册好，都要挂上钩子：全局快捷键也走这里
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);

        RegisterToggleHotKey();
    }

    /// <summary>注册 Ctrl+Alt+B：即使光标不可见，也能立刻把黑洞关掉。</summary>
    private void RegisterToggleHotKey()
    {
        _hotKeyWindow = new WindowInteropHelper(this).Handle;
        if (_hotKeyWindow == IntPtr.Zero)
        {
            return;
        }

        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        _hotKeyRegistered = NativeMethods.RegisterHotKey(_hotKeyWindow, ToggleHotKeyId, modifiers, NativeMethods.VK_B);

        HotKeyHint.Text = _hotKeyRegistered
            ? "快捷键 Ctrl + Alt + B 可随时开关"
            : "快捷键 Ctrl + Alt + B 注册失败（可能已被其它程序占用）";

        if (!_hotKeyRegistered)
        {
            RuntimeLog.Info($"注册全局快捷键失败（GetLastError={Marshal.GetLastWin32Error()}）");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pointerProbe.Stop();
        RuntimeHost.SetPointerOverOwnWindow(false);

        if (_hotKeyRegistered && _hotKeyWindow != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hotKeyWindow, ToggleHotKeyId);
            _hotKeyRegistered = false;
        }

        _preview.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_showMeMessage != 0 && (uint)msg == _showMeMessage)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            handled = true;
        }
        else if ((uint)msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == ToggleHotKeyId)
        {
            App.Current.ToggleEnabled();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        if (PageHost is null)
        {
            // XAML 初始化期间 ListBox 的 IsSelected 会提前触发导航，此时内容区尚未创建
            return;
        }

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "Appearance" => new AppearanceView(),
                "Presets" => new PresetView(),
                "Render" => new RenderView(),
                "Lens" => new LensView(),
                "Follow" => new FollowView(),
                // v4：原「程序设置」页已并入系统页（导航 7 项 → 6 项）
                "System" => new SystemView(),
                _ => new AppearanceView(),
            };
            _pages[tag] = page;
        }

        PageHost.Content = page;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            App.Current.UpdateTrayIcon();
        });

        RuntimeHost.Refresh();
        RuntimeHost.Overlay?.NotifySettingsChanged();
    }

    private void UpdateStatus()
    {
        var settings = _settingsService.Settings;
        string state = settings.Enabled
            ? (settings.FollowEnabled ? "运行中 · 惯性跟随已开启" : "运行中 · 精确跟随")
            : "已暂停（系统光标正常显示）";

        var overlay = RuntimeHost.Overlay as OverlayRunner;
        if (overlay is not null && settings.Enabled)
        {
            if (overlay.StartupError is not null)
            {
                state += $"\n渲染不可用：{overlay.StartupError.Message}";
            }
            else
            {
                state += $"\n{overlay.FramesPerSecond:F0} FPS";
                if (overlay.IsUsingSoftwareRendering)
                {
                    state += " · 软件渲染";
                }

                if (overlay.IsDegraded)
                {
                    state += " · 抓屏已降级";
                }

                // M6.1：自动档当前实际用的步数（用户没显式选档时才显示）
                if (settings.QualitySteps == QualitySteps.Auto)
                {
                    state += $" · 自动 {App.Current.AutoQuality.StepCount} 步";
                }
            }
        }

        // M6.2：预览自己起不来也要说清楚原因（多半是着色器编译或 D3D 设备问题）
        if (_preview.FailureReason is not null)
        {
            state += $"\n预览不可用：{_preview.FailureReason}";
        }

        StatusLine.Text = state;

        UpdatePreviewScaleHint();

        UndoButton.IsEnabled = _settingsService.CanUndo;
    }

    /// <summary>
    /// fixes-v2 问题 3（续）：预览是「真实比例为主、装不下才缩小」，
    /// 缩小的时候必须在角落标出倍率，否则用户会以为洞真的变小了。
    /// </summary>
    private void UpdatePreviewScaleHint()
    {
        double scale = _preview.FitScale;

        if (scale >= 0.995)
        {
            PreviewScaleHint.Visibility = Visibility.Collapsed;
            return;
        }

        string text = $"预览已缩小 ×{scale:0.00}";
        if (PreviewScaleHint.Text != text)
        {
            PreviewScaleHint.Text = text;
        }

        PreviewScaleHint.Visibility = Visibility.Visible;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        SavedHint.Text = _settingsService.Undo() ? "已撤销上一次改动" : "没有可撤销的改动";
        UpdateStatus();
    }

    /// <summary>总开关变化后立刻同步托盘图标与状态行。</summary>
    private void EnabledSwitch_Changed(object sender, RoutedEventArgs e)
    {
        App.Current.UpdateTrayIcon();
        UpdateStatus();
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.SaveNow();
        SavedHint.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要把所有参数恢复为默认值吗？",
            "BlackHole Cursor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // M5.2：恢复默认不该穿过一次 1 秒淡入（否则会看到「从旧外观慢慢变过去」）
        App.Current.PresetFade.Cancel();
        _settingsService.ResetToDefaults();
        SavedHint.Text = "已恢复默认";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
