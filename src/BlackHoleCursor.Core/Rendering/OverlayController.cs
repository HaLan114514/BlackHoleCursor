using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using BlackHoleCursor.Core.Capture;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 桌面覆盖层控制器：窗口 + 交换链 + 抓屏 + 跟随物理 + 渲染循环编排。
/// 与 UI 无关，所有外部依赖都是接口，便于单元测试。
/// </summary>
public sealed class OverlayController : IOverlayController
{
    /// <summary>采样纹理边长（全黑帧检测用）。</summary>
    public const int BlackFrameSampleSize = 32;

    /// <summary>每多少帧做一次全黑帧采样，避免频繁 GPU→CPU 回读。</summary>
    public const int BlackFrameSampleInterval = 20;

    /// <summary>设备丢失后最多重建几次，超过则停止渲染（plan.md 5.2）。</summary>
    /// <summary>设备丢失最多重建这么多次（M6.2：与降级矩阵 <see cref="RenderFailure.Decide"/> 同一处定义）。</summary>
    public const int MaxDeviceRebuilds = RenderFailure.MaxDeviceRebuilds;

    /// <summary>可恢复的 Present 失败连续多少次后停止渲染。</summary>
    public const int MaxConsecutivePresentFailures = RenderFailure.MaxConsecutivePresentFailures;

    /// <summary>重申置顶层级的间隔（秒）。覆盖层要盖住任务栏，必须周期性重申 Z 序。</summary>
    public const double TopmostReassertSeconds = 0.5;

    /// <summary>
    /// fixes-v2 问题 5：整屏抓屏的刷新间隔**按实测抓屏耗时自适应** —— 抓屏越贵，隔得越久。
    /// 抓屏纹理锚在显示器上，只有桌面内容变了才需要重抓；黑洞移动不需要重抓。
    /// 本机 GDI 抓 1080p 全屏要 ~40ms（占整帧 95%），把帧率压到 ~20FPS，
    /// 黑洞只能以 20Hz 更新 → 鼠标一动就一格一格跳，观感就是「左右摇摆」。
    /// 自适应是为了不误伤快机器：那边 DXGI 抓屏只要 1ms，不该被无谓节流。
    /// </summary>
    public const double CaptureRefreshMinSeconds = 1.0 / 60.0;

    public const double CaptureRefreshMaxSeconds = 1.0 / 10.0;

    // ⚠️ 原先这里有一个 `CaptureMarginFactor = 0.05`（抓屏区域在窗口外再扩 5%）。
    // v3（P2）已删除：窗口变成整块显示器后，区域恒等于整屏，
    // margin 会被 clamp 掉、算不出别的结果 —— 而且它正是问题 9「旧区域」竞态的来源。

    /// <summary>
    /// 间隔 = 上次抓屏耗时 × 该系数（再钳制到上面的上下限）。
    /// 取 1.0：抓屏便宜时（局部更新约 15ms）间隔小于帧间隔，等于**每帧都抓**；
    /// 抓屏很贵时（整屏 40ms）才真正起到节流作用。
    /// </summary>
    public const double CaptureRefreshCostFactor = 1.0;

    private readonly SettingsModel _settings;
    private readonly IOverlayWindow _window;
    private readonly IMousePositionSource _mouse;
    private readonly bool _preferHardware;
    private readonly Func<ID3D11Device, CaptureCoordinator>? _captureFactory;
    private readonly FollowState _follow = new();
    private readonly WanderState _wander = new();
    private readonly FrameScheduler _scheduler;
    private readonly LookFade _lookFade;
    private readonly AutoQualityController _autoQuality;
    private readonly TransitionState _transition = new();
    private readonly FrameTiming _timing = new();
    private readonly Stopwatch _timingWatch = Stopwatch.StartNew();
    private readonly Stopwatch _stopwatch = new();
    private readonly BlackFrameDetector _blackFrames = new();
    private readonly byte[] _samplePixels = new byte[BlackFrameSampleSize * BlackFrameSampleSize * 4];

    private RenderDevice? _device;
    private SceneRenderer? _sceneRenderer;
    private CaptureCoordinator? _capture;

    /// <summary>
    /// fixes-v2 问题 7：GDI 抓屏的后台实例。只在抓屏**降级到 GDI** 时启用
    /// （DXGI 桌面复制本身是零拷贝同步、很快，不需要这一层）。
    /// </summary>
    private BackgroundGdiCapture? _background;

    /// <summary>后台抓屏是否至少上传成功过一帧（在此之前纹理还是空的）。</summary>
    private bool _backgroundReady;

    private ID3D11Texture2D? _captureTexture;
    private ID3D11ShaderResourceView? _captureView;
    private int _captureWidth;
    private int _captureHeight;
    private Vector4 _captureMap = new(1.0f, 1.0f, 0.0f, 0.0f);
    private int _sampleOriginX;
    private int _sampleOriginY;
    private double _lastCaptureSeconds = double.NegativeInfinity;

    /// <summary>
    /// 最近一次**真实**抓屏的耗时（ms）。初始 0 = 「还不知道」，
    /// 此时按最小间隔跑，不要凭空多节流。
    /// </summary>
    private double _lastCaptureCostMs;
    private ID3D11Texture2D? _sampleTexture;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private int _extent;
    private double _elapsedSeconds;
    private double _lastTopmostSeconds = double.NegativeInfinity;
    private double _lastFrameSeconds;
    private readonly Func<double> _clock;

