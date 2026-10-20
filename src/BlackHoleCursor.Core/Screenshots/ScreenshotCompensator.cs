using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Core.Screenshots;

/// <summary>
/// M4（prd-v2 F8）：截图补偿的编排层 —— 判定 → 合成 → 写回。
///
/// 之所以需要「补偿」：F5-3 把覆盖层排除出了捕获（`WDA_EXCLUDEFROMCAPTURE`，否则会镜像回廊），
/// 于是用户按 PrtScn 得到的全屏图里**没有黑洞**。这里检测到剪贴板出现全屏位图后，
/// 用同一套渲染器把黑洞合成回去，再写回剪贴板让用户粘贴。
///
/// 只处理剪贴板里的**全屏位图**；区域截图与保存到磁盘的文件不处理（F8-2）。
/// </summary>
public sealed class ScreenshotCompensator : IDisposable
{
    private readonly SettingsModel _settings;
    private readonly IClipboardImages _clipboard;
    private readonly Func<(double X, double Y)> _holePosition;
    private readonly Func<double> _timeSeconds;
    private readonly bool _preferHardware;

    private ScreenshotCompositor? _compositor;
    private uint _lastWrittenSequence;
    private bool _disposed;
    private bool _skipReasonLogged;

    /// <param name="holePosition">黑洞当前的**视觉**位置（虚拟桌面坐标，像素）。</param>
    /// <param name="timeSeconds">覆盖层的动画时间，让补出来的盘纹与桌面上一致。</param>
    /// <param name="preferHardware">false 走 WARP（测试与无显卡环境）。</param>
    public ScreenshotCompensator(
        SettingsModel settings,
        IClipboardImages clipboard,
        Func<(double X, double Y)> holePosition,
        Func<double> timeSeconds,
        bool preferHardware = true)
    {
        _settings = settings;
        _clipboard = clipboard;
        _holePosition = holePosition;
        _timeSeconds = timeSeconds;
        _preferHardware = preferHardware;
    }

    /// <summary>是否启用（设置项，默认开启）。</summary>
    public bool Enabled => _settings.ScreenshotCompensation;

    /// <summary>已完成的补偿次数（供测试与日志）。</summary>
    public int CompensatedCount { get; private set; }

    /// <summary>
    /// 剪贴板发生变化时的处理入口。返回本次是否做了补偿。
    /// 任何一步不满足条件都应当安静地什么都不做 —— 剪贴板是用户的数据，出错代价很高。
    /// </summary>
    public bool HandleClipboardUpdate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 写回剪贴板会再触发一次 WM_CLIPBOARDUPDATE。
        // 不认出「这是自己刚写进去的」就会无限自我补偿。
        if (_clipboard.SequenceNumber == _lastWrittenSequence)
        {
            return false;
        }

        if (!Enabled)
        {
            return false;
        }

        if (!_clipboard.TryGetBitmap(out var bitmap) || !bitmap.HasCompletePixels)
        {
            LogSkipOnce("剪贴板里没有可取回的位图");
            return false;
        }

        var (virtualWidth, virtualHeight) = ScreenMetrics.VirtualScreenSize();
        if (!ScreenshotPolicy.ShouldCompensate(true, bitmap.Width, bitmap.Height, virtualWidth, virtualHeight))
        {
            LogSkipOnce(
                $"位图 {bitmap.Width}x{bitmap.Height} 与虚拟桌面 {virtualWidth}x{virtualHeight} 不一致，按 F8-2 不补偿");
            return false;
        }

        _compositor ??= new ScreenshotCompositor(_preferHardware);

        var (holeX, holeY) = _holePosition();
        int screenHeight = ScreenMetrics.MonitorHeightAt((int)holeX, (int)holeY);
        if (screenHeight <= 0)
        {
            screenHeight = virtualHeight;
        }

        if (!_compositor.Composite(
                bitmap.Pixels,
                bitmap.Width,
                bitmap.Height,
                (int)holeX,
                (int)holeY,
                _settings,
                screenHeight,
                _timeSeconds()))
        {
            return false;
        }

        _clipboard.SetBitmap(bitmap);

        // 必须在**写回之后**再取序列号：写操作本身会把它推到新值
        _lastWrittenSequence = _clipboard.SequenceNumber;
        CompensatedCount++;
        RuntimeLog.Info($"截图补偿：已把黑洞合成回剪贴板（{bitmap.Width}x{bitmap.Height} @({holeX:F0},{holeY:F0})）");
        return true;
    }

    /// <summary>
    /// M6.3：跳过的原因只记一次。剪贴板每次复制都会走到这里，
    /// 无条件记录会把日志刷满（用户复制个文本也会进来一次）。
    /// </summary>
    private void LogSkipOnce(string reason)
    {
        if (_skipReasonLogged)
        {
            return;
        }

        _skipReasonLogged = true;
        RuntimeLog.Info($"截图补偿：本次未补偿（{reason}）");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compositor?.Dispose();
        _compositor = null;
        GC.SuppressFinalize(this);
    }
}
