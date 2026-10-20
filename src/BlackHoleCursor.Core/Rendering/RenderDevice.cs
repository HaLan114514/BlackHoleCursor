using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>D3D11 设备封装。硬件不可用时（如无显卡的环境）自动退回 WARP 软件渲染。</summary>
public sealed class RenderDevice : IDisposable
{
    public RenderDevice(ID3D11Device device, ID3D11DeviceContext context, FeatureLevel featureLevel, bool usesWarp)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
        UsesWarp = usesWarp;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public FeatureLevel FeatureLevel { get; }

    /// <summary>是否运行在软件渲染（WARP）上。用于主界面提示性能可能不足。</summary>
    public bool UsesWarp { get; }

    public static RenderDevice Create(bool preferHardware = true)
    {
        FeatureLevel[] levels = { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };

        if (preferHardware)
        {
            // fixes-v2 问题 5：必须在**驱动桌面的那块适配器**上建设备。
            // 原来用 IntPtr.Zero（= 适配器 0），双显卡机器上它常常不是驱动显示器的那块，
            // 于是 DXGI Desktop Duplication 直接返回 DXGI_ERROR_UNSUPPORTED，
            // 抓屏只能退化到 GDI BitBlt —— 整屏 1080p 约 40ms，正是抖动的根因。
            using (var displayAdapter = FindDisplayAdapter())
            {
                if (displayAdapter is not null)
                {
                    // ⚠️ 指定了适配器时 DriverType **必须**是 Unknown，
                    // 传 Hardware 会直接返回 E_INVALIDARG（0x80070057）。
                    var onDisplayAdapter = D3D11.D3D11CreateDevice(
                        displayAdapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        levels,
                        out ID3D11Device? deviceOnDisplay,
                        out ID3D11DeviceContext? contextOnDisplay);

                    if (onDisplayAdapter.Success
                        && deviceOnDisplay is not null
                        && contextOnDisplay is not null)
                    {
                        RuntimeLog.Info("已在驱动桌面的适配器上创建 D3D11 设备（为启用 DXGI 抓屏）");
                        return new RenderDevice(deviceOnDisplay, contextOnDisplay, FeatureLevel.Level_11_0, usesWarp: false);
                    }

                    deviceOnDisplay?.Dispose();
                    contextOnDisplay?.Dispose();
                    RuntimeLog.Info(
                        $"在驱动桌面的适配器上建设备失败（0x{onDisplayAdapter.Code:X8}），退回默认适配器");
                }
            }

            var hardwareResult = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                levels,
                out ID3D11Device? hardwareDevice,
                out ID3D11DeviceContext? hardwareContext);

            if (hardwareResult.Success && hardwareDevice is not null && hardwareContext is not null)
            {
                return new RenderDevice(hardwareDevice, hardwareContext, FeatureLevel.Level_11_0, usesWarp: false);
            }
        }

        var warpResult = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? warpDevice,
            out ID3D11DeviceContext? warpContext);

        if (warpResult.Failure || warpDevice is null || warpContext is null)
        {
            throw new InvalidOperationException(
                $"无法创建 Direct3D 11 设备（WARP 返回码 {warpResult.Code}）。请确认显卡驱动正常。");
        }

        return new RenderDevice(warpDevice, warpContext, FeatureLevel.Level_11_0, usesWarp: true);
    }

    /// <summary>
    /// 找到**驱动桌面**的那块 DXGI 适配器：遍历所有适配器，取第一个挂着
    /// <c>AttachedToDesktop</c> 输出的那个。找不到返回 null（由调用方退回默认行为）。
    /// </summary>
    private static IDXGIAdapter1? FindDisplayAdapter()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

            for (uint index = 0; ; index++)
            {
                var result = factory.EnumAdapters1(index, out IDXGIAdapter1? adapter);
                if (result.Failure || adapter is null)
                {
                    return null;
                }

                bool drivesDesktop = false;
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                    if (outputResult.Failure || output is null)
                    {
                        break;
                    }

                    using (output)
                    {
                        if (output.Description.AttachedToDesktop)
                        {
                            drivesDesktop = true;
                            break;
                        }
                    }
                }

                if (drivesDesktop)
                {
                    return adapter;
                }

                adapter.Dispose();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info($"枚举 DXGI 适配器失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
        GC.SuppressFinalize(this);
    }
}