    /// <summary>
    /// v3（P1.1）：上一次真正下发过的窗口矩形。窗口现在是整块显示器、不跟随光标，
    /// 所以只在它**真的变了**（换显示器 / 改分辨率）时才 <c>Move</c> ——
    /// ① layered 窗上无意义的 swapchain 重建会失败（参考实现 <c>main.rs:757-763</c> 明确警告）；
    /// ② 每帧 Move 是一次白白的窗口管理器往返。
    /// 默认值（0,0,0,0）不等于任何真实矩形，所以首次一定会下发一次。
    /// </summary>
    private OverlayGeometry.Rect _lastWindowRect;

    /// <summary>
    /// 未被渲染帧消费掉的累计时间（B1 修复）。
    /// 渲染节流只决定「画不画」，不能决定「物理推进多少」，
    /// 否则 Tick 频率越高、被跳过的帧越多，黑洞走得越慢。
    /// </summary>
    private double _pendingSeconds;
    private double _framesPerSecond;
    private (int X, int Y) _lastMouse;
    private bool _lastEnabled;
    private bool _running;
    private bool _deviceLost;
    private volatile bool _uiActive;
    private int _renderCount;
    private int _captureFailures;
    private int _deviceRebuilds;
    private int _consecutivePresentFailures;

    /// <param name="preferHardware">false 时直接走 WARP 软件渲染（测试与无显卡环境）。</param>
    /// <param name="captureFactory">
    /// 抓屏方案工厂，默认使用「**WGC 为主 + GDI BitBlt 兜底**」（plan-wgc W3）。
    /// 抽成工厂是因为抓屏资源与 D3D11 设备绑定，设备重建时必须重新创建。
    /// </param>
    /// <param name="clock">
    /// 时间源（秒）。默认用 <see cref="Stopwatch"/>；测试传入虚拟时钟即可在不真实等待的前提下
    /// 按任意 Tick 频率推进同一段仿真时间，用来锁 B1「移速必须与帧率无关」（plan-v2 M3.1）。
    /// 宿主与帧调度共用同一个时钟。
    /// </param>
    /// <param name="lookFade">
    /// M5.2：预设切换的交叉淡入状态。宿主与预览区**共用同一个实例**，
    /// 否则预览会瞬间跳变、桌面却在淡入，两边不一致。
    /// </param>
    /// <param name="autoQuality">
    /// M6.1：自动档的步数调节器。同样与预览区共用，否则两边渲染出的画面不一样。
    /// </param>
    public OverlayController(
        SettingsModel settings,
        IOverlayWindow window,
        IMousePositionSource mouse,
        bool preferHardware = true,
        Func<ID3D11Device, CaptureCoordinator>? captureFactory = null,
        Func<double>? clock = null,
        LookFade? lookFade = null,
        AutoQualityController? autoQuality = null)
    {
        _settings = settings;
        _window = window;
        _mouse = mouse;
        _preferHardware = preferHardware;
        _captureFactory = captureFactory;
        _clock = clock ?? (() => _stopwatch.Elapsed.TotalSeconds);
        _scheduler = new FrameScheduler(_clock);
        // 与物理共用同一个时钟：M3.1 的虚拟时钟测试里淡入进度也是可控的
        _lookFade = lookFade ?? new LookFade(clock: _clock);
        _autoQuality = autoQuality ?? new AutoQualityController();
    }

    /// <summary>
    /// M5.2：开始一次交叉淡入。必须在改设置**之前**调用，让当前外观成为淡入起点。
    /// </summary>
    public void BeginLookFade() => _lookFade.Begin(CurrentLook());

    /// <summary>
    /// fixes-v2 问题 5：逐帧输出「鼠标 / 窗口 / 黑洞中心 / 抓屏耗时」，用于定位抖动。
    /// 默认关闭，用 <c>--jitter-log</c> 启动才会打。
    /// </summary>
    public bool JitterLogEnabled { get; set; }

    public bool IsRunning => _running;

    /// <summary>
    /// v2 外观参数。每帧由 <see cref="SettingsModel"/> 重新映射（M2.1），
    /// 因此改设置后下一帧即生效；需要直接注入外观时也可覆盖（测试用）。
    /// </summary>
    public BlackHoleLook Look { get; set; } = BlackHoleLook.Default;

    /// <summary>置为 true 后不再每帧从设置重新映射，便于测试注入固定外观。</summary>
    public bool UseExplicitLook { get; set; }

    /// <summary>
    /// 本帧**实际**使用的外观（每帧由设置重新映射 + 自动档 + 交叉淡入之后的结果）。
    ///
    /// ⚠️ 这个只读属性是为**防回归**加的：P1.1 删掉 <c>ComputeV2Radius</c> 的调用点时，
    /// 曾把它内部的 <c>_look = CurrentLook()</c> 一起弄丢 —— 表现是黑洞永远用默认外观，
    /// 而窗口几何类断言一条都不会失败（它根本不影响窗口矩形）。
    /// 有个可观测点，这类「副作用被顺手删掉」的事故才能被测出来。
    /// </summary>
    public BlackHoleLook EffectiveLook => _look;

    /// <summary>
    /// 本帧使用的显示器高度（像素）。0 = 还没取到（同样是为了防上面那类回归）。
    /// </summary>
    public double EffectiveScreenHeight => _screenHeight;

    private BlackHoleLook CurrentLook()
    {
        BlackHoleLook target = UseExplicitLook ? Look : LookPresets.FromSettings(_settings);

        // M6.1：自动档的步数由实测帧率决定。测试注入固定外观时不掺和，避免断言被它改掉。
        if (!UseExplicitLook)
        {
            target = _autoQuality.Apply(target, _settings);
        }

        return _lookFade.Blend(target);
    }

