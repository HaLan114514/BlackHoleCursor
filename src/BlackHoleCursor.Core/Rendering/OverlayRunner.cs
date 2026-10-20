namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 在独立线程上驱动覆盖层：窗口、交换链与渲染循环都在该线程创建/销毁，
/// 避免阻塞 UI 线程，也避免掉帧影响输入响应。
/// </summary>
public sealed class OverlayRunner : IOverlayController, IDisposable
{
    private readonly SettingsModel _settings;
    private readonly bool _preferHardware;
    private readonly LookFade _lookFade;
    private readonly AutoQualityController _autoQuality;
    private readonly bool _jitterLog;
    private Thread? _thread;
    private OverlayController? _controller;
    private Win32OverlayWindow? _window;
    private volatile bool _stopRequested;
    private volatile bool _uiActive;

    /// <param name="lookFade">
    /// M5.2：预设切换的 1 秒交叉淡入。由 App 创建并与预览区共用，
    /// 不传则本实例自建（此时淡入只作用在覆盖层上）。
    /// </param>
    /// <param name="autoQuality">
    /// M6.1：自动档的步数调节器。同样由 App 创建并与预览区共用，
    /// 否则预览会用旧的步数渲染，与桌面不一致。
    /// </param>
    public OverlayRunner(
        SettingsModel settings,
        bool preferHardware = true,
        LookFade? lookFade = null,
        AutoQualityController? autoQuality = null,
        bool jitterLog = false)
    {
        _settings = settings;
        _preferHardware = preferHardware;
        _lookFade = lookFade ?? new LookFade();
        _autoQuality = autoQuality ?? new AutoQualityController();
        _jitterLog = jitterLog;
    }

    public bool IsRunning => _controller?.IsRunning ?? false;

    public double FramesPerSecond => _controller?.FramesPerSecond ?? 0;

    public (double X, double Y) VisualPosition => _controller?.VisualPosition ?? (0, 0);

    public double ElapsedSeconds => _controller?.ElapsedSeconds ?? 0;

    /// <summary>启动失败原因（如缺少 D3D11），供主界面提示。</summary>
    public Exception? StartupError { get; private set; }

    public bool IsDegraded => _controller?.IsDegraded ?? false;

    public bool IsUsingSoftwareRendering => _controller?.IsUsingSoftwareRendering ?? false;

    /// <summary>设备丢失后重建渲染资源的次数。</summary>
    public int DeviceRebuildCount => _controller?.DeviceRebuildCount ?? 0;

    /// <summary>抓屏结果为全黑（受保护内容），扭曲层已被临时跳过。</summary>
    public bool IsLensSuppressed => _controller?.IsLensSuppressed ?? false;

    /// <summary>渲染资源是否因设备丢失无法恢复而彻底停止。</summary>
    public bool IsDeviceLost => _controller?.IsDeviceLost ?? false;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _stopRequested = false;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "BlackHoleCursor.Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopRequested = true;
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // 线程卡住时不再等待，避免退出流程挂死
        }

        _thread = null;
    }

    public void NotifySettingsChanged() => _controller?.NotifySettingsChanged();

    public void SetUiActive(bool active)
    {
        // 渲染线程可能还没建好控制器，先记住状态，创建时再补上
        _uiActive = active;
        _controller?.SetUiActive(active);
    }

    public void ReplayTransition() => _controller?.ReplayTransition();

    /// <summary>
    /// M5.2：开始一次 1 秒交叉淡入（必须在改设置之前调用）。
    /// 起点由**当前设置**算出，因此覆盖层还没建立控制器时也能正确开始。
    /// </summary>
    public void BeginLookFade() => _lookFade.Begin(LookPresets.FromSettings(_settings));

    /// <summary>问题 1：分段帧耗时（抓屏 / 渲染 / Present / 整帧），主界面「系统」页显示用。</summary>
    public FrameTiming? Timing => _controller?.Timing;

    private void Run()
    {
        try
        {
            // 问题 1：把系统定时器分辨率提到 1ms。
            // 默认是 15.6ms，MsgWaitForMultipleObjectsEx(1) 会睡满一两个滴答，
            // 实测把渲染线程压到了 32 FPS。副作用是略微增加功耗，退出时必须还原。
            NativeMethods.TimeBeginPeriod(1);
            RuntimeLog.Info("覆盖层线程启动");
            _window = new Win32OverlayWindow();
            _controller = new OverlayController(
                _settings,
                _window,
                new SystemMousePositionSource(),
                _preferHardware,
                lookFade: _lookFade,
                autoQuality: _autoQuality);
            _controller.JitterLogEnabled = _jitterLog;
            _controller.SetUiActive(_uiActive);
            _controller.Start();

            // 交换链在首帧才创建，其实际参数由 OverlayController 在首帧日志里报告
            RuntimeLog.Info($"覆盖层已启动（软件渲染={_controller.IsUsingSoftwareRendering}）");

            while (!_stopRequested)
            {
                // 覆盖层窗口在本线程创建，必须持续抽消息，否则窗口会被判定为「未响应」
                Win32OverlayWindow.WaitAndPump(1);
                _controller.Tick();
            }
        }
        catch (Exception ex)
        {
            StartupError = ex;
            RuntimeLog.Error("覆盖层线程异常退出", ex);
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
            RuntimeLog.Info("覆盖层线程结束");
            _controller?.Stop();
            _window?.Dispose();
            _controller = null;
            _window = null;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
