using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.UI;

/// <summary>
/// 主界面实时预览：使用与桌面覆盖层完全相同的渲染器与参数，保证所调即所见。
/// 离屏渲染后回读到 WriteableBitmap，避免 D3D9/D3D11 互操作复杂度。
/// </summary>
public sealed class PreviewSurface : IDisposable
{
    private readonly SettingsModel _settings;
    private readonly DispatcherTimer _timer;
    private readonly int _size;
    private readonly LookFade _lookFade;
    private readonly AutoQualityController _autoQuality;

    private RenderDevice? _device;
    private SceneRenderer? _sceneRenderer;
    private PreviewRenderer? _previewRenderer;
    private ID3D11ShaderResourceView? _backgroundView;
    private WriteableBitmap? _bitmap;
    private double _time;
    private DateTime _lastFrame = DateTime.UtcNow;
    private bool _disposed;

    /// <param name="lookFade">
    /// M5.2：与桌面覆盖层共用同一个淡入实例，预览才能跟着一起淡入（不传则预览独立，不会淡入）。
    /// </param>
    /// <param name="autoQuality">
    /// M6.1：与覆盖层共用同一个自动档调节器，否则预览会用旧步数渲染，与桌面不一致。
    /// </param>
    public PreviewSurface(
        SettingsModel settings,
        int size = 184,
        LookFade? lookFade = null,
        AutoQualityController? autoQuality = null)
    {
        _settings = settings;
        _size = size;
        _lookFade = lookFade ?? new LookFade();
        _autoQuality = autoQuality ?? new AutoQualityController();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick, Dispatcher.CurrentDispatcher);
    }

    public ImageSource? Source => _bitmap;

    public bool IsAvailable => _bitmap is not null;

    public string? FailureReason { get; private set; }

    /// <summary>
    /// fixes-v2 问题 3（续）：当前预览的缩小倍率（1 = 真实比例）。
    /// &lt; 1 表示盘装不下、已整体缩小，UI 会在预览框角落标注出来。
    /// </summary>
    public double FitScale { get; private set; } = 1.0;

    public void Start()
    {
        try
        {
            _device = RenderDevice.Create(preferHardware: true);
            _sceneRenderer = new SceneRenderer(_device.Device);
            _previewRenderer = new PreviewRenderer(_device, _sceneRenderer, _size, _size);
            _backgroundView = PreviewBackground.CreateView(_device.Device, _size, _size);
            _previewRenderer.SetBackground(_backgroundView);

            _bitmap = new WriteableBitmap(_size, _size, 96, 96, PixelFormats.Bgra32, null);
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            return;
        }

        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_previewRenderer is null || _bitmap is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        double delta = Math.Clamp((now - _lastFrame).TotalSeconds, 0.0, 0.2);
        _lastFrame = now;
        _time += delta;

        // M5.2：与覆盖层共用同一份淡入状态（只按时钟判到期，两边各调一次不会加速）
        _lookFade.Step();

        // 预览里让黑洞缓慢巡游，方便观察发光与遮挡。
        // 幅度调小：缩略图下整个黑洞（含远场）都塞在框里，漂移太大容易被框裁掉。
        double t = _time * 0.6;
        double amplitude = _size * 0.12;
        double centerX = _size / 2.0 + Math.Sin(t) * amplitude;
        double centerY = _size / 2.0 + Math.Cos(t * 0.8) * amplitude * 0.6;

        // 与实机共用同一渲染器与同一套映射，保证「预览 = 实机」。
        // 屏高必须与覆盖层同口径（Win32 物理像素）：HoleSize 是「占屏高的比例」，
        // 两边口径不一致时预览会与桌面上的实际大小对不上（M2.4）。
        double screenHeight = ScreenMetrics.PrimaryMonitorHeight();
        if (screenHeight <= 0)
        {
            screenHeight = SystemParameters.PrimaryScreenHeight;
        }

        // M6.1：自动档的步数也必须与覆盖层一致（同一个调节器实例）
        var look = _lookFade.Blend(_autoQuality.Apply(LookPresets.FromSettings(_settings), _settings));

        // fixes-v2 问题 3（续）：预览取景改为**混合** ——
        // 默认按真实比例（框代表屏高的一半，于是洞越大框里越大、有大小反馈），
        // 盘的外圈装不下时才整体缩小到刚好装下，倍率交给 UI 在角落标注。
        // 仍然共用同一渲染器与同一套外观；屏高口径改成预览框自身
        //（HoleSize 已按预览视口换算过，不能再乘真实屏高）。
        var (previewHole, fitScale) = OverlayGeometry.PreviewFit(look, _size);

        FitScale = fitScale;

        var thumbnail = look.Clone();
        thumbnail.HoleSize = previewHole;

        var description = new SceneDescription(
            _size,
            _size,
            centerX,
            centerY,
            _settings,
            _time,
            _settings.LensEnabled,
            Look: thumbnail,
            ScreenHeight: _size);

        byte[] pixels = _previewRenderer.Render(description);
        _bitmap.Lock();
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, _bitmap.BackBuffer, pixels.Length);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _size, _size));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _backgroundView?.Dispose();
        _previewRenderer?.Dispose();
        _sceneRenderer?.Dispose();
        _device?.Dispose();
        GC.SuppressFinalize(this);
    }
}