    /// <summary>光标所在显示器的高度（像素），用于把 <c>HoleSize</c> 换算成像素。</summary>
    private double _screenHeight;

    /// <summary>本帧实际使用的外观（每帧由设置重新映射）。</summary>
    private BlackHoleLook _look = BlackHoleLook.Default;

    public double FramesPerSecond => _framesPerSecond;

    /// <summary>
    /// 物理已推进的累计时间（秒）。只在真正渲染的帧累加（<c>step</c>），
    /// 因此它就是「黑洞实际走了多久」。M3.1 用它断言
    /// 「推进量 == 真实经过的时间」，与帧率、与被节流跳过多少帧都无关。
    /// </summary>
    public double ElapsedSeconds => _elapsedSeconds;

    public bool IsDegraded => _capture?.IsDegraded ?? false;

    public bool IsUsingSoftwareRendering => _device?.UsesWarp ?? false;

    public bool IsDeviceLost => _deviceLost;

    /// <summary>抓屏连续失败次数（用于主界面提示）。</summary>
    public int CaptureFailureCount => _captureFailures;

    /// <summary>已渲染帧数（供测试断言）。</summary>
    public int RenderCount => _renderCount;

    /// <summary>因设备丢失而重建渲染资源的次数（用于主界面提示）。</summary>
    public int DeviceRebuildCount => _deviceRebuilds;

    /// <summary>抓屏结果为全黑（受保护内容），扭曲层已被临时跳过。</summary>
    public bool IsLensSuppressed => _blackFrames.IsBlack;

    /// <summary>成功完成的全黑帧采样次数（供测试断言 GPU 回读链路可用）。</summary>
    public int BlackFrameSampleCount { get; private set; }

    /// <summary>黑洞当前的视觉位置（可能滞后于真实鼠标）。</summary>
    public (double X, double Y) VisualPosition => (_follow.X, _follow.Y);

    /// <summary>N4：出现 / 消失动画的当前进度（0 = 已消失，1 = 完全显示），供测试与诊断。</summary>
    public double TransitionProgress => _transition.Progress;

    /// <summary>N4：动画是否已播完（消失动画播完 = 可以收起窗口）。</summary>
    public bool IsTransitionFinished => _transition.IsFinished;

    /// <summary>问题 1：分段帧耗时（抓屏 / 渲染 / Present / 整帧），供主界面显示与日志。</summary>
    public FrameTiming Timing => _timing;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _device = RenderDevice.Create(_preferHardware);
        _sceneRenderer = new SceneRenderer(_device.Device);

        // plan-wgc W3：默认抓屏链改为「**WGC 为主 + GDI BitBlt 兜底**」。
        //
        // 原来 primary 是 DxgiDesktopCapture（Desktop Duplication）；换掉的理由见
        // `fixes-v3` 问题 4：GDI 每次要付一次 DWM 合成同步等待（~17ms），
        // 折射区画出来的是旧桌面，与旁边实时桌面并排就是「慢半拍」。
        // WGC 是回调驱动 + GPU 零拷贝，能真正压掉这个延迟。
        //
        // 兜底仍是 GDI：老系统 / 无桌面会话 / 受保护内容下 WGC 会激活失败，
        // 那时 `IsDegraded` 为 true，由 CaptureCoordinator 切过来（程序不崩）。
        _capture = _captureFactory is not null
            ? _captureFactory(_device.Device)
            : new CaptureCoordinator(new WgcDesktopCapture(_device.Device), new GdiDesktopCapture());

        _running = true;
        _stopwatch.Restart();
        _lastFrameSeconds = _clock();
        _pendingSeconds = 0;
        _lastTopmostSeconds = double.NegativeInfinity;
        _framesPerSecond = 0;
        _consecutivePresentFailures = 0;
        _blackFrames.Reset();
        _lastMouse = _mouse.GetPosition();
        _follow.SnapTo(_lastMouse.X, _lastMouse.Y);
        _wander.Reset();
        _scheduler.Reset();
        _timing.Reset();

        // N4：启动首帧也播一次「出现」
        _lastEnabled = _settings.Enabled;
        _transition.Reset();
        if (_settings.Enabled)
        {
            _transition.Begin(TransitionDirection.Appearing, TransitionParameters.FromSettings(_settings));
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _window.SetVisible(false);

        ReleaseRenderTarget();

        _compositionVisual?.Dispose();
        _compositionVisual = null;
        _compositionTarget?.Dispose();
        _compositionTarget = null;
        _compositionDevice?.Dispose();
        _compositionDevice = null;

        _swapChain?.Dispose();
        _swapChain = null;

        _captureView?.Dispose();
        _captureView = null;
        _captureTexture?.Dispose();
        _captureTexture = null;
        _sampleTexture?.Dispose();
        _sampleTexture = null;
        _background?.Dispose();
        _background = null;

        _capture?.Dispose();
        _capture = null;

        _sceneRenderer?.Dispose();
        _sceneRenderer = null;
        _device?.Dispose();
        _device = null;
    }

    public void NotifySettingsChanged()
    {
        // 影响半径/缩放变化后窗口尺寸会变，渲染资源在下一帧自动重建
        _scheduler.Reset();
    }

    public void SetUiActive(bool active) => _uiActive = active;

    /// <summary>N4：主界面「重播一次」用。黑洞关闭中时先切回显示状态再播。</summary>
    public void ReplayTransition()
    {
        if (!_settings.Enabled)
        {
            _settings.Enabled = true;
            _lastEnabled = true;
        }

        _transition.Replay(TransitionParameters.FromSettings(_settings));
    }

