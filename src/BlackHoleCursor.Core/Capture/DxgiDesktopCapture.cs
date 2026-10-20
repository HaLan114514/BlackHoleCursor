using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BlackHoleCursor.Core.Capture;

/// <summary>
/// 主抓屏方案：DXGI Desktop Duplication。性能最好，只拷贝需要的局部区域。
/// </summary>
public sealed class DxgiDesktopCapture : IDesktopCapture
{
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int WaitTimeout = unchecked((int)0x887A0027);

    private readonly ID3D11Device _device;
    private IDXGIOutputDuplication? _duplication;
    private IDXGIResource? _frame;
    private Vortice.RawRect _outputBounds;
    private bool _disposed;

    public DxgiDesktopCapture(ID3D11Device device)
    {
        _device = device;
        Recreate();
    }

    public string Name => "DXGI Desktop Duplication";

    public bool IsAvailable => _duplication is not null;

    /// <summary>指定坐标所在显示器的桌面矩形（用于把虚拟坐标转成输出内坐标）。</summary>
    public Vortice.RawRect OutputBounds => _outputBounds;

    public void Recreate()
    {
        ReleaseDuplication();

        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();

            int outputIndex = 0;
            int attached = 0;

            while (true)
            {
                var result = adapter.EnumOutputs((uint)outputIndex, out IDXGIOutput? output);
                if (result.Failure || output is null)
                {
                    break;
                }

                using (output)
                {
                    var description = output.Description;
                    if (!description.AttachedToDesktop)
                    {
                        outputIndex++;
                        continue;
                    }

                    attached++;
                    _outputBounds = description.DesktopCoordinates;

                    // 不能只在第一个输出上试一次就放弃：双显卡（核显 + 独显）机器上，
                    // 常出现「建设备的适配器」与「驱动桌面的适配器」不是同一个，
                    // 此时 DuplicateOutput 会失败，但换下一个输出可能就成功。
                    // 失败原因必须打进日志 —— 原来静默吞异常，根本无从排查
                    // （fixes-v2 问题 5：抓屏贵是抖动的根因，DXGI 用不了才退化到 GDI）。
                    try
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _duplication = output1.DuplicateOutput(_device);
                        RuntimeLog.Info(
                            $"DXGI 抓屏已就绪：输出 #{outputIndex} {description.DeviceName} " +
                            $"{_outputBounds.Left},{_outputBounds.Top} {_outputBounds.Right - _outputBounds.Left}" +
                            $"x{_outputBounds.Bottom - _outputBounds.Top}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _duplication = null;
                        RuntimeLog.Info(
                            $"DXGI 抓屏在输出 #{outputIndex} {description.DeviceName} 上失败：" +
                            $"{ex.GetType().Name} HResult=0x{ex.HResult:X8} {ex.Message}");
                    }
                }

                outputIndex++;
            }

            RuntimeLog.Info(
                $"DXGI 抓屏不可用：枚举到 {attached} 个已连接输出，全部无法 DuplicateOutput，将降级为 GDI");
        }
        catch (Exception ex)
        {
            _duplication = null;
            RuntimeLog.Info($"DXGI 抓屏初始化异常：{ex.GetType().Name} HResult=0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>切换到覆盖指定坐标的输出（多显示器场景）。</summary>
    public void SelectOutput(int centerX, int centerY)
    {
        if (_duplication is not null &&
            centerX >= _outputBounds.Left && centerX < _outputBounds.Right &&
            centerY >= _outputBounds.Top && centerY < _outputBounds.Bottom)
        {
            return;
        }

        ReleaseDuplication();

        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();

            int outputIndex = 0;
            while (true)
            {
                var result = adapter.EnumOutputs((uint)outputIndex, out IDXGIOutput? output);
                if (result.Failure || output is null)
                {
                    break;
                }

                using (output)
                {
                    var description = output.Description;
                    var bounds = description.DesktopCoordinates;

                    if (!description.AttachedToDesktop ||
                        centerX < bounds.Left || centerX >= bounds.Right ||
                        centerY < bounds.Top || centerY >= bounds.Bottom)
                    {
                        outputIndex++;
                        continue;
                    }

                    _outputBounds = bounds;
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    _duplication = output1.DuplicateOutput(_device);
                    return;
                }
            }
        }
        catch (Exception)
        {
            _duplication = null;
        }

        // 目标坐标不属于任何输出时退回主输出
        Recreate();
    }

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
        if (_duplication is null)
        {
            return CaptureStatus.Unavailable;
        }

        var result = _duplication.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result.Failure || resource is null)
        {
            resource?.Dispose();
            int code = result.Code;
            if (code == WaitTimeout)
            {
                return CaptureStatus.Timeout;
            }

            if (code == AccessLost)
            {
                return CaptureStatus.AccessLost;
            }

            return CaptureStatus.Unavailable;
        }

        _frame = resource;

        using var desktopTexture = resource.QueryInterface<ID3D11Texture2D>();
        int sourceLeft = left - _outputBounds.Left;
        int sourceTop = top - _outputBounds.Top;

        // 与显示器边界求交，避免 Box 越界导致拷贝失败
        int x = Math.Max(sourceLeft, 0);
        int y = Math.Max(sourceTop, 0);
        int right = Math.Min(sourceLeft + width, _outputBounds.Right - _outputBounds.Left);
        int bottom = Math.Min(sourceTop + height, _outputBounds.Bottom - _outputBounds.Top);

        if (right <= x || bottom <= y)
        {
            return CaptureStatus.Unavailable;
        }

        context.CopySubresourceRegion(
            destination,
            0,
            (uint)(destX + (x - sourceLeft)),
            (uint)(destY + (y - sourceTop)),
            0,
            desktopTexture,
            0,
            new Box(x, y, 0, right, bottom, 1));

        return CaptureStatus.Success;
    }

    public void ReleaseFrame()
    {
        if (_frame is null)
        {
            return;
        }

        try
        {
            _duplication?.ReleaseFrame();
        }
        catch (Exception)
        {
            // 帧已失效，忽略
        }

        _frame.Dispose();
        _frame = null;
    }

    private void ReleaseDuplication()
    {
        ReleaseFrame();

        if (_duplication is not null)
        {
            _duplication.Dispose();
            _duplication = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseDuplication();
        GC.SuppressFinalize(this);
    }
}
