using System.Drawing;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.3：薄盘穿越着色（ShadeCrossing）的测试。
/// 借助 DiskProbePS 把一次合成穿越的结果画成像素再断言。
/// </summary>
public class DiskShadingTests : IDisposable
{
    private const int Size = 64;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public DiskShadingTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    private byte[] RenderDisk(BlackHoleLook look, double observerAngle = 0.0)
    {
        using var pixelShader = _renderer.CreatePixelShader("DiskProbePS");
        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, false,
            Probe: new ShaderProbe(AngleRadians: observerAngle),
            Look: look);

        return _preview.Render(description, pixelShader);
    }

    /// <summary>uv.x → 像素列：探针把 uv.x 线性映射到穿越半径。</summary>
    private static int ColumnFor(double uvX) => Math.Clamp((int)(uvX * Size), 0, Size - 1);

    /// <summary>某一列（固定半径）上所有方位角的平均亮度与平均密度。</summary>
    private static (double Brightness, double Density) ColumnMean(byte[] buffer, double uvX)
    {
        int x = ColumnFor(uvX);
        double brightness = 0;
        double density = 0;

        for (int y = 0; y < Size; y++)
        {
            int i = (y * Size + x) * 4;
            brightness += (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;
            density += buffer[i + 3];
        }

        return (brightness / Size, density / Size);
    }

    private static (double R, double G, double B) ColumnMeanRgb(byte[] buffer, double uvX)
    {
        int x = ColumnFor(uvX);
        double r = 0;
        double g = 0;
        double b = 0;

        for (int y = 0; y < Size; y++)
        {
            int i = (y * Size + x) * 4;
            r += buffer[i + 2];
            g += buffer[i + 1];
            b += buffer[i];
        }

        return (r / Size, g / Size, b / Size);
    }

    /// <summary>把半径换算成探针的 uv.x（与 DiskProbePS 的映射保持一致）。</summary>
    private static double UvForRadius(double radius, double inner, double outer)
        => (radius - inner * 0.5) / (outer * 1.5 - inner * 0.5);

    private static BlackHoleLook LowGainLook()
    {
        var look = new BlackHoleLook { Gain = 0.2, DopplerMix = 1.0 };
        look.Clamp();
        return look;
    }

    [Fact]
    public void DiskProbe_Compiles()
    {
        using var ps = _renderer.CreatePixelShader("DiskProbePS");

        Assert.NotNull(ps);
        Assert.Contains("float4 ShadeCrossing(", ShaderSources.FullSource);
    }

    [Fact]
    public void InsideTheDisk_EmitsLightAndDensity()
    {
        var pixels = RenderDisk(LowGainLook());
        var (brightness, density) = ColumnMean(pixels, UvForRadius(4.4, 2.2, 7.0));

        Assert.True(brightness > 5, $"盘内应有发光，实测平均亮度 {brightness:F1}");
        Assert.True(density > 5, $"盘内应有密度，实测 {density:F1}");
    }

    [Fact]
    public void InsideTheInnerEdge_IsEmpty()
    {
        var pixels = RenderDisk(LowGainLook());
        var (brightness, density) = ColumnMean(pixels, UvForRadius(1.5, 2.2, 7.0));

        Assert.True(brightness < 1, $"内径以内不应发光，实测 {brightness:F1}");
        Assert.True(density < 1, $"内径以内密度应为 0，实测 {density:F1}");
    }

    [Fact]
    public void OutsideTheOuterEdge_IsEmpty()
    {
        var pixels = RenderDisk(LowGainLook());
        var (brightness, density) = ColumnMean(pixels, UvForRadius(9.5, 2.2, 7.0));

        Assert.True(brightness < 1, $"外径以外不应发光，实测 {brightness:F1}");
        Assert.True(density < 1, $"外径以外密度应为 0，实测 {density:F1}");
    }

    [Fact]
    public void EmissionPeaksNearTheInnerEdge()
    {
        var look = LowGainLook();
        var pixels = RenderDisk(look);

        double nearInner = ColumnMean(pixels, UvForRadius(3.0, 2.2, 7.0)).Brightness;
        double nearOuter = ColumnMean(pixels, UvForRadius(6.4, 2.2, 7.0)).Brightness;

        Assert.True(nearInner > nearOuter,
            $"Shakura-Sunyaev 温度剖面的峰值靠近内径（内侧 {nearInner:F1} vs 外侧 {nearOuter:F1}）");
    }

    /// <summary>
    /// 沿整个环求平均会把「迎面更亮 / 背面更暗」抵消掉，
    /// 所以这里逐像素比对两个相反的观察方向：有 Doppler 时每个方位角都应变化。
    /// </summary>
    private double ObserverAsymmetry(BlackHoleLook look, double radius)
    {
        double radiusUv = UvForRadius(radius, look.InnerRadius, look.OuterRadius);
        int x = ColumnFor(radiusUv);

        var fromA = RenderDisk(look, observerAngle: 0.0);
        var fromB = RenderDisk(look, observerAngle: Math.PI);

        double total = 0;
        for (int y = 0; y < Size; y++)
        {
            int i = (y * Size + x) * 4;
            double a = (fromA[i] + fromA[i + 1] + fromA[i + 2]) / 3.0;
            double b = (fromB[i] + fromB[i + 1] + fromB[i + 2]) / 3.0;
            total += Math.Abs(a - b);
        }

        return total / Size;
    }

    [Fact]
    public void Doppler_MakesTheDiskDirectional()
    {
        var look = LowGainLook();

        double asymmetry = ObserverAsymmetry(look, radius: 4.4);

        Assert.True(asymmetry > 1.0,
            $"开启多普勒后，改变观察方向应改变同一处的发光（实测平均差 {asymmetry:F2}）");
    }

    [Fact]
    public void DopplerMix_ZeroRemovesTheDirectionality()
    {
        var look = LowGainLook();
        look.DopplerMix = 0.0;

        double asymmetry = ObserverAsymmetry(look, radius: 4.4);

        Assert.True(asymmetry < 0.01,
            $"关闭多普勒后 g ≡ 1，画面应与观察方向无关（实测平均差 {asymmetry:F4}）");
    }

    [Fact]
    public void Doppler_IsStrongerNearTheInnerEdge()
    {
        var look = LowGainLook();

        double inner = ObserverAsymmetry(look, radius: 3.0);
        double outer = ObserverAsymmetry(look, radius: 6.4);

        Assert.True(inner > outer,
            $"内圈轨道速度更快，多普勒效应应更强（内侧 {inner:F2} vs 外侧 {outer:F2}）");
    }

    [Fact]
    public void Temperature_DeterminesDiskColor()
    {
        double radiusUv = UvForRadius(4.4, 2.2, 7.0);

        var warm = LowGainLook();
        warm.TempKelvin = 3000;
        var warmRgb = ColumnMeanRgb(RenderDisk(warm), radiusUv);

        var cool = LowGainLook();
        cool.TempKelvin = 16000;
        var coolRgb = ColumnMeanRgb(RenderDisk(cool), radiusUv);

        Assert.True(warmRgb.R > warmRgb.B, $"低温盘应偏红（R={warmRgb.R:F1}, B={warmRgb.B:F1}）");
        Assert.True(coolRgb.B > coolRgb.R, $"高温盘应偏蓝（R={coolRgb.R:F1}, B={coolRgb.B:F1}）");
    }

    [Fact]
    public void Gain_ScalesBrightness()
    {
        var dim = LowGainLook();
        dim.Gain = 0.1;
        double dimBrightness = ColumnMean(RenderDisk(dim), UvForRadius(4.4, 2.2, 7.0)).Brightness;

        var bright = LowGainLook();
        bright.Gain = 2.0;
        double brightBrightness = ColumnMean(RenderDisk(bright), UvForRadius(4.4, 2.2, 7.0)).Brightness;

        Assert.True(brightBrightness > dimBrightness,
            $"亮度增益应提高发光（{dimBrightness:F1} → {brightBrightness:F1}）");
    }

    [Fact]
    public void Tint_ShiftsTheColor()
    {
        double radiusUv = UvForRadius(4.4, 2.2, 7.0);

        var neutral = LowGainLook();
        neutral.Tint = Color.FromArgb(255, 255, 255, 255);
        neutral.TintStrength = 1.0;
        var neutralRgb = ColumnMeanRgb(RenderDisk(neutral), radiusUv);

        var magenta = LowGainLook();
        magenta.Tint = Color.FromArgb(255, 255, 0, 255);
        magenta.TintStrength = 1.0;
        var magentaRgb = ColumnMeanRgb(RenderDisk(magenta), radiusUv);

        Assert.True(magentaRgb.G < neutralRgb.G,
            $"品红染色应压低绿通道（{neutralRgb.G:F1} → {magentaRgb.G:F1}）");
    }

    [Fact]
    public void TintStrength_ZeroLeavesBlackbodyUntouched()
    {
        double radiusUv = UvForRadius(4.4, 2.2, 7.0);

        var plain = LowGainLook();
        plain.TintStrength = 0.0;

        var tinted = LowGainLook();
        tinted.Tint = Color.FromArgb(255, 255, 0, 255);
        tinted.TintStrength = 0.0;

        var a = ColumnMeanRgb(RenderDisk(plain), radiusUv);
        var b = ColumnMeanRgb(RenderDisk(tinted), radiusUv);

        Assert.True(Math.Abs(a.R - b.R) < 1.0 && Math.Abs(a.G - b.G) < 1.0,
            "染色强度为 0 时不应改变颜色");
    }

    [Fact]
    public void Streaks_VaryAlongTheOrbit()
    {
        var pixels = RenderDisk(LowGainLook());
        int x = ColumnFor(UvForRadius(4.4, 2.2, 7.0));

        double min = double.MaxValue;
        double max = 0;
        for (int y = 0; y < Size; y++)
        {
            int i = (y * Size + x) * 4;
            double value = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.True(max - min > 3, $"盘面应有条纹起伏（{min:F1} … {max:F1}）");
    }

    [Fact]
    public void ReverseSpeed_StillEmits()
    {
        var look = LowGainLook();
        look.Speed = -5.0;

        var (brightness, _) = ColumnMean(RenderDisk(look), UvForRadius(4.4, 2.2, 7.0));

        Assert.True(brightness > 5, $"反向公转时盘也应发光，实测 {brightness:F1}");
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