    /// <summary>渲染一帧。由内部循环或测试驱动。</summary>
    public void Tick()
    {
        if (!_running || _device is null || _sceneRenderer is null)
        {
            return;
        }

        double now = _clock();
        double delta = Math.Clamp(now - _lastFrameSeconds, 0.0, FollowState.MaxTimeStep);
        _lastFrameSeconds = now;

        // 关键：把时间「攒」起来，只在真正渲染的那一帧消费。
        // 旧写法在每一帧都推进 _lastFrameSeconds，而跟随物理只在渲染帧推进，
        // 于是被节流跳过的那些帧的时间被白白丢掉 —— 渲染线程跑得越快（Tick 频率越高），
        // 丢得越多，黑洞就「走得越慢」。帧率从 32 提到 57 之后这个 bug 才暴露出来。
        _pendingSeconds += delta;

        long frameStart = _timingWatch.ElapsedTicks;

        var (mouseX, mouseY) = _mouse.GetPosition();
        bool moving = mouseX != _lastMouse.X || mouseY != _lastMouse.Y;
        _lastMouse = (mouseX, mouseY);

        // N4：总开关翻转时播一次出现 / 消失动画。
        // 消失动画播完之前不能收起窗口，否则动画根本看不见。
        var transitionParameters = TransitionParameters.FromSettings(_settings);
        if (_settings.Enabled != _lastEnabled)
        {
            _lastEnabled = _settings.Enabled;
            _transition.Begin(
                _settings.Enabled ? TransitionDirection.Appearing : TransitionDirection.Disappearing,
                transitionParameters);
        }

        if (!_settings.Enabled && _transition.IsFinished)
        {
            // 动画播完（或压根没开动画）→ 立刻收起。托盘退出走的是 Stop()，不经过这里。
            _window.SetVisible(false);
            _follow.SnapTo(mouseX, mouseY);
            // 黑洞关闭期间不累积「静止时长」，重新启用时从鼠标位置开始
            _wander.Reset();
            _framesPerSecond = 0;
            return;
        }

        // 播动画期间必须保持满帧，否则缩放会一顿一顿（这里用的是上一帧的游荡状态，差一帧无妨）
        if (!_scheduler.ShouldRender(true, moving, _settings, _uiActive || _transition.IsActive, _wander.IsActive))
        {
            return;
        }

        // 一次性消费攒下来的时间；超过单帧上限的部分丢弃，防止卡顿后瞬移
        double step = Math.Clamp(_pendingSeconds, 0.0, FollowState.MaxTimeStep);
        _pendingSeconds = 0.0;

        _transition.Step(step, transitionParameters);

        // M5.2：预设切换的 1 秒交叉淡入按真实时间推进（与渲染节流无关）
        _lookFade.Step();

        // v3（P1.5）：刷新屏高与本帧外观（原先挂在 ComputeV2Radius 里，见方法注释）
        UpdateLookAndMetrics();

        // N1 静止游荡：先算出叠加在鼠标位置上的偏移（命中点永远是真实鼠标）
        _wander.Step(moving, step, WanderParameters.FromSettings(_settings));
        var (wanderX, wanderY) = _wander.Offset;

        _elapsedSeconds += step;

        // 与惯性跟随叠加时，黑洞追的是「游荡后的目标点」，手感是「被拖着飘」
        _follow.Step(mouseX + wanderX, mouseY + wanderY, step, FollowParameters.FromSettings(_settings));

        // v3（P1.1）：窗口 = 光标所在显示器的**整块矩形**，永不跟随光标移动。
        //
        // 黑洞位置改由着色器 uniform 决定（P1.2），于是 Win32 的**整数像素约束不再作用到
        // 黑洞定位上** —— 这正是 fixes-v2 问题 7 抖动的根因：
        // 小窗口架构下「位置精确」与「折射图案稳定」只能二选一，全屏 + uniform 可以同时满足。
        // 参考实现 singularity 同样是「每个显示器一个手搓全屏窗」。
        var monitor = ScreenMetrics.MonitorRectAt((int)_follow.X, (int)_follow.Y);
        var rect = new OverlayGeometry.Rect(
            monitor.Left,
            monitor.Top,
            Math.Max(monitor.Right - monitor.Left, 1),
            Math.Max(monitor.Bottom - monitor.Top, 1));

        if (rect != _lastWindowRect)
        {
            _window.Move(rect.Left, rect.Top, rect.Width, rect.Height);
            _lastWindowRect = rect;
        }

        _window.SetVisible(true);

        // 周期性重申置顶：explorer/任务栏重建或别的窗口抢占后，覆盖层会被压到下面
        if (_elapsedSeconds - _lastTopmostSeconds >= TopmostReassertSeconds)
        {
            _lastTopmostSeconds = _elapsedSeconds;
            _window.EnsureTopmost();
        }

        EnsureRenderTarget(rect.Width);

        // ---- 问题 1 埋点：分段计时 ----
        long captureStart = _timingWatch.ElapsedTicks;

        bool useBackground = false;
        if (_settings.LensEnabled && _capture is not null)
        {
            useBackground = TryRefreshBackground(rect);

            if (useBackground)
            {
                // 采样点仍要对着黑洞：抓屏纹理现在是整块显示器，
                // 所以采样原点由 TryRefreshBackground 按「窗口 − 显示器原点」算好
                UpdateBlackFrameState(
                    _sampleOriginX,
                    _sampleOriginY,
                    Math.Min(BlackFrameSampleSize, Math.Min(rect.Width, rect.Height)));

                // 抓到全黑帧（受保护内容 / HDR）时跳过扭曲，只画黑洞，避免出现死黑矩形
                useBackground = !_blackFrames.IsBlack;
            }
        }

        long captureEnd = _timingWatch.ElapsedTicks;
        double captureMs = TicksToMilliseconds(captureEnd - captureStart);

        // 本帧真的抓了才更新耗时（跳过抓屏的帧是 0~0.1ms，会把它拉低到错误量级）
        if (captureMs > 0.1)
        {
            _lastCaptureCostMs = captureMs;
        }

        _timing.AddCapture(captureMs);

        var (centerX, centerY) = OverlayGeometry.LocalCenter(_follow.X, _follow.Y, rect);

        // fixes-v2 问题 5：抖动取证。盯住三件事 ——
        //   1) left 是否随鼠标**单调**推进（量化会让它以 2px 跳，这本身没问题）
        //   2) center（黑洞在窗口内的位置）是否在两个值之间来回跳 → 相对窗口的抖动
        //   3) 抓屏耗时的方差（几十毫秒的尖峰意味着抓到的桌面与当前帧不同步）
        if (JitterLogEnabled)
        {
            RuntimeLog.Info(
                $"JITTER mouse={mouseX},{mouseY} follow={_follow.X:F2},{_follow.Y:F2} " +
                $"left={rect.Left} top={rect.Top} extent={rect.Width} " +
                $"center={centerX:F2},{centerY:F2} " +
                $"captureMs={captureMs:F2} mapOffset={_captureMap.Z:F5},{_captureMap.W:F5}");
        }
        var description = new SceneDescription(
            rect.Width,
            rect.Height,
            centerX,
            centerY,
            _settings,
            _elapsedSeconds,
            useBackground,
            _transition.Scale(transitionParameters),
            _transition.Fade,
            Look: _look,
            ScreenHeight: _screenHeight,
            CaptureMap: _captureMap,

            // fixes-v2 问题 8：把黑洞自己（跟随后的位置）作为视差来源。
            // 用 _follow 而不是裸鼠标：惯性跟随 / 静止游荡都该带着星空星云一起动。
            ParallaxX: _follow.X,
            ParallaxY: _follow.Y,

            // W7：`useBackground` 已经代表「抓屏纹理有效且不是全黑帧」（受保护内容 / HDR 时
            // 上面的 BlackFrameDetector 会把它置 false）。此时整屏不透明 ⇒ 内容年龄一致
            // ⇒ 折射区不再比周围慢一拍；抓屏不可用则退回透明，绝不整屏黑。
            DesktopReady: useBackground ? 1.0 : 0.0);

        long renderStart = _timingWatch.ElapsedTicks;

        var constants = SceneParamsBuilder.Build(description);
        _sceneRenderer.Render(
            _device.Context,
            _renderTargetView!,
            useBackground ? _captureView : null,
            constants,
            rect.Width,
            rect.Height);

        long renderEnd = _timingWatch.ElapsedTicks;
        _timing.AddRender(TicksToMilliseconds(renderEnd - renderStart));

        // Vortice 的 Present 返回 Result 而不抛异常，必须显式检查返回值
        long presentStart = _timingWatch.ElapsedTicks;

        Result presentResult;
        try
        {
            presentResult = _swapChain!.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Present 抛出异常", ex);
            HandlePresentFailure(unchecked((int)0x80004005));
            return;
        }

        if (presentResult.Failure)
        {
            HandlePresentFailure(presentResult.Code);
            return;
        }

        _consecutivePresentFailures = 0;

        _timing.AddPresent(TicksToMilliseconds(_timingWatch.ElapsedTicks - presentStart));

        _capture?.ReleaseFrame();

        _renderCount++;
        if (_renderCount == 1)
        {
            // 首帧取证：窗口尺寸、交换链类型、阴影与盘的像素半径是否匹配预期
            double monitorHeight = ScreenMetrics.MonitorHeightAt(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
            double shadow = LensMath.ShadowRadiusPixels(
                LookPresets.FromSettings(_settings).HoleSize, monitorHeight);
            RuntimeLog.Info(
                $"首帧渲染完成：窗口 {rect.Width}x{rect.Height} @({rect.Left},{rect.Top})，交换链 {ActiveSwapEffect}，"
                + $"窗口半边长 {rect.Width / 2.0:F1}，阴影半径 {shadow:F1} px");
        }
        if (delta > 0)
        {
            double instant = 1.0 / delta;
            _framesPerSecond = _framesPerSecond <= 0 ? instant : (_framesPerSecond * 0.9) + (instant * 0.1);
        }

        _timing.AddFrame(TicksToMilliseconds(_timingWatch.ElapsedTicks - frameStart));

        // 问题 1：周期性写一条分段耗时日志，用真实数据定位瓶颈，而不是靠猜
        if (_timing.TryTakeLog(out double averageFps))
        {
            // M6.1：自动档按**这个窗口的平均值**升降步数（只在 QualitySteps.Auto 下影响画面）。
            // 不能用逐帧瞬时值：实测启动时头几秒能冲到 117 FPS（抓屏还没就绪），
            // 而同一时段的 5 秒平均只有 41 FPS —— 用瞬时值会在最卡的时候反而升到最高档。
            if (_autoQuality.Update(averageFps, FrameTiming.LogIntervalSeconds) && !UseExplicitLook)
            {
                RuntimeLog.Info(
                    $"自动质量档位 → {_autoQuality.StepCount} 步"
                    + $"（近 {FrameTiming.LogIntervalSeconds:F0}s 平均 {averageFps:F0} FPS）");
            }

            RuntimeLog.Info(
                $"帧耗时（近 {FrameTiming.LogIntervalSeconds:F0}s 平均 {averageFps:F1} FPS，窗口 {rect.Width}x{rect.Height}）："
                + $"抓屏 {_timing.CaptureMs:F2} / 渲染 {_timing.RenderMs:F2} / Present {_timing.PresentMs:F2} / 整帧 {_timing.FrameMs:F2} ms，"
                + $"扭曲={(useBackground ? "开" : "关")}，抓屏降级={IsDegraded}，全黑={_blackFrames.IsBlack}");
        }
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// v3（P1.5）：每帧刷新「显示器高度」与「本帧外观」。
    ///
    /// 这两件事原先挂在 <c>ComputeV2Radius</c> 里（那个方法顺带把窗口半径算出来）。
    /// 窗口改成整块显示器后半径不再需要，但**这两个副作用绝不能丢**：
    /// <c>_screenHeight</c> 决定 <c>HoleSize</c> 怎么换算成像素，<c>_look</c> 是本帧真正渲染的外观。
    ///
    /// ⚠️ P1.1 删掉 <c>ComputeV2Radius</c> 的调用点时曾把它们一起弄丢 ——
    /// 表现是黑洞永远用默认外观、且屏高按 0 算。窗口几何类断言抓不到这个，
    /// 因为它根本不影响窗口矩形。这里是修复。
    /// </summary>
    private void UpdateLookAndMetrics()
    {
        _screenHeight = ScreenMetrics.MonitorHeightAt(_lastMouse.X, _lastMouse.Y);
        _look = CurrentLook();
    }

    /// <summary>自适应抓屏间隔：抓屏越贵隔得越久（详见常量处的说明）。</summary>
    private double CaptureIntervalSeconds() => CaptureRefreshInterval(_lastCaptureCostMs);

    /// <summary>
    /// 抓屏间隔策略（纯函数，便于单测）：耗时 × 系数，再钳制到
    /// [<see cref="CaptureRefreshMinSeconds"/>, <see cref="CaptureRefreshMaxSeconds"/>]。
    /// </summary>
    public static double CaptureRefreshInterval(double lastCaptureCostMs)
        => Math.Clamp(
            (lastCaptureCostMs / 1000.0) * CaptureRefreshCostFactor,
            CaptureRefreshMinSeconds,
            CaptureRefreshMaxSeconds);

    private bool TryRefreshBackground(OverlayGeometry.Rect rect)
    {
        if (_capture is null || _device is null)
        {
            return false;
        }

        // fixes-v2 问题 5：抓**整块显示器**，区域只由光标所在的显示器决定，
        // 与黑洞 / 窗口位置完全无关。
        // 旧实现抓的是「窗口那一块」，于是窗口移到 P(t)、抓屏还是 P(t−1) 的内容，
        // 背景相对黑洞每帧滑一个 Δ —— 鼠标动得越快抖得越狠。
        // 参考实现 singularity 正是靠「整屏固定抓取 + 窗口不动」消灭抖动的。
        int cx = rect.Left + (rect.Width / 2);
        int cy = rect.Top + (rect.Height / 2);
        var monitor = ScreenMetrics.MonitorRectAt(cx, cy);
        int monitorWidth = Math.Max(monitor.Right - monitor.Left, 1);
        int monitorHeight = Math.Max(monitor.Bottom - monitor.Top, 1);

        bool hadTexture = _captureTexture is not null
            && _captureWidth == monitorWidth
            && _captureHeight == monitorHeight;

        if (!hadTexture)
        {
            _captureTexture?.Dispose();
            _captureView?.Dispose();
            _captureTexture = CreateCaptureTexture(monitorWidth, monitorHeight);
            _captureView = _device.Device.CreateShaderResourceView(_captureTexture, null);
            _captureWidth = monitorWidth;
            _captureHeight = monitorHeight;
        }

        // v3（P2）：**确认走 GDI 就立刻交给后台线程**，绝不让主线程做整屏同步抓屏。
        //
        // 原来这个调用放在「第一次同步抓屏成功之后」，于是主线程必须先自己抓一帧整屏 ——
        // 1920×1080 的 GDI 抓取在内存紧张时会明显变慢甚至拖死首帧，实测连一帧都出不来
        // （日志里「覆盖层已启动」之后就再无输出）。
        // 而 `IsDegraded` 在初始化失败时就已经为真，不必等一次 TryCapture 才知道。
        if (_background is null && _capture.IsDegraded)
        {
            StartBackgroundCapture();
        }

        // fixes-v2 问题 5（第二刀）：整屏抓屏**不必每帧都做**。
        // 纹理锚在显示器上，黑洞/窗口怎么动都不影响它的有效性 —— 只有**桌面内容变了**才需要重抓。
        // 实测每帧整屏 GDI 抓屏要 33~68ms，占整帧 ~95%，把帧率压到约 20FPS：
        // 黑洞只能以 20Hz 更新，鼠标一动就一格一格地跳，看起来就是「左右摇摆」。
        // 按固定间隔节流后，渲染帧率不再被抓屏拖住，而桌面内容仍以 30Hz 刷新。
        // fixes-v2 问题 7：后台抓屏不需要节流 ——
        // 它不在主循环里，抓多快都不影响帧时间；反倒是节流会让桌面内容变旧。
        if (_background is null)
        {
            double now = _clock();
            if (hadTexture && now - _lastCaptureSeconds < CaptureIntervalSeconds())
            {
                return true;                   // 复用上一张纹理，内容依然对得上
            }

            _lastCaptureSeconds = now;
            _capture.SelectOutput(cx, cy);
        }

        // 走到这里必然已经建好纹理（上面 !hadTexture 分支负责建），
        // 显式判空是为了让可空性分析通过，而不是运行时兜底
        if (_captureTexture is null)
        {
            return false;
        }

        // v3（P2）：窗口 == 整块显示器（P1.1），所以抓取区域**就是显示器本身**。
        //
        // 原来那套「窗口 ± margin 的局部增量更新」是为了少抓点面积
        // （fixes-v2 问题 5 第三刀：面积约 1/2.7，40ms → 15ms）。
        // 窗口变全屏后 `rect.Left == monitor.Left`，margin 会被 clamp 掉，
        // 区域恒等于整屏 —— 那段逻辑已经算不出别的结果，留着只会误导后人，
        // 而且正是它制造了问题 9 的「旧区域」竞态。这里显式去掉。
        //
        // 抓屏成本不再是问题：已由后台线程承担（实测 0.78ms）。
        int captureWidth = monitorWidth;
        int captureHeight = monitorHeight;
        int destX = 0;
        int destY = 0;

        CaptureStatus status;

        if (_background is not null)
        {
            // ---- 后台线程路径（fixes-v2 问题 7）----
            // 只更新区域 + 上传最新一帧，两者都不阻塞：
            // 上传是一次 UpdateSubresource（约 0.3ms），没有新帧时直接返回。
            _background.SetRegion(monitor.Left, monitor.Top, captureWidth, captureHeight, destX, destY);
            bool uploaded = _background.TryUpload(_device.Context, _captureTexture);
            _backgroundReady |= uploaded;
            _lastCaptureCostMs = _background.LastCostMs;

            status = _backgroundReady ? CaptureStatus.Success : CaptureStatus.Timeout;
        }
        else
        {
            status = _capture.TryCapture(
                _device.Context,
                _captureTexture,
                monitor.Left,
                monitor.Top,
                captureWidth,
                captureHeight,
                destX,
                destY);
        }

        // 抓取的是整块显示器，着色器取样前必须换算坐标系（窗口 uv → 显示器 uv）
        var map = OverlayGeometry.CaptureMap(
            rect.Left, rect.Top, rect.Width, rect.Height,
            monitor.Left, monitor.Top, monitorWidth, monitorHeight);
        _captureMap = new Vector4((float)map.ScaleX, (float)map.ScaleY, (float)map.OffsetX, (float)map.OffsetY);

        // 全黑帧检测要对着黑洞采样：抓屏纹理是整块显示器，必须减去显示器原点。
        // v3（P1.3）：窗口 v3 起就是整块显示器，按「窗口中心」算会永远落在屏幕正中 ——
        // 而黑洞现在可以在屏幕任意位置（由 uniform 决定，见 P1.2），
        // 所以采样原点必须跟着黑洞走，否则检测的是别处。
        int sampleSize = Math.Min(BlackFrameSampleSize, Math.Min(rect.Width, rect.Height));
        (_sampleOriginX, _sampleOriginY) = OverlayGeometry.BlackFrameSampleOrigin(
            _follow.X, _follow.Y, sampleSize, monitor.Left, monitor.Top);

        if (status == CaptureStatus.Timeout)
        {
            // 桌面没有新内容 → 复用上一帧纹理即可，这是常态而非失败
            return true;
        }

        if (status != CaptureStatus.Success)
        {
            _captureFailures++;
            if (_captureFailures == 1)
            {
                RuntimeLog.Info(_capture.IsDegraded
                    ? $"抓屏失败（{status}），已降级为 {_capture.ActiveName}"
                    : $"抓屏失败（{status}），当前方案 {_capture.ActiveName}");
            }

            return false;
        }

        _captureFailures = 0;
        return true;
    }

    /// <summary>
    /// fixes-v2 问题 7：启动后台 GDI 抓屏。
    /// 之后每帧只做「更新区域 + 上传最新一帧」，渲染循环不再等那 17ms 的 DWM 同步。
    /// </summary>
    private void StartBackgroundCapture()
    {
        var background = new BackgroundGdiCapture();
        background.Start();
        _background = background;
        _backgroundReady = false;
        RuntimeLog.Info("抓屏已切到后台线程（GDI 的 ~17ms 不再占用渲染循环）");
    }

    /// <summary>
    /// 低频采样抓屏纹理中**黑洞所在的那块区域**，判断是否抓到全黑帧（DRM / HDR）。
    /// 全黑状态是持续性的，因此无需每帧检测。
    ///
    /// ⚠️ 抓屏改成整块显示器后，纹理中心不再是黑洞所在处 ——
    /// 采样位置必须由「窗口矩形 − 显示器原点」换算（坐标以抓屏纹理为准）。
    /// </summary>
    private void UpdateBlackFrameState(int sampleLeft, int sampleTop, int size)
    {
        if (_device is null || _captureTexture is null)
        {
            return;
        }

        if (_renderCount % BlackFrameSampleInterval != 0)
        {
            return;
        }

        try
        {
            EnsureSampleTexture();
            if (_sampleTexture is null)
            {
                return;
            }

            if (size <= 0)
            {
                return;
            }

            int left = Math.Clamp(sampleLeft, 0, Math.Max(_captureWidth - size, 0));
            int top = Math.Clamp(sampleTop, 0, Math.Max(_captureHeight - size, 0));

            _device.Context.CopySubresourceRegion(
                _sampleTexture,
                0,
                0,
                0,
                0,
                _captureTexture,
                0,
                new Box(left, top, 0, left + size, top + size, 1));

            var mapped = _device.Context.Map(_sampleTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int stride = size * 4;
                for (int row = 0; row < size; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.DataPointer, row * (int)mapped.RowPitch),
                        _samplePixels,
                        row * stride,
                        stride);
                }
            }
            finally
            {
                _device.Context.Unmap(_sampleTexture, 0);
            }

            BlackFrameSampleCount++;

            if (_blackFrames.Update(_samplePixels.AsSpan(0, size * size * 4)))
            {
                RuntimeLog.Info(_blackFrames.IsBlack
                    ? "抓屏结果为全黑（可能在播放受保护内容），已临时跳过扭曲层"
                    : "抓屏画面恢复正常，扭曲层已恢复");
            }
        }
        catch (Exception ex)
        {
            // 检测失败只影响降级判断，绝不能影响主渲染路径
            RuntimeLog.Error("全黑帧检测失败，本次忽略", ex);
            _blackFrames.Reset();
        }
    }

