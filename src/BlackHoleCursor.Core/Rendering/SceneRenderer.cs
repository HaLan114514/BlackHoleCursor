using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 黑洞场景渲染器：一个全屏三角形 + 一个合成像素着色器完成「透镜扭曲背景 + 黑洞本体」。
/// 主界面预览与桌面覆盖层共用同一个渲染器，保证预览与实机效果一致。
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly float[] _staging = new float[SceneConstants.FloatCount];
    private bool _disposed;

    public SceneRenderer(ID3D11Device device)
    {
        _device = device;

        string source = ShaderSources.FullSource;

        ReadOnlyMemory<byte> vertexByteCode = Compile(source, ShaderSources.VertexEntryPoint, ShaderSources.VertexProfile);
        ReadOnlyMemory<byte> pixelByteCode = Compile(source, ShaderSources.PixelEntryPoint, ShaderSources.PixelProfile);

        _vertexShader = device.CreateVertexShader(vertexByteCode.Span, null);
        _pixelShader = device.CreatePixelShader(pixelByteCode.Span, null);

        _constantBuffer = device.CreateBuffer(
            (uint)(SceneConstants.FloatCount * sizeof(float)),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);

        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    private static ReadOnlyMemory<byte> Compile(string source, string entryPoint, string profile)
    {
        try
        {
            return Compiler.Compile(
                ToAscii(source),
                entryPoint,
                "blackhole-scene.hlsl",
                profile,
                ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness,
                EffectFlags.None);
        }
        catch (Exception ex)
        {
            // M6.2：编译失败必须是一个**可识别**的异常 ——
            // 覆盖层线程靠它决定「记录原因 + 恢复系统光标 + 主界面显示渲染不可用」，
            // 而不是拿到一个来历不明的 D3DCompiler 异常。
            throw new ShaderCompilationException(entryPoint, ex);
        }
    }

    /// <summary>
    /// HLSL 编译器只接受单字节字符（注释里的中文会被按本地代码页截断，导致编译失败）。
    /// 这里把非 ASCII 字符替换成空格，保持行号不变以方便报错定位。
    /// </summary>
    private static string ToAscii(string source)
    {
        var buffer = source.ToCharArray();
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] > 127)
            {
                buffer[i] = ' ';
            }
        }

        return new string(buffer);
    }

    /// <summary>
    /// 渲染一帧。<paramref name="background"/> 为 null 时只画黑洞（透镜关闭或无底图场景）。
    /// </summary>
    /// <summary>按入口点编译一个额外的像素着色器（用于着色器工具函数的单元验证）。</summary>
    public ID3D11PixelShader CreatePixelShader(string entryPoint)
        => _device.CreatePixelShader(Compile(ShaderSources.FullSource, entryPoint, ShaderSources.PixelProfile).Span, null);

    public void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        ID3D11ShaderResourceView? background,
        in SceneConstants constants,
        int width,
        int height,
        ID3D11PixelShader? pixelShader = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        float[] values = constants.ToFloatArray();
        Array.Copy(values, _staging, SceneConstants.FloatCount);

        MappedSubresource mapped = context.Map(_constantBuffer, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            Marshal.Copy(_staging, 0, mapped.DataPointer, SceneConstants.FloatCount);
        }
        finally
        {
            context.Unmap(_constantBuffer);
        }

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(pixelShader ?? _pixelShader);
        context.RSSetViewports(new[] { new Viewport(0, 0, width, height, 0.0f, 1.0f) });
        context.OMSetRenderTargets(target, null);

        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetConstantBuffer(0, _constantBuffer);

        if (background is not null)
        {
            context.PSSetShaderResource(0, background);
            context.PSSetSampler(0, _sampler);
        }
        else
        {
            context.PSSetShaderResource(0, null!);
            context.PSSetSampler(0, null!);
        }

        context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
        context.Draw(3, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sampler.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        GC.SuppressFinalize(this);
    }
}
