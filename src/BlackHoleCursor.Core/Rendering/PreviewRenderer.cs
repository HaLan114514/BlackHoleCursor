using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 离屏渲染 + 回读到 CPU，供 WPF 预览 / 单元测试使用。
/// 与桌面覆盖层共用同一个 SceneRenderer，保证「所见即所得」。
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly SceneRenderer _sceneRenderer;
    private ID3D11Texture2D _target;
    private ID3D11RenderTargetView _targetView;
    private ID3D11Texture2D _staging;
    private ID3D11ShaderResourceView? _backgroundView;
    private int _width;
    private int _height;
    private bool _disposed;

    public PreviewRenderer(RenderDevice device, SceneRenderer sceneRenderer, int width, int height)
    {
        _device = device.Device;
        _context = device.Context;
        _sceneRenderer = sceneRenderer;
        _width = Math.Max(width, 8);
        _height = Math.Max(height, 8);

        (_target, _targetView) = CreateTarget(_width, _height);
        _staging = CreateStaging(_width, _height);
    }

    public int Width => _width;

    public int Height => _height;

    public void SetBackground(ID3D11ShaderResourceView? backgroundView) => _backgroundView = backgroundView;

    public void Resize(int width, int height)
    {
        int w = Math.Max(width, 8);
        int h = Math.Max(height, 8);
        if (w == _width && h == _height)
        {
            return;
        }

        _targetView.Dispose();
        _target.Dispose();
        _staging.Dispose();

        _width = w;
        _height = h;
        (_target, _targetView) = CreateTarget(w, h);
        _staging = CreateStaging(w, h);
    }

    /// <summary>渲染一帧并回读 BGRA 像素（左上原点，行优先）。</summary>
    public byte[] Render(in SceneDescription description, ID3D11PixelShader? pixelShader = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var constants = SceneParamsBuilder.Build(description);
        _sceneRenderer.Render(
            _context,
            _targetView,
            _backgroundView,
            constants,
            _width,
            _height,
            pixelShader);

        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _target, 0, null);

        MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int bytesPerRow = _width * 4;
            var buffer = new byte[bytesPerRow * _height];
            for (int y = 0; y < _height; y++)
            {
                IntPtr row = IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch);
                Marshal.Copy(row, buffer, y * bytesPerRow, bytesPerRow);
            }

            return buffer;
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private (ID3D11Texture2D, ID3D11RenderTargetView) CreateTarget(int width, int height)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        return (texture, _device.CreateRenderTargetView(texture, null));
    }

    private ID3D11Texture2D CreateStaging(int width, int height)
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targetView.Dispose();
        _target.Dispose();
        _staging.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>预览用的合成底图：棋盘 + 网格，便于观察透镜拉伸与黑洞遮挡。</summary>
public static class PreviewBackground
{
    public static ID3D11Texture2D Create(ID3D11Device device, int width, int height)
    {
        var pixels = new uint[width * height];
        int cell = Math.Max(width / 12, 4);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / cell) + (y / cell)) % 2 == 0;
                byte r = (byte)(dark ? 22 : 40);
                byte g = (byte)(dark ? 26 : 48);
                byte b = (byte)(dark ? 40 : 70);
                pixels[y * width + x] = 0xFF000000 | ((uint)b << 16) | ((uint)g << 8) | r;
            }
        }

        return device.CreateTexture2D(pixels, Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Immutable, CpuAccessFlags.None);
    }

    public static ID3D11ShaderResourceView CreateView(ID3D11Device device, int width, int height)
        => device.CreateShaderResourceView(Create(device, width, height), null);
}
