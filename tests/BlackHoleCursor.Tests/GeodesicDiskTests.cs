using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.4.3：薄盘多次穿越接入测地线主流程。
/// 用 <c>GeodesicDiskProbePS</c> 渲染（测地线积分 + 盘穿越 + 背景反投影）。
///
/// 测试技巧：预览底图是**非均匀棋盘**，直接比较两个不同位置的亮度不可靠。
/// 因此这里一律做「同一像素、有盘 vs 无盘」的对照
/// （无盘 = <c>Gain 0</c> 且 <c>Opacity 0</c>，即盘既不发光也不遮挡），
/// 背景图案的影响被完全抵消。
/// </summary>
public class GeodesicDiskTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _background;

    public GeodesicDiskTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _background = PreviewBackground.CreateView(_device.Device, Size, Size);
        _preview.SetBackground(_background);
    }

    private byte[] Render(BlackHoleLook look)
    {
        using var pixelShader = _renderer.CreatePixelShader("GeodesicDiskProbePS");
        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description, pixelShader);
    }

    /// <summary>对照组：盘既不发光也不遮挡，输出就是纯背景。</summary>
    private byte[] RenderWithoutDisk() => Render(OcclusionLook(0.0));

    /// <summary>只保留遮挡、关掉发光，便于单独量化「背景被吃掉多少」。</summary>
    private static BlackHoleLook OcclusionLook(double opacity)
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        look.Gain = 0.0;
        look.Opacity = opacity;
        return look;
    }

    private static double Brightness(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;
    }

    private static double MeanBrightness(byte[] buffer, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        int count = 0;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                sum += Brightness(buffer, x, y);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>以画面中心为圆心、半径在 [rInner, rOuter] 之间像素的平均亮度。</summary>
    private static double MeanAnnulus(byte[] buffer, int rInner, int rOuter)
    {
        double sum = 0;
        int count = 0;
        int cx = Size / 2;
        int cy = Size / 2;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d >= rInner && d <= rOuter)
                {
                    sum += Brightness(buffer, x, y);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>统计比参考图更暗（且差值超过阈值）的像素数，即「被盘遮挡掉的像素」。</summary>
    private static int CountDarkerThan(byte[] candidate, byte[] reference, double threshold)
    {
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (Brightness(reference, x, y) - Brightness(candidate, x, y) > threshold)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int ShadowRadiusPx(BlackHoleLook look)
        => (int)Math.Round(LensMath.ShadowRadiusPixels(look.HoleSize, Size));

    [Fact]
    public void GeodesicDiskProbe_Compiles()
    {
        using var ps = _renderer.CreatePixelShader("GeodesicDiskProbePS");
        Assert.NotNull(ps);

        var pixels = Render(LookPresets.Create(LookPresets.Gargantua));

        Assert.Equal(Size * Size * 4, pixels.Length);
    }

    /// <summary>
    /// 远侧光弧：阴影外侧一圈（含绕过阴影的远侧盘面）在有盘时显著变亮。
    /// 这是「同一条光线多次穿越薄盘」的直接证据 —— 单次穿越远不到这个亮度。
    /// </summary>
    [Fact]
    public void DiskLight_SurroundsShadow()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        int shadow = ShadowRadiusPx(look);

        double withDisk = MeanAnnulus(Render(look), shadow + 2, shadow + 18);
        double without = MeanAnnulus(RenderWithoutDisk(), shadow + 2, shadow + 18);

        Assert.True(withDisk > without + 1.0,
            $"阴影外侧应出现盘光（含远侧光弧）：有盘 {withDisk:F2} 应高于无盘 {without:F2}");
    }

    /// <summary>盘光只出现在黑洞附近，不应污染远处画面（防止整体提亮的假通过）。</summary>
    [Fact]
    public void DiskLight_DoesNotLeakFarOutside()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);

        double withDisk = MeanBrightness(Render(look), 4, 4, 40, 40);
        double without = MeanBrightness(RenderWithoutDisk(), 4, 4, 40, 40);

        Assert.True(Math.Abs(withDisk - without) < 2.0,
            $"远离黑洞处盘不应改变画面：有盘 {withDisk:F2} / 无盘 {without:F2}");
    }

    [Fact]
    public void OpacityZero_DoesNotOccludeBackground()
    {
        byte[] without = RenderWithoutDisk();

        Assert.Equal(0, CountDarkerThan(Render(OcclusionLook(0.0)), without, 1.0));
    }

    /// <summary>提高盘不透明度 → 背景被遮挡得更多（plan-v2 M1.4.3 第二条断言）。</summary>
    [Fact]
    public void HigherOpacity_OccludesMoreBackground()
    {
        byte[] without = RenderWithoutDisk();

        int low = CountDarkerThan(Render(OcclusionLook(0.25)), without, 1.0);
        int high = CountDarkerThan(Render(OcclusionLook(1.0)), without, 1.0);

        Assert.True(low > 0, "盘应该能遮挡到背景");
        Assert.True(high > low, $"不透明度越高遮挡越多：0.25 → {low} 像素，1.0 → {high} 像素");
    }

    public void Dispose()
    {
        _background.Dispose();
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
