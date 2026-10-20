using Vortice.Direct3D11;

namespace BlackHoleCursor.Core.Capture;

public enum CaptureStatus
{
    /// <summary>抓到新帧（帧仍被持有，需调用 ReleaseFrame）。</summary>
    Success,

    /// <summary>桌面暂无新帧，应复用上一帧。</summary>
    Timeout,

    /// <summary>访问丢失（分辨率/显卡切换），需要重建。</summary>
    AccessLost,

    /// <summary>抓屏不可用（如受保护内容、无会话）。</summary>
    Unavailable,
}

/// <summary>桌面抓取抽象。DXGI 为主、GDI 为备，便于测试与降级。</summary>
public interface IDesktopCapture : IDisposable
{
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// 抓取指定区域到目标纹理。
    /// <paramref name="destX"/> / <paramref name="destY"/> 为目标纹理内的落点（默认 0,0）：
    /// fixes-v2 问题 5 用它做**局部增量更新** —— 纹理仍是整块显示器大小（位置无关，不会抖），
    /// 但每帧只把窗口那一块抓进去，面积小得多，可以每帧都抓而不用节流。
    /// </summary>
    CaptureStatus TryCapture(
        ID3D11DeviceContext context,
        ID3D11Texture2D destination,
        int left,
        int top,
        int width,
        int height,
        int destX = 0,
        int destY = 0);

    /// <summary>释放当前持有的帧。</summary>
    void ReleaseFrame();

    /// <summary>重建内部资源（分辨率、显卡切换后）。</summary>
    void Recreate();

    /// <summary>选中包含指定坐标的显示器（多显示器场景；GDI 实现为空操作）。</summary>
    void SelectOutput(int centerX, int centerY);
}

/// <summary>
/// 抓屏协调器：负责重试、重建与降级。
/// 这是「抓屏失败时程序不能崩、要有明确行为」的集中处理点。
/// </summary>
public sealed class CaptureCoordinator : IDisposable
{
    private const int MaxRecreateAttempts = 3;

    private readonly IDesktopCapture _primary;
    private readonly IDesktopCapture _fallback;
    private IDesktopCapture _active;
    private int _recreateAttempts;

    public CaptureCoordinator(IDesktopCapture primary, IDesktopCapture fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _active = primary;
    }

    public bool IsDegraded => !ReferenceEquals(_active, _primary);

    public string ActiveName => _active.Name;

    public CaptureStatus LastStatus { get; private set; } = CaptureStatus.Unavailable;

    public int FailureCount { get; private set; }

    public int RecreateCount { get; private set; }

    public void SelectOutput(int centerX, int centerY) => _active.SelectOutput(centerX, centerY);

    public CaptureStatus TryCapture(
        ID3D11DeviceContext context,
        ID3D11Texture2D destination,
        int left,
        int top,
        int width,
        int height,
        int destX = 0,
        int destY = 0)
    {
        var status = _active.TryCapture(context, destination, left, top, width, height, destX, destY);
        LastStatus = status;

        switch (status)
        {
            case CaptureStatus.Success:
                FailureCount = 0;
                _recreateAttempts = 0;
                return status;

            case CaptureStatus.Timeout:
                // 桌面没有新内容，复用上一帧即可，不算失败
                return status;

            case CaptureStatus.AccessLost:
                _active.ReleaseFrame();
                FailureCount++;

                if (_recreateAttempts < MaxRecreateAttempts)
                {
                    _recreateAttempts++;
                    RecreateCount++;
                    _active.Recreate();
                    return status;
                }

                if (!IsDegraded)
                {
                    SwitchToFallback();
                }

                return status;

            default:
                _active.ReleaseFrame();
                FailureCount++;

                if (!IsDegraded)
                {
                    SwitchToFallback();
                }

                return status;
        }
    }

    public void ReleaseFrame() => _active.ReleaseFrame();

    private void SwitchToFallback()
    {
        _active = _fallback;
        _recreateAttempts = 0;
        RecreateCount++;
        _active.Recreate();
    }

    /// <summary>
    /// 释放两个实现持有的原生资源（含 Desktop Duplication）。
    /// 设备重建时必须调用，否则旧的 duplication 会一直占着输出。
    /// </summary>
    public void Dispose()
    {
        _active.ReleaseFrame();
        _primary.Dispose();
        _fallback.Dispose();
        GC.SuppressFinalize(this);
    }
}