    private void EnsureSampleTexture()
    {
        if (_sampleTexture is not null || _device is null)
        {
            return;
        }

        _sampleTexture = _device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = BlackFrameSampleSize,
            Height = BlackFrameSampleSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    /// <summary>按 plan.md 5.2 的降级矩阵处置 Present 失败。</summary>
    private void HandlePresentFailure(int hresult)
    {
        _consecutivePresentFailures++;
        var kind = RenderFailure.Classify(hresult);
        var action = RenderFailure.Decide(kind, _consecutivePresentFailures, _deviceRebuilds);

        switch (action)
        {
            case FailureAction.Rebuild:
                RebuildDevice(hresult);
                return;

            case FailureAction.Stop:
                _deviceLost = true;
                RuntimeLog.Error(
                    $"Present 连续失败 {_consecutivePresentFailures} 次（0x{hresult:X8}，{kind}），覆盖层停止渲染");
                Stop();
                return;

            default:
                // 只记第一次，避免每帧刷日志
                if (_consecutivePresentFailures == 1)
                {
                    RuntimeLog.Info($"Present 失败（0x{hresult:X8}，{kind}），跳过本帧");
                }

                return;
        }
    }

    /// <summary>设备丢失：重建 Device 与全部资源，最多重试 <see cref="MaxDeviceRebuilds"/> 次。</summary>
    private void RebuildDevice(int hresult)
    {
        _deviceRebuilds++;

        if (_deviceRebuilds > MaxDeviceRebuilds)
        {
            _deviceLost = true;
            RuntimeLog.Error(
                $"设备丢失已达重建上限（{MaxDeviceRebuilds} 次，0x{hresult:X8}），覆盖层停止渲染");
            Stop();
            return;
        }

        RuntimeLog.Info($"检测到设备丢失（0x{hresult:X8}），重建渲染资源（第 {_deviceRebuilds}/{MaxDeviceRebuilds} 次）");

        // Stop 会释放 Device/交换链/抓屏，Start 再全部重建；窗口本身保留
        Stop();
        Start();
    }

    private void EnsureRenderTarget(int extent)
    {
        if (_extent == extent && _swapChain is not null && _renderTargetView is not null)
        {
            return;
        }

        ReleaseRenderTarget();

        if (_swapChain is null)
        {
            _swapChain = CreateSwapChain(extent);
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)extent, (uint)extent, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device!.Device.CreateRenderTargetView(backBuffer, null);

        BindComposition();

        _extent = extent;
    }

    /// <summary>把交换链挂到 DirectComposition 视觉树上，窗口才能真正透出桌面。</summary>
    private void BindComposition()
    {
        if (_compositionDevice is null)
        {
            using var dxgiDevice = _device!.Device.QueryInterface<IDXGIDevice>();
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _compositionTarget = _compositionDevice.CreateSurfaceFromHwnd(_window.Handle, true);
            _compositionVisual = _compositionDevice.CreateVisual();
            _compositionTarget.SetRoot(_compositionVisual);
        }

        _compositionVisual!.SetContent(_swapChain!);
        _compositionDevice.Commit();
    }

    private IDXGISwapChain1 CreateSwapChain(int extent)
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        // DirectComposition 交换链支持预乘 alpha，翻转模型按兼容性依次尝试
        (SwapEffect Effect, AlphaMode Mode, uint Buffers)[] attempts =
        {
            (SwapEffect.FlipDiscard, AlphaMode.Premultiplied, 2),
            (SwapEffect.FlipSequential, AlphaMode.Premultiplied, 2),
            (SwapEffect.Discard, AlphaMode.Premultiplied, 1),
            (SwapEffect.FlipSequential, AlphaMode.Ignore, 2),
            (SwapEffect.Discard, AlphaMode.Ignore, 1),
        };

        Exception? lastError = null;
        foreach (var (effect, mode, buffers) in attempts)
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)extent,
                Height = (uint)extent,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = buffers,
                Scaling = Scaling.Stretch,
                SwapEffect = effect,
                AlphaMode = mode,
                Flags = SwapChainFlags.None,
            };

            try
            {
                var chain = factory.CreateSwapChainForComposition(_device!.Device, description, null);
                _swapEffect = effect;
                _alphaMode = mode;
                return chain;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("无法为覆盖层创建交换链", lastError);
    }

    private SwapEffect _swapEffect;
    private AlphaMode _alphaMode;

    /// <summary>实际生效的交换效果（诊断用）。</summary>
    public string ActiveSwapEffect => $"{_swapEffect}/{_alphaMode}";

    private ID3D11Texture2D CreateCaptureTexture(int width, int height)
        => _device!.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    private void ReleaseRenderTarget()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
    }
}
