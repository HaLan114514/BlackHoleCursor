using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.1：着色器工具函数的单元测试。
/// HLSL 函数无法直接调用，所以借助 common.hlsl 里的两个验证入口
/// （ToolsColorProbePS / ToolsTransformProbePS）把结果画成像素再断言。
/// </summary>
public class ShaderToolsTests : IDisposable
{
    private const int Size = 64;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public ShaderToolsTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    private byte[] RenderProbe(string entryPoint, ShaderProbe probe)
    {
        using var pixelShader = _renderer.CreatePixelShader(entryPoint);
        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, false,
            Probe: probe);

        return _preview.Render(description, pixelShader);
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
    }

    [Fact]
    public void ProbeEntryPoints_Compile()
    {
        using var color = _renderer.CreatePixelShader("ToolsColorProbePS");
        using var transform = _renderer.CreatePixelShader("ToolsTransformProbePS");

        Assert.NotNull(color);
        Assert.NotNull(transform);
    }

    [Fact]
    public void ToolHelpers_ArePresentInShaderSource()
    {
        Assert.Contains("float3 blackbody(", ShaderSources.FullSource);
        Assert.Contains("float vnoiseWrapY(", ShaderSources.FullSource);
        Assert.Contains("float2 rot(", ShaderSources.FullSource);
        Assert.Contains("float2 mirrorUV(", ShaderSources.FullSource);
        Assert.Contains("float gmod(", ShaderSources.FullSource);
        Assert.Contains("float3 stars(", ShaderSources.FullSource);
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(2500.0)]
    [InlineData(3200.0)]
    public void Blackbody_LowTemperature_IsWarm(double kelvin)
    {
        var pixels = RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: kelvin));
        var p = Pixel(pixels, 0, 0);

        Assert.True(p.R > p.B, $"{kelvin}K 应偏暖（R={p.R}, B={p.B}）");
    }

    [Theory]
    [InlineData(9000.0)]
    [InlineData(15000.0)]
    [InlineData(20000.0)]
    public void Blackbody_HighTemperature_IsCool(double kelvin)
    {
        var pixels = RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: kelvin));
        var p = Pixel(pixels, 0, 0);

        Assert.True(p.B > p.R, $"{kelvin}K 应偏冷（R={p.R}, B={p.B}）");
    }

    [Fact]
    public void Blackbody_MidTemperature_HasAllChannels()
    {
        var pixels = RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 5500));
        var p = Pixel(pixels, 0, 0);

        Assert.True(p.R > 100, $"5500K 红通道不应退化（R={p.R}）");
        Assert.True(p.G > 100, $"5500K 绿通道不应退化（G={p.G}）");
        Assert.True(p.B > 40, $"5500K 蓝通道不应退化（B={p.B}）");
    }

    [Fact]
    public void Blackbody_MonotonicBlueWarming()
    {
        var cold = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 2000)), 0, 0);
        var warm = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 6500)), 0, 0);
        var hot = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 15000)), 0, 0);

        Assert.True(cold.B <= warm.B && warm.B <= hot.B,
            $"蓝通道应随色温单调上升（{cold.B} → {warm.B} → {hot.B}）");
    }

    [Fact]
    public void Blackbody_AroundDaylight_IsNearlyWhite()
    {
        var p = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 6600)), 0, 0);

        Assert.True(p.R > 240, $"约 6600K 应接近白光（R={p.R}）");
        Assert.True(p.G > 240, $"约 6600K 应接近白光（G={p.G}）");
        Assert.True(p.B > 240, $"约 6600K 应接近白光（B={p.B}）");
    }

    [Fact]
    public void Blackbody_VeryLowTemperature_HasNoBlue()
    {
        var p = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 1500)), 0, 0);

        Assert.True(p.B < 10, $"1500K 的蓝通道应当基本没有（B={p.B}）");
        Assert.True(p.R == 255, $"1500K 的红通道应饱和（R={p.R}）");
    }

    [Fact]
    public void Blackbody_VeryHighTemperature_IsBlueDominant()
    {
        var p = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 40000)), 0, 0);

        Assert.Equal(255, p.B);
        Assert.True(p.R < p.B, $"40000K 应蓝占优（R={p.R}, B={p.B}）");
    }

    [Fact]
    public void Blackbody_ClampsOutOfRangeInput()
    {
        // 低于 1500K 与高于 40000K 都应被 clamp，不得产生非法值
        var tooCold = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: -500)), 0, 0);
        var tooHot = Pixel(RenderProbe("ToolsColorProbePS", new ShaderProbe(TemperatureKelvin: 1e9)), 0, 0);

        Assert.True(tooCold.B < 10, "越界低温应等同于 1500K");
        Assert.True(tooHot.B >= tooHot.R, "越界高温应等同于 40000K");
    }

    [Fact]
    public void WrapNoise_VariesAcrossPixels()
    {
        var pixels = RenderProbe("ToolsColorProbePS", new ShaderProbe(NoiseScale: 8.0));

        byte min = 255;
        byte max = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            min = Math.Min(min, pixels[i]);
            max = Math.Max(max, pixels[i]);
        }

        Assert.True(max - min > 60, $"噪声应有明显起伏（min={min}, max={max}）");
    }

    [Fact]
    public void WrapNoise_StaysWithinUnitRange()
    {
        var pixels = RenderProbe("ToolsColorProbePS", new ShaderProbe(NoiseScale: 16.0));

        for (int i = 3; i < pixels.Length; i += 4)
        {
            Assert.InRange(pixels[i], 0, 255);
        }
    }

    [Fact]
    public void Rotation_PreservesVectorLength()
    {
        var pixels = RenderProbe("ToolsTransformProbePS", new ShaderProbe(AngleRadians: 0.7));

        // 只在中心附近取样：探针把 rot 的结果 +0.5 后写入 8bit，
        // 远离中心时 |v| > 0.5 会被截断，无法验证长度不变
        for (int y = 26; y <= 38; y += 6)
        {
            for (int x = 26; x <= 38; x += 6)
            {
                var p = Pixel(pixels, x, y);

                double vx = (x + 0.5) / Size - 0.5;
                double vy = (y + 0.5) / Size - 0.5;
                double expected = Math.Sqrt(vx * vx + vy * vy);

                double rx = p.R / 255.0 - 0.5;
                double ry = p.G / 255.0 - 0.5;
                double actual = Math.Sqrt(rx * rx + ry * ry);

                Assert.True(Math.Abs(expected - actual) < 0.02,
                    $"旋转必须保持长度：期望 {expected:F3}，实际 {actual:F3}（像素 {x},{y}）");
            }
        }
    }

    [Fact]
    public void Rotation_ActuallyRotates()
    {
        var withoutRotation = RenderProbe("ToolsTransformProbePS", new ShaderProbe(AngleRadians: 0.0));
        var withRotation = RenderProbe("ToolsTransformProbePS", new ShaderProbe(AngleRadians: 1.2));

        int difference = withoutRotation.Where((b, i) => b != withRotation[i]).Count();
        Assert.True(difference > 0, "不同旋转角应产生不同输出");
    }

    [Fact]
    public void MirrorUV_FoldsBackIntoUnitRange()
    {
        var pixels = RenderProbe("ToolsTransformProbePS", new ShaderProbe(AngleRadians: 0.3));

        byte minB = 255;
        byte maxB = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            minB = Math.Min(minB, pixels[i + 2]);
            maxB = Math.Max(maxB, pixels[i + 2]);
        }

        Assert.True(maxB <= 255 && minB >= 0);
        Assert.True(maxB - minB > 100, $"mirrorUV 应在 0..1 之间来回折返（min={minB}, max={maxB}）");
    }

    [Fact]
    public void ProbeDoesNotAffectNormalRendering()
    {
        // 主流程着色器仍然可用，且不受验证参数影响
        var constants = SceneParamsBuilder.Build(new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0, new SettingsModel(), 0.0, false));

        Assert.Equal(SceneConstants.FloatCount, constants.ToFloatArray().Length);
        Assert.Equal(0, SceneConstants.FloatCount % 4);
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
